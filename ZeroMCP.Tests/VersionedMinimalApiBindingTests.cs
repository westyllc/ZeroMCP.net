using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asp.Versioning;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using ZeroMCP.Discovery;
using ZeroMCP.Dispatch;
using ZeroMCP.Extensions;
using ZeroMCP.Options;

namespace ZeroMCP.Tests;

// ---------------------------------------------------------------------------
// Test host: minimal APIs on an Asp.Versioning group (api/v{version:apiVersion}/...),
// with SubstituteApiVersionInUrl so ApiExplorer reports api/v1/... paths.
// ---------------------------------------------------------------------------

public sealed class CreateWidgetRequest
{
    [Required] public string Name { get; set; } = default!;
    [Required] public int Quantity { get; set; }
    public string? Note { get; set; }
}

public sealed class UpdateWidgetRequest
{
    public string? Note { get; set; }
    public bool? Archived { get; set; }
}

public sealed class VersionedMinimalApiHost : IAsyncLifetime
{
    private WebApplication? _app;
    public HttpClient Client { get; private set; } = default!;
    public IServiceProvider Services => _app!.Services;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services
            .AddApiVersioning(o =>
            {
                o.DefaultApiVersion = new ApiVersion(1, 0);
                o.AssumeDefaultVersionWhenUnspecified = true;
            })
            .AddApiExplorer(o =>
            {
                o.GroupNameFormat = "'v'VVV";
                o.SubstituteApiVersionInUrl = true;
            });
        builder.Services.AddZeroMCP(o =>
        {
            o.ServerName = "Versioned Test";
            o.EnableToolInspector = true;
        });

        var app = builder.Build();
        app.UseRouting();

        var versions = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1, 0)).Build();
        var widgets = app.MapGroup("/api/v{version:apiVersion}/widgets").WithApiVersionSet(versions);

        // T1: required [FromBody] complex type
        widgets.MapPost("/", (CreateWidgetRequest req) =>
                Results.Ok(new { echoed = true, req.Name, req.Quantity, req.Note }))
            .AsMcp("v_create_widget", "Create a widget (versioned).");

        // T2: body whose properties are all optional (the silent-default regression)
        widgets.MapPatch("/{id}", (int id, UpdateWidgetRequest req) =>
                Results.Ok(new { id, req.Note, req.Archived }))
            .AsMcp("v_update_widget", "Update a widget (versioned).");

        // T3: versioned GET with query params
        widgets.MapGet("/", (string? color, int limit = 10) =>
                Results.Ok(new { color, limit }))
            .AsMcp("v_list_widgets", "List widgets (versioned).");

        // T5 (end-to-end): unsafe method whose ApiDescription cannot be resolved
        app.MapPost("/api/hidden/widgets", (CreateWidgetRequest req) => Results.Ok(new { req.Name }))
            .ExcludeFromDescription()
            .AsMcp("hidden_create_widget", "Create a widget on an endpoint excluded from ApiExplorer.");

        // T4: unversioned sanity in the same host
        app.MapPost("/api/plain/widgets", (CreateWidgetRequest req) =>
                Results.Ok(new { req.Name, req.Quantity, req.Note }))
            .AsMcp("plain_create_widget", "Create a widget (unversioned).");

        // Register the app's endpoint data sources with routing BEFORE MapZeroMCP() pre-warms discovery,
        // so both ZeroMCP's registry and ApiExplorer's cached description collection see the minimal APIs.
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

public sealed class VersionedMinimalApiBindingTests : IClassFixture<VersionedMinimalApiHost>
{
    private readonly VersionedMinimalApiHost _host;
    private readonly HttpClient _client;

    public VersionedMinimalApiBindingTests(VersionedMinimalApiHost host)
    {
        _host = host;
        _client = host.Client;
    }

    [Fact]
    public void Discovery_VersionedMinimalEndpoints_ResolveApiDescription()
    {
        var discovery = _host.Services.GetRequiredService<McpToolDiscoveryService>();
        discovery.GetTool("v_create_widget")!.ApiDescriptionResolved.Should().BeTrue();
        discovery.GetTool("v_update_widget")!.ApiDescriptionResolved.Should().BeTrue();
        discovery.GetTool("v_list_widgets")!.ApiDescriptionResolved.Should().BeTrue();
        discovery.GetTool("plain_create_widget")!.ApiDescriptionResolved.Should().BeTrue();
        discovery.GetTool("hidden_create_widget")!.ApiDescriptionResolved.Should().BeFalse();
    }

