using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace ZeroMCP.Options;

/// <summary>
/// Configuration options for the ZeroMCP middleware.
/// </summary>
public sealed class ZeroMCPOptions
{
    /// <summary>
    /// The route prefix for the MCP endpoint. Default is "/mcp".
    /// </summary>
    public string RoutePrefix { get; set; } = "/mcp";

    /// <summary>
    /// The server name advertised via <c>server/discover</c> and legacy <c>initialize</c>. Defaults to the entry assembly name.
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// The server version advertised via <c>server/discover</c> and legacy <c>initialize</c>. Defaults to "1.0.0".
    /// </summary>
    public string ServerVersion { get; set; } = "1.0.0";

    /// <summary>
    /// When true (default), the server accepts both modern MCP <c>2026-07-28</c> (per-request <c>_meta</c>,
    /// <c>server/discover</c>) and legacy <c>2024-11-05</c> (<c>initialize</c> handshake, GET SSE).
    /// When false, only the modern protocol is accepted; <c>initialize</c> and GET SSE return errors.
    /// </summary>
    public bool EnableLegacyProtocol { get; set; } = true;

    /// <summary>
    /// Cache TTL hint (<c>ttlMs</c>) returned on modern-era list/read results. Default 0 (immediately stale).
    /// </summary>
    public int ListResultTtlMs { get; set; }

    /// <summary>
    /// Cache scope hint for modern-era list/read results: <c>public</c> or <c>private</c>. Default <c>private</c>
    /// because tool visibility can depend on the caller's authorization context.
    /// </summary>
    public string ListResultCacheScope { get; set; } = "private";

    /// <summary>
    /// Whether to include JSON Schema definitions in tool input descriptions.
    /// Enables the LLM to understand parameter types and constraints. Default is true.
    /// </summary>
    public bool IncludeInputSchemas { get; set; } = true;

    /// <summary>
    /// Optional predicate to further filter which [McpTool]-tagged actions are exposed at discovery time (by name only).
    /// Useful for environment-specific exclusions (e.g. exclude admin tools in non-production).
    /// </summary>
    public Func<string, bool>? ToolFilter { get; set; }

    /// <summary>
    /// Optional predicate to filter which tools are returned in tools/list per request.
    /// Receives the tool name and the current HTTP context (e.g. to check user, headers, or environment).
    /// Return true to include the tool, false to hide it. When null, no per-request filter is applied
    /// (role/policy filters on descriptors still apply).
    /// </summary>
    public Func<string, HttpContext, bool>? ToolVisibilityFilter { get; set; }

    /// <summary>
    /// Header names to forward from the incoming MCP request into the synthetic HttpContext
    /// when dispatching tool calls. Enables the dispatched action to see the same auth (e.g. Bearer token).
    /// Default is ["Authorization"]. Set to empty or null to disable forwarding.
    /// </summary>
    public IReadOnlyList<string>? ForwardHeaders { get; set; } = ["Authorization"];

    /// <summary>
    /// Request header name used to read and propagate a correlation ID. If present, the same value is echoed in the response and in logs.
    /// If absent, a new GUID is generated. Default is "X-Correlation-ID". Set to null or empty to disable correlation ID handling.
    /// </summary>
    public string? CorrelationIdHeader { get; set; } = "X-Correlation-ID";

    /// <summary>
    /// When true, tags the current <see cref="System.Diagnostics.Activity"/> (if any) with MCP tool invocation details
    /// (mcp.tool, mcp.status_code, mcp.is_error, mcp.duration_ms). Use with OpenTelemetry or similar. Default is false.
    /// </summary>
    public bool EnableOpenTelemetryEnrichment { get; set; }

    // --- Phase 2: Result enrichment ---

    /// <summary>
    /// When true, tools/call results include a metadata object (statusCode, contentType, correlationId, durationMs)
    /// and optional suggestedNextActions and hints for AI clients. Default is false.
    /// </summary>
    public bool EnableResultEnrichment { get; set; }

