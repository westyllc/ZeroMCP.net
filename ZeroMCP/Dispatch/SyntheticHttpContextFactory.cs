using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ZeroMCP.Discovery;
using ZeroMCP.Options;
using ZeroMCP.Transport;

namespace ZeroMCP.Dispatch;

/// <summary>
/// Constructs synthetic HttpContext instances for in-process action dispatch.
/// Translates MCP JSON arguments into a realistic HttpContext that ASP.NET Core's
/// model binding pipeline can consume as if it were a real HTTP request.
/// </summary>
public sealed class SyntheticHttpContextFactory
{
    private readonly IHttpContextFactory _httpContextFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ZeroMCPOptions _options;

    public SyntheticHttpContextFactory(
        IHttpContextFactory httpContextFactory,
        IServiceProvider serviceProvider,
        IOptions<ZeroMCPOptions> options)
    {
        _httpContextFactory = httpContextFactory;
        _serviceProvider = serviceProvider;
        _options = options.Value;
    }

    /// <summary>
    /// Builds a synthetic HttpContext populated from the MCP tool arguments.
    /// Route values, query string, and body stream are all set appropriately.
    /// If sourceContext is provided and ForwardHeaders is configured, copies those headers.
    /// Uses cancellationToken for RequestAborted so [Mcp] actions receive cancellation (notifications/cancelled, connection drop).
    /// </summary>
    public HttpContext Build(
        McpToolDescriptor descriptor,
        IReadOnlyDictionary<string, JsonElement> args,
        IServiceScope scope,
        HttpContext? sourceContext = null,
        CancellationToken cancellationToken = default)
    {
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature());
        var abortToken = cancellationToken != default ? cancellationToken : sourceContext?.RequestAborted ?? default;
        features.Set<IHttpRequestLifetimeFeature>(new CancellableHttpRequestLifetimeFeature(abortToken));
        features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new MemoryStream()));
        features.Set<IServiceProvidersFeature>(new SyntheticRequestServicesFeature(scope.ServiceProvider));
        features.Set<IItemsFeature>(new ItemsFeature());

        // Pre-build form when we have FormFile params so FormFeature is set before context creation
        if (descriptor.FormFileParameters.Count > 0)
        {
            var (formCollection, formError) = BuildFormCollection(descriptor, args);
            if (formError is not null)
                throw new ArgumentException(formError);
            features.Set<IFormFeature>(new FormFeature(formCollection!));
        }

        var context = new DefaultHttpContext(features);
        context.RequestServices = scope.ServiceProvider;

        // Set HTTP method and path
        context.Request.Method = descriptor.HttpMethod.ToUpperInvariant();
        context.Request.PathBase = PathString.Empty;
        context.Request.Path = "/" + descriptor.RelativeUrl.TrimStart('/');
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");

        // Bind route values
        var routeValues = new RouteValueDictionary();
        foreach (var routeParam in descriptor.RouteParameters)
        {
            if (args.TryGetValue(routeParam.Name, out var value))
            {
                var stringValue = ExtractStringValue(value);
                routeValues[routeParam.Name] = stringValue;

                // Also rewrite the path with the actual value substituted
                context.Request.Path = new PathString(
                    context.Request.Path.Value!.Replace(
                        $"{{{routeParam.Name}}}",
                        Uri.EscapeDataString(stringValue),
                        StringComparison.OrdinalIgnoreCase));
            }
        }

        context.Request.RouteValues = routeValues;

        // Ambient controller/action so LinkGenerator and CreatedAtAction can resolve routes
        if (descriptor.ActionDescriptor is not null)
        {
            routeValues["controller"] = descriptor.ActionDescriptor.ControllerName;
            routeValues["action"] = descriptor.ActionDescriptor.ActionName;
        }

        // Set up routing feature so model binding can read route values
        context.Features.Set<IRoutingFeature>(new RoutingFeature
        {
            RouteData = new RouteData(routeValues)
        });

        // Bind query string
        var queryParts = new List<string>();
        var handledParams = new HashSet<string>(
            descriptor.RouteParameters.Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var queryParam in descriptor.QueryParameters)
        {
            handledParams.Add(queryParam.Name);
            if (args.TryGetValue(queryParam.Name, out var value))
            {
                queryParts.Add($"{Uri.EscapeDataString(queryParam.Name)}={Uri.EscapeDataString(ExtractStringValue(value))}");
            }
        }

        // For minimal API endpoints with no body, fall through any remaining args
        // (not already placed in route values or query string) to the query string.
        // This mirrors how .AsMcp() tools dispatch arguments and handles the case
        // where the API description lookup doesn't find the endpoint's query parameters.
        //
        // That diversion is only REST-faithful when the ApiDescription was resolved (a real binder ignores
        // unknown query params) or the method is safe. For an UNRESOLVED descriptor on an unsafe method the
        // arguments may belong in a body we know nothing about — silently sending them to the query string
        // would run the write on defaults, so fail loudly instead (the dispatcher maps this to a 400).
        if (descriptor.ActionDescriptor is null && descriptor.Body is null)
        {
            var leftovers = args.Keys.Where(k => !handledParams.Contains(k)).ToList();
            if (leftovers.Count > 0 && !descriptor.ApiDescriptionResolved && !IsSafeMethod(descriptor.HttpMethod))
            {
                throw new ArgumentException(
                    $"cannot bind arguments [{string.Join(", ", leftovers)}]: route metadata unresolved for {descriptor.HttpMethod.ToUpperInvariant()} {descriptor.RelativeUrl}");
            }

            foreach (var key in leftovers)
            {
                queryParts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(ExtractStringValue(args[key]))}");
            }
        }

        if (queryParts.Count > 0)
        {
            context.Request.QueryString = new QueryString("?" + string.Join("&", queryParts));
        }

        // Bind form (files + form fields) or body (JSON)
        if (descriptor.FormFileParameters.Count > 0)
        {
            context.Request.ContentType = "multipart/form-data";
            context.Request.Body = new MemoryStream(); // Some pipeline code expects non-null body
        }
        else if (descriptor.Body is not null)
        {
            var bodyArgs = new Dictionary<string, JsonElement>();

            // Collect body properties: all args that aren't route or query params
            var nonBodyParamNames = new HashSet<string>(
                descriptor.RouteParameters.Select(p => p.Name)
                    .Concat(descriptor.QueryParameters.Select(p => p.Name)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var (key, val) in args)
            {
                if (!nonBodyParamNames.Contains(key))
                    bodyArgs[key] = val;
            }

            var bodyJson = JsonSerializer.Serialize(bodyArgs, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
            context.Request.Body = new MemoryStream(bodyBytes);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bodyBytes.Length;
            // Minimal-API (RequestDelegateFactory) body binding only reads the body when the server reports
            // CanHaveBody; without this feature every [FromBody]/implicit-body handler fails with
            // "Implicit body inferred ... but no body was provided".
            context.Features.Set<IHttpRequestBodyDetectionFeature>(SyntheticRequestBodyDetectionFeature.WithBody);
        }

        // Set response body to a writable MemoryStream so we can capture output
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        // Forward headers from the MCP request (e.g. Authorization) so the dispatched action sees the same auth
        if (sourceContext is not null && _options.ForwardHeaders is { Count: > 0 })
        {
            var sourceHeaders = sourceContext.Request.Headers;
            var names = _options.ForwardHeaders;
            for (var i = 0; i < names.Count; i++)
            {
                var name = names[i];
                if (sourceHeaders.TryGetValue(name, out var value))
                    context.Request.Headers[name] = value;
            }
        }

        // Propagate correlation ID to synthetic context so logs/traces use the same ID


        if (sourceContext?.Items[McpHttpEndpointHandler.CorrelationIdItemKey] is string correlationId)
        {

            context.TraceIdentifier = correlationId;


            context.Items[McpHttpEndpointHandler.CorrelationIdItemKey] = correlationId;
        }

        // Copy the authenticated user from the MCP request so [Authorize] and authorization filters see the same identity.
        // Auth middleware runs on the MCP request before we get here; the synthetic request bypasses middleware, so we must propagate User.
        if (sourceContext is not null)
            context.User = sourceContext.User;

        return context;
    }

    /// <summary>
    /// Makes <see cref="IHttpContextAccessor.HttpContext"/> resolve to <paramref name="syntheticContext"/> for the
    /// current async flow, so code that reads the accessor during dispatch sees the request being dispatched
    /// instead of the outer <c>POST /mcp</c> request. This covers authorization handlers, filters, services and the
    /// action itself. Dispose the returned scope in a <c>finally</c> once the invocation completes. That restores the
    /// previous value, and the outer request is never modified.
    /// </summary>
    /// <remarks>
    /// The <see cref="IHttpContextAccessor"/> is resolved from <paramref name="services"/>. The swap applies only
    /// when it is, or derives from, the framework <see cref="HttpContextAccessor"/>. Its state is a static
    /// <c>AsyncLocal</c>, so every instance shares it. <b>Limitation:</b> a custom (non-framework)
    /// <see cref="IHttpContextAccessor"/> implementation is left alone, and keeps returning whatever it would
    /// have returned (usually the outer request). The same no-op applies when no accessor is registered, or when
    /// the framework's private accessor internals are not recognised (see <see cref="HttpContextAccessorScope"/>).
    /// In all of these cases the returned scope does nothing.
    /// </remarks>
    public IDisposable EnterHttpContextAccessorScope(HttpContext syntheticContext, IServiceProvider services)
    {
        if (services.GetService(typeof(IHttpContextAccessor)) is HttpContextAccessor)
        {
            var scope = HttpContextAccessorScope.Enter(syntheticContext);
            if (scope is not null)
                return scope;
        }
        return NoopScope.Instance;
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }

    private (FormCollection? form, string? error) BuildFormCollection(McpToolDescriptor descriptor, IReadOnlyDictionary<string, JsonElement> args)
    {
        var fields = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        var files = new List<IFormFile>();

        foreach (var param in descriptor.FormParameters)
        {
            if (args.TryGetValue(param.Name, out var value))
                fields[param.Name] = ExtractStringValue(value);
        }

        var maxBytes = _options.MaxFormFileSizeBytes;
        if (maxBytes <= 0) maxBytes = long.MaxValue;

        foreach (var param in descriptor.FormFileParameters)
        {
            if (param.IsCollection)
            {
                if (!args.TryGetValue(param.Name, out var arrEl) || arrEl.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in arrEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var content = item.TryGetProperty("content", out var c) ? c.GetString() : null;
                    if (string.IsNullOrEmpty(content)) continue;
                    var decoded = Convert.FromBase64String(content);
                    if (decoded.LongLength > maxBytes)
                        return (null, $"File '{param.Name}' exceeds MaxFormFileSizeBytes ({_options.MaxFormFileSizeBytes} bytes)");
                    var filename = item.TryGetProperty("filename", out var fn) ? fn.GetString() ?? "file" : "file";
                    var contentType = item.TryGetProperty("content_type", out var ct) ? ct.GetString() ?? "application/octet-stream" : "application/octet-stream";
                    var stream = new MemoryStream(decoded);
                    var ff = new FormFile(stream, 0, stream.Length, param.ParameterName, filename) { Headers = new HeaderDictionary() };
                    ff.Headers.ContentType = contentType;
                    files.Add(ff);
                }
            }
            else
            {
                if (!args.TryGetValue(param.Name, out var base64El))
                    continue;
                var base64 = base64El.GetString();
                if (string.IsNullOrEmpty(base64)) continue;
                var decoded = Convert.FromBase64String(base64);
                if (decoded.LongLength > maxBytes)
                    return (null, $"File '{param.Name}' exceeds MaxFormFileSizeBytes ({_options.MaxFormFileSizeBytes} bytes)");
                var filename = args.TryGetValue(param.Name + "_filename", out var fnEl) ? fnEl.GetString() ?? "file" : "file";
                var contentType = args.TryGetValue(param.Name + "_content_type", out var ctEl) ? ctEl.GetString() ?? "application/octet-stream" : "application/octet-stream";
                var stream = new MemoryStream(decoded);
                var ff = new FormFile(stream, 0, stream.Length, param.ParameterName, filename) { Headers = new HeaderDictionary() };
                ff.Headers.ContentType = contentType;
                files.Add(ff);
            }
        }

        var formFiles = new FormFileCollection();
        foreach (var f in files)
            formFiles.Add(f);

        return (new FormCollection(fields, formFiles), null);
    }

    private static bool IsSafeMethod(string? method) =>
        HttpMethods.IsGet(method ?? "") || HttpMethods.IsHead(method ?? "") || HttpMethods.IsOptions(method ?? "");

    private static string ExtractStringValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        _ => element.GetRawText()
    };
}