    // T1
    [Fact]
    public async Task T1_VersionedPost_RequiredBody_SchemaAdvertisesBody_AndCallBinds()
    {
        var tool = await GetToolAsync("v_create_widget");
        var schema = tool["inputSchema"]!.AsObject();
        var props = schema["properties"]!.AsObject();
        props.Should().HaveProperty("name");
        props.Should().HaveProperty("quantity");
        props.Should().HaveProperty("note");
        var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        required.Should().Contain("name");
        required.Should().Contain("quantity");
        required.Should().NotContain("note");

        var response = await CallToolAsync("v_create_widget", new { version = "1", name = "Sprocket", quantity = 7, note = "rush" });
        AssertSuccess(response);
        var echo = JsonDocument.Parse(ExtractText(response)).RootElement;
        echo.GetProperty("echoed").GetBoolean().Should().BeTrue();
        echo.GetProperty("name").GetString().Should().Be("Sprocket");
        echo.GetProperty("quantity").GetInt32().Should().Be(7);
        echo.GetProperty("note").GetString().Should().Be("rush");
    }

    // T2
    [Fact]
    public async Task T2_VersionedPatch_OptionalBodyProperty_Arrives()
    {
        var tool = await GetToolAsync("v_update_widget");
        var props = tool["inputSchema"]!.AsObject()["properties"]!.AsObject();
        props.Should().HaveProperty("note");
        props.Should().HaveProperty("archived");

        var response = await CallToolAsync("v_update_widget", new { version = "1", id = 42, note = "changed", archived = true });
        AssertSuccess(response);
        var echo = JsonDocument.Parse(ExtractText(response)).RootElement;
        echo.GetProperty("id").GetInt32().Should().Be(42);
        echo.GetProperty("note").GetString().Should().Be("changed", "an optional body property must not silently fall back to its default");
        echo.GetProperty("archived").GetBoolean().Should().BeTrue();
    }

    // T3
    [Fact]
    public async Task T3_VersionedGet_QueryParams_SchemaAdvertises_AndCallBinds()
    {
        var tool = await GetToolAsync("v_list_widgets");
        var props = tool["inputSchema"]!.AsObject()["properties"]!.AsObject();
        props.Should().HaveProperty("color");
        props.Should().HaveProperty("limit");
        props["limit"]!.AsObject()["default"]!.GetValue<int>().Should().Be(10);

        var response = await CallToolAsync("v_list_widgets", new { version = "1", color = "red", limit = 3 });
        AssertSuccess(response);
        var echo = JsonDocument.Parse(ExtractText(response)).RootElement;
        echo.GetProperty("color").GetString().Should().Be("red");
        echo.GetProperty("limit").GetInt32().Should().Be(3);
    }

    // T4 (strict unversioned sanity in addition to the sample-app Priority3_MinimalApi_* tests)
    [Fact]
    public async Task T4_UnversionedPost_Body_StillBinds()
    {
        var tool = await GetToolAsync("plain_create_widget");
        var props = tool["inputSchema"]!.AsObject()["properties"]!.AsObject();
        props.Should().HaveProperty("name");
        props.Should().HaveProperty("quantity");

        var response = await CallToolAsync("plain_create_widget", new { name = "Plain", quantity = 2, note = "n" });
        AssertSuccess(response);
        var echo = JsonDocument.Parse(ExtractText(response)).RootElement;
        echo.GetProperty("name").GetString().Should().Be("Plain");
        echo.GetProperty("quantity").GetInt32().Should().Be(2);
        echo.GetProperty("note").GetString().Should().Be("n");
    }

    // T5 (end-to-end)
    [Fact]
    public async Task T5_UnresolvedUnsafeEndpoint_WithLeftoverArgs_FailsLoudly()
    {
        var response = await CallToolAsync("hidden_create_widget", new { name = "Ghost", quantity = 1 });
        response.Should().HaveProperty("result");
        response["result"]!.AsObject()["isError"]!.GetValue<bool>().Should().BeTrue();
        var text = ExtractText(response);
        text.Should().Contain("Failed to bind arguments");
        text.Should().Contain("route metadata unresolved for POST api/hidden/widgets");
        text.Should().Contain("name").And.Contain("quantity");
    }

    private async Task<JsonObject> GetToolAsync(string name)
    {
        var response = await PostMcpAsync(new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        var tools = response["result"]!.AsObject()["tools"]!.AsArray();
        var tool = tools.FirstOrDefault(t => t!.AsObject()["name"]!.GetValue<string>() == name);
        tool.Should().NotBeNull($"tool '{name}' must be discovered");
        return tool!.AsObject();
    }

    private Task<JsonObject> CallToolAsync(string name, object arguments) =>
        PostMcpAsync(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name, arguments } });

    private async Task<JsonObject> PostMcpAsync(object body)
    {
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = content };
        var httpResponse = await _client.SendAsync(request);
        var json = await httpResponse.Content.ReadAsStringAsync();
        return JsonNode.Parse(json)!.AsObject();
    }

    private static void AssertSuccess(JsonObject response)
    {
        response.Should().HaveProperty("result");
        var result = response["result"]!.AsObject();
        result["isError"]!.GetValue<bool>().Should().BeFalse(ExtractText(response));
    }

    private static string ExtractText(JsonObject response) =>
        response["result"]!.AsObject()["content"]!.AsArray()[0]!.AsObject()["text"]!.GetValue<string>();
}