    /// <summary>
    /// When true and <see cref="SuggestedFollowUpsProvider"/> is set, tools/call results include suggested next tool calls with rationale. Default is false.
    /// </summary>
    public bool EnableSuggestedFollowUps { get; set; }

    /// <summary>
    /// Optional. When <see cref="EnableResultEnrichment"/> is true, invoked after each tool call to supply client-facing hints (e.g. "consider retry", "rate limited").
    /// Parameters: tool name, HTTP status code, response content, isError, HTTP context. Return null or empty to omit hints.
    /// </summary>
    public Func<string, int, string, bool, HttpContext?, IReadOnlyList<string>?>? ResponseHintProvider { get; set; }

    /// <summary>
    /// Optional. When <see cref="EnableSuggestedFollowUps"/> is true, invoked after each tool call to suggest follow-up tools.
    /// Parameters: tool name, HTTP status code, response content, isError, HTTP context. Return null or empty to omit suggestions.
    /// </summary>
    public Func<string, int, string, bool, HttpContext?, IReadOnlyList<ZeroMCPOptionsSuggestedAction>?>? SuggestedFollowUpsProvider { get; set; }

    // --- Phase 2: Streaming / partial responses ---

    /// <summary>
    /// When true, tools/call may return content as a sequence of chunks (chunkIndex, isFinal, text) for long responses.
    /// Default is false (single content block). When true, response content is split by <see cref="StreamingChunkSize"/> for compatibility with streaming-aware clients.
    /// </summary>
    public bool EnableStreamingToolResults { get; set; }

    /// <summary>
    /// When <see cref="EnableStreamingToolResults"/> is true, response content is split into chunks of this size (characters). Default is 4096.
    /// </summary>
    public int StreamingChunkSize { get; set; } = 4096;

    // --- Phase 3: Tool Inspector ---

    /// <summary>
    /// When true, registers a GET {RoutePrefix}/tools endpoint that returns the full tool registry as JSON for developer inspection.
    /// Does not apply per-request visibility (shows all registered tools). Default is true.
    /// </summary>
    public bool EnableToolInspector { get; set; } = true;

    /// <summary>
    /// When true will check XMLDoc descriptions if MCP Description is left blank. Default is true.
    /// </summary>
    public bool EnableXMLDocAnalysis { get; set; } = true;

    /// <summary>
    /// When true and <see cref="EnableToolInspector"/> is true, registers GET {RoutePrefix}/ui with a Swagger-like test invocation UI.
    /// The UI lists tools, shows input schemas, and lets you invoke tools/call from the browser. Default is true.
    /// </summary>
    public bool EnableToolInspectorUI { get; set; } = true;

    /// <summary>
    /// When set, the unversioned /mcp endpoint resolves to this version instead of the highest registered version.
    /// Default: null (auto = highest registered version). Useful during migration to control when clients get bumped.
    /// </summary>
    public int? DefaultVersion { get; set; }

    // --- stdio Transport (Priority 1) ---

    /// <summary>
    /// When running in stdio transport mode, this identity is used as the authenticated user for tool dispatch.
    /// stdio has no HTTP context; set this to provide a fixed identity (e.g. for role-based tool visibility).
    /// When null, User is unauthenticated. ForwardHeaders is a no-op in stdio mode.
    /// </summary>
    public ClaimsPrincipal? StdioIdentity { get; set; }

    /// <summary>
    /// <b>INSECURE opt-out — leave false unless you have audited every consumer of <see cref="IHttpContextAccessor"/>.</b>
    /// <para>
    /// During tool dispatch ZeroMCP points <see cref="IHttpContextAccessor"/> at the synthetic request built from the tool
    /// arguments. That swap is only possible for the framework <see cref="HttpContextAccessor"/> on a framework whose
    /// internals ZeroMCP recognises (see <see cref="ZeroMCP.ZeroMcpCompatibility.AccessorScopeSupported"/>). When it is not
    /// possible (a custom accessor is registered, or the internals are unsupported), the accessor would keep returning the
    /// OUTER <c>POST /mcp</c> request, whose query string and headers the caller controls — so authorization handlers and
    /// actions that read the accessor could be swayed by the caller.
    /// </para>
    /// <para>
    /// Default (<c>false</c>): such dispatches are refused with a 500 and the action is not invoked (fail closed).
    /// <c>true</c>: the dispatch proceeds with the accessor still resolving to the outer request, and a warning is logged.
    /// </para>
    /// </summary>
    public bool AllowDegradedAccessorDispatch { get; set; }

