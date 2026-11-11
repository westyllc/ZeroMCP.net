using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using ZeroMCP.Dispatch;
using ZeroMCP.Extensions;
using ZeroMCP.Options;

namespace ZeroMCP.Tests;

// ---------------------------------------------------------------------------
// Patch 2: during tool dispatch IHttpContextAccessor.HttpContext must be the SYNTHETIC request.
// Mirrors the consumer's CustomerTransactionAccess requirement: the handler reads the transaction id
// only through IHttpContextAccessor (route values or query), never through the authorization resource.
// ---------------------------------------------------------------------------

public sealed class TransactionAccessRequirement : IAuthorizationRequirement;

/// <summary>Succeeds when the transaction id on the CURRENT request (per IHttpContextAccessor) is one the caller owns.</summary>
public sealed class TransactionAccessHandler(IHttpContextAccessor accessor) : AuthorizationHandler<TransactionAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TransactionAccessRequirement requirement)
    {
        var request = accessor.HttpContext?.Request;
        var txnId = request?.RouteValues["txnId"]?.ToString();
        if (string.IsNullOrEmpty(txnId))
            txnId = request?.Query["txnId"].ToString();

        if (!string.IsNullOrEmpty(txnId) && context.User.HasClaim("txn", txnId))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>Authenticates every request; grants one "txn" claim per comma-separated value of X-Txn-Allowed.</summary>
public sealed class TxnTestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "TxnTest";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "customer") };
        foreach (var t in Request.Headers["X-Txn-Allowed"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
            claims.Add(new Claim("txn", t.Trim()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
    }
}

/// <summary>Records, after the /mcp pipeline returns, whether the accessor still resolves to the outer request.</summary>
public sealed class OuterAccessorProbe
{
    public ConcurrentQueue<bool> OuterSeenAfterDispatch { get; } = new();

    /// <summary>Same check, keyed by the X-Probe-Id header so concurrent requests can be told apart.</summary>
    public ConcurrentDictionary<string, bool> OuterSeenByProbeId { get; } = new();
}

/// <summary>
/// Holds every armed dispatch inside its endpoint until all N have arrived, so the N accessor scopes are
/// provably live at the same time (deterministic overlap, no sleeps). Unarmed: passes straight through.
/// </summary>
public sealed class DispatchRendezvous
{
    private readonly object _gate = new();
    private int _expected;
    private int _arrived;
    private TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Arm(int participants)
    {
        lock (_gate)
        {
            _expected = participants;
            _arrived = 0;
            _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public Task ArriveAsync()
    {
        lock (_gate)
        {
            if (_expected == 0) return Task.CompletedTask;
            if (++_arrived == _expected) _allArrived.TrySetResult();
            // Fail loudly (instead of hanging the suite) if dispatches were serialized and never overlap.
            return _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}

public sealed class HttpContextAccessorHost : IAsyncLifetime
{
    private WebApplication? _app;
    public HttpClient Client { get; private set; } = default!;
    public IServiceProvider Services => _app!.Services;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<OuterAccessorProbe>();
        builder.Services.AddSingleton<DispatchRendezvous>();
        builder.Services.AddAuthentication(TxnTestAuthHandler.Scheme)
            .AddScheme<AuthenticationSchemeOptions, TxnTestAuthHandler>(TxnTestAuthHandler.Scheme, _ => { });
        builder.Services.AddSingleton<IAuthorizationHandler, TransactionAccessHandler>();
        builder.Services.AddAuthorization(o =>
            o.AddPolicy("CustomerTransactionAccess", p => p.RequireAuthenticatedUser().AddRequirements(new TransactionAccessRequirement())));
        builder.Services.AddZeroMCP(o =>
        {
            o.ServerName = "Accessor Test";
            o.EnableResultEnrichment = true; // surfaces metadata.statusCode
        });

        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            await next();
            if (ctx.Request.Path.StartsWithSegments("/mcp"))
            {
                var accessor = ctx.RequestServices.GetRequiredService<IHttpContextAccessor>();
                var probe = ctx.RequestServices.GetRequiredService<OuterAccessorProbe>();
                var outerIntact = ReferenceEquals(accessor.HttpContext, ctx);
                probe.OuterSeenAfterDispatch.Enqueue(outerIntact);
                if (ctx.Request.Headers["X-Probe-Id"].ToString() is { Length: > 0 } probeId)
                    probe.OuterSeenByProbeId[probeId] = outerIntact;
            }
        });
        app.UseAuthentication();
        app.UseRouting();
        app.UseAuthorization();

        // Route value read by the requirement.
        app.MapGet("/api/txns/{txnId}", (string txnId, IHttpContextAccessor accessor, HttpContext http) =>
                Results.Ok(new { txnId, accessorPath = accessor.HttpContext?.Request.Path.Value, accessorIsRequest = ReferenceEquals(accessor.HttpContext, http) }))
            .RequireAuthorization("CustomerTransactionAccess")
            .AsMcp("get_txn", "Get a customer transaction.");

        // Query value read by the requirement.
        app.MapGet("/api/txn-lookup", (string? txnId) => Results.Ok(new { txnId }))
            .RequireAuthorization("CustomerTransactionAccess")
            .AsMcp("lookup_txn", "Look up a customer transaction by query.");

        // Concurrency: reads the accessor before and after a rendezvous that holds all parallel dispatches open together.
        app.MapGet("/api/txns-gated/{txnId}", async (string txnId, IHttpContextAccessor accessor, HttpContext http, DispatchRendezvous rendezvous) =>
            {
                var before = accessor.HttpContext;
                await rendezvous.ArriveAsync();
                var after = accessor.HttpContext;
                return Results.Ok(new
                {
                    txnId,
                    beforePath = before?.Request.Path.Value,
                    afterPath = after?.Request.Path.Value,
                    afterUserTxn = after?.User.FindFirst("txn")?.Value,
                    beforeIsRequest = ReferenceEquals(before, http),
                    afterIsRequest = ReferenceEquals(after, http),
                });
            })
            .RequireAuthorization("CustomerTransactionAccess")
            .AsMcp("get_txn_gated", "Get a customer transaction (test rendezvous).");

        app.UseEndpoints(_ => { });
        app.MapZeroMCP();

        await app.StartAsync();
        _app = app;
        Client = app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_app is not null)
            await _app.DisposeAsync();
    }
}

public sealed class HttpContextAccessorDispatchTests(HttpContextAccessorHost host) : IClassFixture<HttpContextAccessorHost>
{
    private readonly HttpClient _client = host.Client;
    private readonly OuterAccessorProbe _probe = host.Services.GetRequiredService<OuterAccessorProbe>();

    [Fact]
    public async Task RouteValue_RequirementReadsAccessor_CallerOwningTxn_Passes()
    {
        var response = await CallToolAsync("get_txn", new { txnId = "T1" }, allowed: "T1");

        IsError(response).Should().BeFalse(Text(response));
        var echo = JsonDocument.Parse(Text(response)).RootElement;
        echo.GetProperty("txnId").GetString().Should().Be("T1");
        echo.GetProperty("accessorPath").GetString().Should().Be("/api/txns/T1");
        echo.GetProperty("accessorIsRequest").GetBoolean().Should().BeTrue("the action must see the synthetic request through the accessor");
        AssertOuterUntouched();
    }

    [Fact]
    public async Task RouteValue_RequirementReadsAccessor_CallerNotOwningTxn_Is403()
    {
        var response = await CallToolAsync("get_txn", new { txnId = "T2" }, allowed: "T1");

        IsError(response).Should().BeTrue();
        StatusCode(response).Should().Be(403);
        AssertOuterUntouched();
    }

    [Fact]
    public async Task QueryValue_RequirementReadsAccessor_Present_Passes()
    {
        var response = await CallToolAsync("lookup_txn", new { txnId = "T1" }, allowed: "T1");

        IsError(response).Should().BeFalse(Text(response));
        JsonDocument.Parse(Text(response)).RootElement.GetProperty("txnId").GetString().Should().Be("T1");
        AssertOuterUntouched();
    }

    [Fact]
    public async Task QueryValue_RequirementReadsAccessor_Missing_Is403()
    {
        var response = await CallToolAsync("lookup_txn", new { }, allowed: "T1");

        IsError(response).Should().BeTrue();
        StatusCode(response).Should().Be(403);
        AssertOuterUntouched();
    }

    [Fact]
    public async Task ParallelDispatches_EachSeeOwnSyntheticContext_AndOuterRequestsStayIntact()
    {
        const int n = 12;
        var rendezvous = host.Services.GetRequiredService<DispatchRendezvous>();
        rendezvous.Arm(n);
        try
        {
            // Caller i owns only txn "P{i}" and asks for it. If the authorization handler or the action read
            // another flow's synthetic context, the route value would not match the caller's claim (403) or the
            // echoed path/user would belong to a different caller.
            var calls = Enumerable.Range(0, n).Select(i =>
            {
                var txn = $"P{i}";
                return CallToolAsync("get_txn_gated", new { txnId = txn }, allowed: txn, probeId: $"probe-{i}");
            }).ToArray();
            var responses = await Task.WhenAll(calls);

            for (var i = 0; i < n; i++)
            {
                var txn = $"P{i}";
                var r = responses[i];
                IsError(r).Should().BeFalse($"caller {i}: {Text(r)}");
                var echo = JsonDocument.Parse(Text(r)).RootElement;
                echo.GetProperty("txnId").GetString().Should().Be(txn);
                echo.GetProperty("beforePath").GetString().Should().Be($"/api/txns-gated/{txn}");
                echo.GetProperty("afterPath").GetString().Should().Be($"/api/txns-gated/{txn}", "after the rendezvous all scopes overlapped; caller {0} must still see its own request", i);
                echo.GetProperty("afterUserTxn").GetString().Should().Be(txn);
                echo.GetProperty("beforeIsRequest").GetBoolean().Should().BeTrue();
                echo.GetProperty("afterIsRequest").GetBoolean().Should().BeTrue();

                _probe.OuterSeenByProbeId.TryGetValue($"probe-{i}", out var outerIntact).Should().BeTrue();
                outerIntact.Should().BeTrue($"outer /mcp request {i} must still be what IHttpContextAccessor returns after dispatch");
            }
        }
        finally
        {
            rendezvous.Arm(0);
        }
    }

    private void AssertOuterUntouched()
    {
        var seen = _probe.OuterSeenAfterDispatch.ToArray();
        seen.Should().NotBeEmpty();
        seen[^1].Should().BeTrue("after dispatch the outer /mcp request must still be what IHttpContextAccessor returns");
    }

    private async Task<JsonObject> CallToolAsync(string name, object arguments, string allowed, string? probeId = null)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Txn-Allowed", allowed);
        if (probeId is not null)
            request.Headers.Add("X-Probe-Id", probeId);
        var response = await _client.SendAsync(request);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static bool IsError(JsonObject r) => r["result"]!.AsObject()["isError"]!.GetValue<bool>();
    private static int StatusCode(JsonObject r) => r["result"]!.AsObject()["metadata"]!.AsObject()["statusCode"]!.GetValue<int>();
    private static string Text(JsonObject r) => r["result"]!.AsObject()["content"]!.AsArray()[0]!.AsObject()["text"]!.GetValue<string>();
}

// ---------------------------------------------------------------------------
// Unit tests: swap/restore semantics of SyntheticHttpContextFactory.EnterHttpContextAccessorScope
// ---------------------------------------------------------------------------

public sealed class HttpContextAccessorScopeTests
{
    private sealed class CustomAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static SyntheticHttpContextFactory Factory()
    {
        var sp = new ServiceCollection().AddOptions().BuildServiceProvider();
        return new SyntheticHttpContextFactory(new DefaultHttpContextFactory(sp), sp, Microsoft.Extensions.Options.Options.Create(new ZeroMCPOptions()));
    }

    private static IServiceProvider ServicesWith(IHttpContextAccessor accessor) =>
        new ServiceCollection().AddSingleton(accessor).BuildServiceProvider();

    [Fact]
    public void FrameworkInternals_AreRecognised()
    {
        // Tripwire: if a framework update renames HttpContextAccessor internals, the swap silently becomes a no-op.
        HttpContextAccessorScope.IsSupported.Should().BeTrue();
    }

    [Fact]
    public async Task FrameworkAccessor_SeesSynthetic_InsideScope_AndOuterIsIntactAfterwards()
    {
        var accessor = new HttpContextAccessor();
        var outer = new DefaultHttpContext();
        var synthetic = new DefaultHttpContext();
        accessor.HttpContext = outer;
        var factory = Factory();
        var services = ServicesWith(accessor);

        HttpContext? seenInside = null;
        HttpContext? seenInForkedFlowAfterDispose = null;
        Task forked = Task.CompletedTask;

        // Dispatch runs in its own async method, exactly like McpToolDispatcher.DispatchAsync.
        async Task DispatchLikeAsync()
        {
            using var scope = factory.EnterHttpContextAccessorScope(synthetic, services);
            await Task.Yield();
            seenInside = accessor.HttpContext;
            var gate = new TaskCompletionSource();
            forked = Task.Run(async () => { await gate.Task; seenInForkedFlowAfterDispose = accessor.HttpContext; });
            scope.Dispose();
            gate.SetResult();
        }

        await DispatchLikeAsync();
        await forked;

        seenInside.Should().BeSameAs(synthetic);
        seenInForkedFlowAfterDispose.Should().BeNull("work forked during dispatch must not keep the finished synthetic context");
        accessor.HttpContext.Should().BeSameAs(outer, "the outer request's accessor value must be untouched after dispatch");
    }

    [Fact]
    public void Dispose_RestoresPreviousValue_InSameFlow()
    {
        var accessor = new HttpContextAccessor();
        var outer = new DefaultHttpContext();
        accessor.HttpContext = outer;

        var scope = Factory().EnterHttpContextAccessorScope(new DefaultHttpContext(), ServicesWith(accessor));
        accessor.HttpContext.Should().NotBeSameAs(outer);
        scope.Dispose();
        accessor.HttpContext.Should().BeSameAs(outer);
    }

    [Fact]
    public void CustomAccessor_IsLeftAlone()
    {
        var custom = new CustomAccessor();
        var outer = new DefaultHttpContext();
        custom.HttpContext = outer;

        using (Factory().EnterHttpContextAccessorScope(new DefaultHttpContext(), ServicesWith(custom)))
            custom.HttpContext.Should().BeSameAs(outer);
        custom.HttpContext.Should().BeSameAs(outer);
    }

    [Fact]
    public void NoAccessorRegistered_IsNoop()
    {
        var act = () => Factory().EnterHttpContextAccessorScope(new DefaultHttpContext(), new ServiceCollection().BuildServiceProvider()).Dispose();
        act.Should().NotThrow();
    }
}