// ---------------------------------------------------------------------------
// Sample app (UseRouting + MapZeroMCP, no UseEndpoints): upstream pre-warms discovery inside MapZeroMCP()
// before the app's endpoint data sources are visible, which also freezes ApiExplorer's cached description
// collection. Even after InvalidateCache() the minimal-API tools are discovered but UNRESOLVED. This pins
// the fork's policy end-to-end on a real app: safe GET still binds via the query fallback, while the
// unsafe POST fails loudly instead of silently running on defaults. (The Priority3_MinimalApi_* tests in
// McpEndpointIntegrationTests early-return in this layout because the tools are absent.)
// ---------------------------------------------------------------------------

public sealed class SampleMinimalApiUnresolvedTests : IClassFixture<SampleAppWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly McpToolDiscoveryService _discovery;

    public SampleMinimalApiUnresolvedTests(SampleAppWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _discovery = factory.Services.GetRequiredService<McpToolDiscoveryService>();
        _discovery.InvalidateCache();
    }

    [Fact]
    public void Discovery_SampleMinimalTools_AreUnresolved()
    {
        _discovery.GetTool("list_orders_minimal")!.ApiDescriptionResolved.Should().BeFalse();
        _discovery.GetTool("create_order_minimal")!.ApiDescriptionResolved.Should().BeFalse();
    }

    [Fact]
    public async Task Unresolved_SafeGet_QueryFallbackStillBinds()
    {
        var response = await CallAsync("list_orders_minimal", new { status = "pending", page = 1, pageSize = 5 });
        response["result"]!.AsObject()["isError"]!.GetValue<bool>().Should().BeFalse(Text(response));
        var orders = JsonSerializer.Deserialize<JsonElement[]>(Text(response))!;
        orders.Should().AllSatisfy(o => o.GetProperty("status").GetString().Should().Be("pending"));
        orders.Length.Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public async Task Unresolved_UnsafePost_FailsLoudly()
    {
        var response = await CallAsync("create_order_minimal", new { customerName = "MinimalUser", product = "TestProduct", quantity = 3 });
        response["result"]!.AsObject()["isError"]!.GetValue<bool>().Should().BeTrue();
        Text(response).Should().Contain("Failed to bind arguments")
            .And.Contain("route metadata unresolved for POST api/orders/minimal");
    }

    private async Task<JsonObject> CallAsync(string name, object arguments)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        var response = await _client.SendAsync(request);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static string Text(JsonObject response) =>
        response["result"]!.AsObject()["content"]!.AsArray()[0]!.AsObject()["text"]!.GetValue<string>();
}

// ---------------------------------------------------------------------------
// Unit tests: SyntheticHttpContextFactory fail-loud policy (T5) and resolver fallback
// ---------------------------------------------------------------------------

public sealed class SyntheticHttpContextFactoryBindingTests
{
    private static (SyntheticHttpContextFactory factory, IServiceScope scope) CreateFactory()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        var sp = services.BuildServiceProvider();
        var factory = new SyntheticHttpContextFactory(
            new DefaultHttpContextFactory(sp), sp, Microsoft.Extensions.Options.Options.Create(new ZeroMCPOptions()));
        return (factory, sp.CreateScope());
    }

    private static McpToolDescriptor Descriptor(string method, bool resolved) => new()
    {
        Name = "t",
        HttpMethod = method,
        RelativeUrl = "api/v{version:apiVersion}/things/{id}",
        RouteParameters = [new McpParameterDescriptor { Name = "version", ParameterType = typeof(string), IsRequired = true },
                           new McpParameterDescriptor { Name = "id", ParameterType = typeof(string), IsRequired = true }],
        ApiDescriptionResolved = resolved
    };

    private static IReadOnlyDictionary<string, JsonElement> Args(object o) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void Unresolved_UnsafeMethod_WithLeftoverArgs_Throws(string method)
    {
        var (factory, scope) = CreateFactory();
        var act = () => factory.Build(Descriptor(method, resolved: false), Args(new { version = "1", id = "5", name = "x", qty = 2 }), scope);
        act.Should().Throw<ArgumentException>()
            .WithMessage($"cannot bind arguments [name, qty]: route metadata unresolved for {method} api/v{{version:apiVersion}}/things/{{id}}");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void Unresolved_SafeMethod_FallsThroughToQuery(string method)
    {
        var (factory, scope) = CreateFactory();
        var ctx = factory.Build(Descriptor(method, resolved: false), Args(new { version = "1", id = "5", name = "x" }), scope);
        ctx.Request.Query["name"].ToString().Should().Be("x");
    }

    [Fact]
    public void Resolved_UnsafeMethod_WithLeftoverArgs_FallsThroughToQuery()
    {
        var (factory, scope) = CreateFactory();
        var ctx = factory.Build(Descriptor("POST", resolved: true), Args(new { version = "1", id = "5", extra = "y" }), scope);
        ctx.Request.Query["extra"].ToString().Should().Be("y");
    }

    [Fact]
    public void Unresolved_UnsafeMethod_OnlyRouteArgs_DoesNotThrow()
    {
        var (factory, scope) = CreateFactory();
        var act = () => factory.Build(Descriptor("DELETE", resolved: false), Args(new { version = "1", id = "5" }), scope);
        act.Should().NotThrow();
    }
}