    // --- Legacy SSE Transport (Priority 6) ---

    /// <summary>
    /// Maximum size in bytes for base64-decoded form file content. Enforced before decoding; exceeded payloads return a structured error.
    /// Default is 10 MB. Set to 0 to disable the limit (not recommended).
    /// </summary>
    public long MaxFormFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    // --- IAsyncEnumerable<T> streaming ---

    /// <summary>
    /// Safety limit: maximum number of items yielded from an IAsyncEnumerable&lt;T&gt; before
    /// the enumerator is cancelled and a final error chunk is emitted. 0 = unlimited. Default: 10000.
    /// </summary>
    public int MaxStreamingItems { get; set; } = 10_000;

    /// <summary>
    /// When true, registers GET {RoutePrefix}/sse and POST {RoutePrefix}/messages for the deprecated MCP HTTP+SSE transport (spec 2024-11-05).
    /// Enables backward compatibility with clients that have not migrated to Streamable HTTP.
    /// Default is false. Can also be enabled via <c>MapZeroMCP().WithLegacySseTransport()</c>.
    /// </summary>
    /// <remarks>
    /// SSE sessions are held in process memory; does not scale horizontally without sticky sessions.
    /// Use Streamable HTTP for horizontal scale.
    /// </remarks>
    public bool EnableLegacySseTransport { get; set; }

    // --- listChanged notifications ---

    /// <summary>
    /// When true, registers <c>McpNotificationService</c> and advertises <c>listChanged: true</c>
    /// in the <c>initialize</c> capabilities. Connected SSE clients will receive
    /// <c>notifications/tools/list_changed</c>, <c>notifications/resources/list_changed</c>,
    /// and <c>notifications/prompts/list_changed</c> when the application calls the
    /// corresponding <c>Notify*ListChangedAsync()</c> methods.
    /// Default is false (opt-in).
    /// </summary>
    public bool EnableListChangedNotifications { get; set; }

    // --- Resource subscriptions ---

    /// <summary>
    /// When true and <see cref="EnableResources"/> is true, advertises <c>subscribe: true</c>
    /// in the <c>resources</c> capability and handles <c>resources/subscribe</c> and
    /// <c>resources/unsubscribe</c> JSON-RPC methods. Connected SSE clients receive
    /// <c>notifications/resources/updated</c> when the application calls
    /// <see cref="Notifications.McpNotificationService.NotifyResourceUpdatedAsync"/>.
    /// Default is false (opt-in).
    /// </summary>
    public bool EnableResourceSubscriptions { get; set; }

    // --- MCP Resources ---

    /// <summary>
    /// When true, advertises the <c>resources</c> capability during MCP handshake and handles
    /// <c>resources/list</c> and <c>resources/read</c> JSON-RPC methods.
    /// Controller actions tagged with <c>[McpResource]</c> are exposed as static resources;
    /// actions tagged with <c>[McpTemplate]</c> are exposed via <c>resources/templates/list</c>.
    /// Default is true.
    /// </summary>
    public bool EnableResources { get; set; } = true;

    // --- MCP Prompts ---

    /// <summary>
    /// When true, advertises the <c>prompts</c> capability during MCP handshake and handles
    /// <c>prompts/list</c> and <c>prompts/get</c> JSON-RPC methods.
    /// Controller actions tagged with <c>[McpPrompt]</c> are exposed as reusable prompt templates.
    /// Default is true.
    /// </summary>
    public bool EnablePrompts { get; set; } = true;
}

/// <summary>Suggested follow-up tool and rationale for AI clients.</summary>
public sealed record ZeroMCPOptionsSuggestedAction(string ToolName, string Rationale);
