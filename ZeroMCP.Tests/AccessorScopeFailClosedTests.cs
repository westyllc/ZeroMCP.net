using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZeroMCP.Extensions;

namespace ZeroMCP.Tests;

// ---------------------------------------------------------------------------
// Patch 3: when IHttpContextAccessor cannot be pointed at the synthetic request (here: a custom,
// non-framework accessor), dispatch is REFUSED by default. The insecure opt-out lets it proceed with the
// accessor still resolving to the OUTER /mcp request — the tests below pin that hazard explicitly.
// ---------------------------------------------------------------------------

/// <summary>A decorator over the framework accessor. Not an HttpContextAccessor, so ZeroMCP cannot swap it.</summary>
public sealed class DecoratingHttpContextAccessor(HttpContextAccessor inner) : IHttpContextAccessor
{
    public HttpContext? HttpContext
    {
        get => inner.HttpContext;
        set => inner.HttpContext = value;
    }
}

/// <summary>Counts how often the tool's endpoint delegate actually ran.</summary>
public sealed class InvocationCounter
{
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public void Hit() => Interlocked.Increment(ref _count);
}

public abstract class CustomAccessorHostBase : IAsyncLifetime
{
    private WebApplication? _app;
    public HttpClient Client { get; private set; } = default!;
    public IServiceProvider Services => _app!.Services;

    protected abstract bool AllowDegradedAccessorDispatch { get; }

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSingleton<IHttpContextAccessor>(new DecoratingHttpContextAccessor(new HttpContextAccessor()));
        builder.Services.AddSingleton<InvocationCounter>();
        builder.Services.AddAuthentication(TxnTestAuthHandler.Scheme)
            .AddScheme<AuthenticationSchemeOptions, TxnTestAuthHandler>(TxnTestAuthHandler.Scheme, _ => { });
        builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, TransactionAccessHandler>();
        builder.Services.AddAuthorization(o =>
            o.AddPolicy("CustomerTransactionAccess", p => p.RequireAuthenticatedUser().AddRequirements(new TransactionAccessRequirement())));
        builder.Services.AddZeroMCP(o =>
        {
            o.ServerName = "Custom Accessor Test";
            o.EnableResultEnrichment = true;
            o.AllowDegradedAccessorDispatch = AllowDegradedAccessorDispatch;
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseRouting();
        app.UseAuthorization();

        app.MapGet("/api/txns/{txnId}", (string txnId, IHttpContextAccessor accessor, InvocationCounter counter) =>
            {
                counter.Hit();
                return Results.Ok(new { txnId, accessorPath = accessor.HttpContext?.Request.Path.Value });
            })
            .RequireAuthorization("CustomerTransactionAccess")
            .AsMcp("get_txn", "Get a customer transaction.");

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

    internal static async Task<JsonObject> CallToolAsync(HttpClient client, string url, string name, object arguments, string allowed)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } });
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Txn-Allowed", allowed);
        var response = await client.SendAsync(request);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    internal static bool IsError(JsonObject r) => r["result"]!.AsObject()["isError"]!.GetValue<bool>();
    internal static int StatusCode(JsonObject r) => r["result"]!.AsObject()["metadata"]!.AsObject()["statusCode"]!.GetValue<int>();
    internal static string Text(JsonObject r) => r["result"]!.AsObject()["content"]!.AsArray()[0]!.AsObject()["text"]!.GetValue<string>();
}

public sealed class CustomAccessorDefaultHost : CustomAccessorHostBase
{
    protected override bool AllowDegradedAccessorDispatch => false;
}

public sealed class CustomAccessorDegradedHost : CustomAccessorHostBase
{
    protected override bool AllowDegradedAccessorDispatch => true;
}

public sealed class CustomAccessorRefusalTests(CustomAccessorDefaultHost host) : IClassFixture<CustomAccessorDefaultHost>
{
    [Fact]
    public async Task CustomAccessor_DefaultOptions_DispatchRefused500_HandlerNotInvoked()
    {
        var counter = host.Services.GetRequiredService<InvocationCounter>();
        var before = counter.Count;

        // The caller owns T1 — authorization would pass — yet the dispatch must still be refused.
        var r = await CustomAccessorHostBase.CallToolAsync(host.Client, "/mcp", "get_txn", new { txnId = "T1" }, allowed: "T1");

        CustomAccessorHostBase.IsError(r).Should().BeTrue();
        CustomAccessorHostBase.StatusCode(r).Should().Be(500);
        var text = CustomAccessorHostBase.Text(r);
        text.Should().Contain("refused");
        text.Should().Contain(typeof(DecoratingHttpContextAccessor).FullName!);
        text.Should().Contain("AllowDegradedAccessorDispatch");
        counter.Count.Should().Be(before, "the tool must not run when the accessor cannot be pointed at the synthetic request");
    }
}

public sealed class CustomAccessorDegradedTests(CustomAccessorDegradedHost host) : IClassFixture<CustomAccessorDegradedHost>
{
    [Fact]
    public async Task OptOut_DispatchProceeds_ButAccessorSeesOuterRequest()
    {
        var counter = host.Services.GetRequiredService<InvocationCounter>();
        var before = counter.Count;

        // Outer query names the owned txn so authorization (which reads the accessor) passes.
        var r = await CustomAccessorHostBase.CallToolAsync(host.Client, "/mcp?txnId=T1", "get_txn", new { txnId = "T1" }, allowed: "T1");

        CustomAccessorHostBase.IsError(r).Should().BeFalse(CustomAccessorHostBase.Text(r));
        counter.Count.Should().Be(before + 1);
        var echo = JsonDocument.Parse(CustomAccessorHostBase.Text(r)).RootElement;
        echo.GetProperty("accessorPath").GetString().Should().Be("/mcp",
            "with the opt-out the accessor is NOT swapped: it still resolves to the outer MCP request");
    }

    [Fact]
    public async Task OptOut_Hazard_OuterQueryStringSwaysAuthorization()
    {
        // This is WHY the default refuses: the requirement reads the accessor, the accessor is the outer request,
        // so the caller's own query string (?txnId=T1, owned) authorizes access to T2, which it does NOT own.
        var r = await CustomAccessorHostBase.CallToolAsync(host.Client, "/mcp?txnId=T1", "get_txn", new { txnId = "T2" }, allowed: "T1");

        CustomAccessorHostBase.IsError(r).Should().BeFalse(CustomAccessorHostBase.Text(r));
        JsonDocument.Parse(CustomAccessorHostBase.Text(r)).RootElement.GetProperty("txnId").GetString().Should().Be("T2");
    }
}