/// <summary>Minimal IServiceProvidersFeature for synthetic requests when RequestServicesFeature constructor is not compatible.</summary>
internal sealed class SyntheticRequestServicesFeature : IServiceProvidersFeature
{
    public SyntheticRequestServicesFeature(IServiceProvider requestServices) => RequestServices = requestServices;
    public IServiceProvider RequestServices { get; set; }
}

/// <summary>Reports that the synthetic request carries a body so minimal-API body binding reads it.</summary>
internal sealed class SyntheticRequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
{
    public static readonly SyntheticRequestBodyDetectionFeature WithBody = new();
    public bool CanHaveBody => true;
}

/// <summary>Minimal IRoutingFeature implementation for synthetic requests.</summary>
internal sealed class RoutingFeature : IRoutingFeature
{
    public RouteData? RouteData { get; set; }
}

/// <summary>IHttpRequestLifetimeFeature with configurable RequestAborted for synthetic contexts.</summary>
internal sealed class CancellableHttpRequestLifetimeFeature : IHttpRequestLifetimeFeature
{
    public CancellableHttpRequestLifetimeFeature(CancellationToken requestAborted) => RequestAborted = requestAborted;
    public CancellationToken RequestAborted { get; set; }
    public void Abort() { /* no-op for synthetic */ }
}