public sealed class ApiDescriptionResolverTests
{
    private sealed class FakeProvider(params ApiDescription[] descriptions) : IApiDescriptionGroupCollectionProvider
    {
        public ApiDescriptionGroupCollection ApiDescriptionGroups { get; } =
            new([new ApiDescriptionGroup("g", descriptions)], 1);
    }

    private static ApiDescription Desc(string method, string path) => new()
    {
        HttpMethod = method,
        RelativePath = path,
        ActionDescriptor = new ActionDescriptor() // no endpoint identity → forces path/pattern fallback
    };

    private static RouteEndpoint Endpoint(string template) =>
        new(_ => Task.CompletedTask, RoutePatternFactory.Parse(template), 0, EndpointMetadataCollection.Empty, template);

    [Fact]
    public void PatternFallback_MatchesSubstitutedVersionSegment()
    {
        var target = Desc("POST", "api/v1/widgets/{id}");
        var provider = new FakeProvider(Desc("GET", "api/v1/widgets/{id}"), Desc("POST", "api/v1/widgets"), target);
        var ep = Endpoint("/api/v{version:apiVersion}/widgets/{id:int}");
        ApiDescriptionResolver.Resolve(provider, ep, "api/v{version:apiVersion}/widgets/{id:int}", "POST").Should().BeSameAs(target);
    }

    [Fact]
    public void ExactPathMatch_PreferredOverPattern()
    {
        var exact = Desc("GET", "api/orders/minimal");
        var provider = new FakeProvider(Desc("GET", "api/orders/{x}"), exact);
        ApiDescriptionResolver.Resolve(provider, Endpoint("/api/orders/minimal"), "api/orders/minimal", "GET").Should().BeSameAs(exact);
    }

    [Fact]
    public void NoMatch_ReturnsNull()
    {
        var provider = new FakeProvider(Desc("POST", "api/v1/widgets/{id}/extra"), Desc("GET", "api/v1/widgets/{id}"));
        ApiDescriptionResolver.Resolve(provider, Endpoint("/api/v{version:apiVersion}/widgets/{id}"), "api/v{version:apiVersion}/widgets/{id}", "POST")
            .Should().BeNull();
    }

    [Fact]
    public void EndpointIdentity_ViaEndpointMetadata_WinsOverPath()
    {
        var marker = new object();
        var ep = new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/api/v{version:apiVersion}/a"), 0,
            new EndpointMetadataCollection(marker), "a");
        var decoy = Desc("GET", "api/v1/a");
        var real = new ApiDescription
        {
            HttpMethod = "GET",
            RelativePath = "something/else",
            ActionDescriptor = new ActionDescriptor { EndpointMetadata = [marker] }
        };
        ApiDescriptionResolver.Resolve(new FakeProvider(decoy, real), ep, "api/v{version:apiVersion}/a", "GET").Should().BeSameAs(real);
    }

    [Theory]
    [InlineData("api/v{version:apiVersion}/w/{id:regex(^\\d{{3}}$)}", "api/v2/w/{id}", true)]
    [InlineData("api/v{version:apiVersion}/w", "api/v2/w/extra", false)]
    [InlineData("api/x.y/{id}", "api/xzy/1", false)]
    public void TemplateRegex_Behaves(string template, string path, bool expected) =>
        ApiDescriptionResolver.BuildTemplateRegex(ApiDescriptionResolver.NormalizePath(template)).IsMatch(path).Should().Be(expected);
}
