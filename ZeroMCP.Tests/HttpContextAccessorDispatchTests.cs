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
                ctx.RequestServices.GetRequiredService<OuterAccessorProbe>()
                    .OuterSeenAfterDispatch.Enqueue(ReferenceEquals(accessor.HttpContext, ctx));
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

    private void AssertOuterUntouched()
    {
        var seen = _probe.OuterSeenAfterDispatch.ToArray();
        seen.Should().NotBeEmpty();
        seen[^1].Should().BeTrue("after dispatch the outer /mcp request must still be what IHttpContextAccessor returns");
    }

    private async Task<JsonObject> CallToolAsync(string name, object arguments, string allowed)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Txn-Allowed", allowed);
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
