using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;


namespace ZeroMCP.Discovery;

/// <summary>
/// Holds all the metadata needed to describe and invoke a single MCP tool.
/// Built at startup from the ApiDescription and McpAttribute, immutable at runtime.
/// </summary>
public sealed class McpToolDescriptor
{
    /// <summary>The tool name exposed to MCP clients.</summary>
    public string Name { get; init; } = default!;

    /// <summary>The tool description shown to the LLM.</summary>
    public string? Description { get; init; }

    /// <summary>Optional tags from McpAttribute.</summary>
    public string[]? Tags { get; init; }

    /// <summary>Optional primary category for grouping tools.</summary>
    public string? Category { get; init; }

    /// <summary>Optional free-form usage examples for this tool.</summary>
    public string[]? Examples { get; init; }

    /// <summary>Optional AI-facing hints/metadata strings (e.g. \"cost=high\", \"side_effects=writes_data\").</summary>
    public string[]? Hints { get; init; }

    /// <summary>When set, tool is only visible in tools/list if the user is in at least one of these roles.</summary>
    public string[]? RequiredRoles { get; init; }

    /// <summary>When set, tool is only visible in tools/list if the user satisfies this authorization policy.</summary>
    public string? RequiredPolicy { get; init; }

    /// <summary>When set, tool is only exposed on the versioned endpoint /mcp/v{Version}. Null means the tool appears on all version endpoints.</summary>
    public int? Version { get; init; }

    /// <summary>The full ApiDescription for this action (route, HTTP method, params). Null for minimal API endpoints.</summary>
    public ApiDescription? ApiDescription { get; init; }

    /// <summary>The controller action descriptor — used to build the invoker. Null for minimal API endpoints.</summary>
    public ControllerActionDescriptor? ActionDescriptor { get; init; }

    /// <summary>The endpoint for minimal API tools. When set, dispatch uses RequestDelegate instead of controller invoker.</summary>
    public Endpoint? Endpoint { get; init; }

    /// <summary>
    /// Parameters that come from the route template (e.g. /orders/{id}).
    /// </summary>
    public IReadOnlyList<McpParameterDescriptor> RouteParameters { get; init; } = [];

    /// <summary>
    /// Parameters that come from the query string.
    /// </summary>
    public IReadOnlyList<McpParameterDescriptor> QueryParameters { get; init; } = [];

    /// <summary>
    /// The body parameter, if any. Will be a complex type.
    /// </summary>
    public McpBodyDescriptor? Body { get; init; }

    /// <summary>
    /// True when the ApiExplorer <see cref="Microsoft.AspNetCore.Mvc.ApiExplorer.ApiDescription"/> for a minimal-API endpoint
    /// was resolved at discovery time, i.e. <see cref="QueryParameters"/> and <see cref="Body"/> reflect the endpoint's real
    /// binding sources. When false (minimal API whose route metadata could not be matched), query/body shape is unknown, and
    /// dispatch refuses to divert leftover arguments to the query string on unsafe methods (POST/PUT/PATCH/DELETE).
    /// Default false; always false for controller actions (which carry <see cref="ApiDescription"/> directly).
    /// </summary>
    public bool ApiDescriptionResolved { get; init; }

    /// <summary>
    /// Form file parameters (IFormFile, IFormFileCollection). MCP clients pass base64-encoded content.
    /// </summary>
    public IReadOnlyList<McpFormFileDescriptor> FormFileParameters { get; init; } = [];

    /// <summary>
    /// Form field parameters ([FromForm] string, etc.) alongside file params.
    /// </summary>
    public IReadOnlyList<McpParameterDescriptor> FormParameters { get; init; } = [];

    /// <summary>
    /// The merged JSON Schema for all inputs (route + query + body flattened).
    /// Null if IncludeInputSchemas is false.
    /// </summary>
    public string? InputSchemaJson { get; set; }

    /// <summary>True when the action returns IAsyncEnumerable&lt;T&gt; and results should be streamed progressively.</summary>
    public bool IsStreaming { get; init; }

    /// <summary>The element type T from IAsyncEnumerable&lt;T&gt;, used for schema generation. Null for non-streaming tools.</summary>
    public Type? StreamingElementType { get; init; }

    /// <summary>HTTP method (GET, POST, etc.)</summary>
    public string HttpMethod { get; init; } = default!;

    /// <summary>Relative URL template (e.g. "api/orders/{id}")</summary>
    public string RelativeUrl { get; init; } = default!;
}

public sealed class McpParameterDescriptor
{
    public string Name { get; init; } = default!;
    public Type ParameterType { get; init; } = default!;
    public bool IsRequired { get; init; }
    public string? Description { get; init; }
    /// <summary>Default value for optional parameters (e.g. page = 1). Emitted as "default" in JSON Schema.</summary>
    public object? DefaultValue { get; init; }
}

public sealed class McpBodyDescriptor
{
    public Type BodyType { get; init; } = default!;
    public string ParameterName { get; init; } = default!;
}

/// <summary>Describes an IFormFile or IFormFileCollection parameter for MCP schema and dispatch.</summary>
public sealed class McpFormFileDescriptor
{
    public string Name { get; init; } = default!;
    public string ParameterName { get; init; } = default!;
    /// <summary>True for IFormFileCollection (multiple files).</summary>
    public bool IsCollection { get; init; }
}
