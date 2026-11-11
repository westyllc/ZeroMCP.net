using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using ZeroMCP.Attributes;
using ZeroMCP.Metadata;

namespace ZeroMCP.Discovery;

/// <summary>
/// Discovers all <c>[McpPrompt]</c> attributed controller actions at startup and builds the prompt registry.
/// Results are cached for the lifetime of the application.
/// </summary>
public sealed class McpPromptDiscoveryService
{
    private readonly IApiDescriptionGroupCollectionProvider _apiDescriptionProvider;
    private readonly EndpointDataSource _endpointDataSource;
    private readonly ILogger<McpPromptDiscoveryService> _logger;

    private IReadOnlyList<McpPromptDescriptor>? _prompts;
    private IReadOnlyDictionary<string, McpPromptDescriptor>? _byName;

#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif

    public McpPromptDiscoveryService(
        IApiDescriptionGroupCollectionProvider apiDescriptionProvider,
        EndpointDataSource endpointDataSource,
        ILogger<McpPromptDiscoveryService> logger)
    {
        _apiDescriptionProvider = apiDescriptionProvider;
        _endpointDataSource = endpointDataSource;
        _logger = logger;
    }

    private void EnsureBuilt()
    {
        if (_prompts is not null) return;
        lock (_lock)
        {
            if (_prompts is not null) return;
            BuildRegistry();
        }
    }

    /// <summary>
    /// Clears the cached prompt registry so the next access triggers a full rebuild.
    /// Call this followed by <c>McpNotificationService.NotifyPromptsListChangedAsync()</c>
    /// to inform connected clients that the prompt list has changed.
    /// </summary>
    public void InvalidateCache()
    {
        lock (_lock)
        {
            _prompts = null;
            _byName = null;
        }
        _logger.LogInformation("ZeroMCP: prompt registry cache invalidated");
    }

    /// <summary>All discovered MCP prompts.</summary>
    public IReadOnlyList<McpPromptDescriptor> GetPrompts()
    {
        EnsureBuilt();
        return _prompts!;
    }

    /// <summary>Finds a prompt by name (case-insensitive). Returns null if not found.</summary>
    public McpPromptDescriptor? GetPrompt(string name)
    {
        EnsureBuilt();
        return _byName!.TryGetValue(name, out var d) ? d : null;
    }

    private void BuildRegistry()
    {
        var prompts = new List<McpPromptDescriptor>();

        var allDescriptions = _apiDescriptionProvider.ApiDescriptionGroups.Items
            .SelectMany(g => g.Items);

        foreach (var apiDescription in allDescriptions)
        {
            if (apiDescription.ActionDescriptor is not ControllerActionDescriptor controllerDescriptor)
                continue;

            var promptAttr = controllerDescriptor.MethodInfo
                .GetCustomAttributes(typeof(McpPromptAttribute), inherit: false)
                .FirstOrDefault() as McpPromptAttribute;

            if (promptAttr is null)
                continue;

            var dispatchDescriptor = BuildDispatchDescriptor(apiDescription, controllerDescriptor);

            // Derive prompt arguments from query and route params (body params become a single object arg)
            var args = new List<McpPromptArgumentDescriptor>();
            foreach (var p in dispatchDescriptor.RouteParameters)
                args.Add(new McpPromptArgumentDescriptor { Name = p.Name, Description = p.Description, Required = p.IsRequired });
            foreach (var p in dispatchDescriptor.QueryParameters)
                args.Add(new McpPromptArgumentDescriptor { Name = p.Name, Description = p.Description, Required = p.IsRequired });

            var descriptor = new McpPromptDescriptor
            {
                Name = promptAttr.Name,
                Description = promptAttr.Description,
                Arguments = args,
                HttpMethod = dispatchDescriptor.HttpMethod,
                RelativeUrl = dispatchDescriptor.RelativeUrl,
                DispatchDescriptor = dispatchDescriptor
            };

            prompts.Add(descriptor);
            _logger.LogDebug("Registered MCP prompt '{Name}' → {Method} {Url}", descriptor.Name, descriptor.HttpMethod, descriptor.RelativeUrl);
        }

        // Minimal API endpoints tagged with .AsPrompt()
        foreach (var endpoint in _endpointDataSource.Endpoints)
        {
            var promptMeta = endpoint.Metadata.GetMetadata<McpPromptEndpointMetadata>();
            if (promptMeta is null) continue;

            var dispatch = BuildMinimalDispatchDescriptor(endpoint);

            var args = new List<McpPromptArgumentDescriptor>();
            foreach (var p in dispatch.RouteParameters)
                args.Add(new McpPromptArgumentDescriptor { Name = p.Name, Required = p.IsRequired });
            foreach (var p in dispatch.QueryParameters)
                args.Add(new McpPromptArgumentDescriptor { Name = p.Name, Required = p.IsRequired });

            prompts.Add(new McpPromptDescriptor
            {
                Name = promptMeta.Name,
                Description = promptMeta.Description,
                Arguments = args,
                HttpMethod = dispatch.HttpMethod,
                RelativeUrl = dispatch.RelativeUrl,
                DispatchDescriptor = dispatch
            });
            _logger.LogDebug("Registered MCP prompt (minimal) '{Name}' → {Method} {Url}", promptMeta.Name, dispatch.HttpMethod, dispatch.RelativeUrl);
        }

        var byName = new Dictionary<string, McpPromptDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in prompts)
            byName[p.Name] = p;

        _prompts = prompts;
        _byName = byName;

        _logger.LogInformation("ZeroMCP: discovered {Count} MCP prompt(s)", prompts.Count);
    }

    private McpToolDescriptor BuildMinimalDispatchDescriptor(Endpoint endpoint)
    {
        var routeParams = new List<McpParameterDescriptor>();
        var queryParams = new List<McpParameterDescriptor>();
        var httpMethod = "GET";
        var relativeUrl = "";

        if (endpoint is RouteEndpoint routeEndpoint)
        {
            relativeUrl = routeEndpoint.RoutePattern.RawText?.TrimStart('/') ?? "";
            foreach (var param in routeEndpoint.RoutePattern.Parameters)
            {
                routeParams.Add(new McpParameterDescriptor
                {
                    Name = param.Name ?? "",
                    ParameterType = typeof(string),
                    IsRequired = !param.IsOptional
                });
            }
        }

        var methodMeta = endpoint.Metadata.GetMetadata<HttpMethodMetadata>();
        if (methodMeta?.HttpMethods is { Count: > 0 })
            httpMethod = methodMeta.HttpMethods[0];

        var apiDesc = ApiDescriptionResolver.Resolve(_apiDescriptionProvider, endpoint, relativeUrl, httpMethod);
        if (apiDesc is not null)
        {
            foreach (var param in apiDesc.ParameterDescriptions)
            {
                if (param.Type == typeof(CancellationToken) || param.Source.Id == "Path")
                    continue;
                if (param.Source.Id == "Query")
                {
                    queryParams.Add(new McpParameterDescriptor
                    {
                        Name = param.Name,
                        ParameterType = param.Type ?? typeof(string),
                        IsRequired = param.IsRequired || (param.ModelMetadata?.IsRequired == true),
                        Description = param.ModelMetadata?.Description,
                        DefaultValue = param.DefaultValue
                    });
                }
            }
        }

        return new McpToolDescriptor
        {
            Name = "",
            ApiDescription = null,
            ActionDescriptor = null,
            Endpoint = endpoint,
            RouteParameters = routeParams,
            QueryParameters = queryParams,
            Body = null,
            ApiDescriptionResolved = apiDesc is not null,
            FormFileParameters = [],
            FormParameters = [],
            HttpMethod = httpMethod,
            RelativeUrl = relativeUrl
        };
    }

    private McpToolDescriptor BuildDispatchDescriptor(
        ApiDescription apiDescription,
        ControllerActionDescriptor controllerDescriptor)
    {
        var routeParams = new List<McpParameterDescriptor>();
        var queryParams = new List<McpParameterDescriptor>();
        McpBodyDescriptor? body = null;

        foreach (var param in apiDescription.ParameterDescriptions)
        {
            if (param.Type == typeof(CancellationToken))
                continue;

            switch (param.Source.Id)
            {
                case "Path":
                    routeParams.Add(new McpParameterDescriptor
                    {
                        Name = param.Name,
                        ParameterType = param.Type ?? typeof(string),
                        IsRequired = param.IsRequired,
                        Description = param.ModelMetadata?.Description
                    });
                    break;

                case "Query":
                    queryParams.Add(new McpParameterDescriptor
                    {
                        Name = param.Name,
                        ParameterType = param.Type ?? typeof(string),
                        // ApiParameterDescription.IsRequired only covers route / BindRequired;
                        // ModelMetadata.IsRequired also honours [Required] from DataAnnotations.
                        IsRequired = param.IsRequired || (param.ModelMetadata?.IsRequired == true),
                        Description = param.ModelMetadata?.Description,
                        DefaultValue = param.DefaultValue
                    });
                    break;

                case "Body":
                    body = new McpBodyDescriptor
                    {
                        BodyType = param.Type ?? typeof(object),
                        ParameterName = param.Name
                    };
                    break;
            }
        }

        return new McpToolDescriptor
        {
            Name = controllerDescriptor.ActionName,
            ApiDescription = apiDescription,
            ActionDescriptor = controllerDescriptor,
            Endpoint = FindEndpointForAction(controllerDescriptor),
            RouteParameters = routeParams,
            QueryParameters = queryParams,
            Body = body,
            FormFileParameters = [],
            FormParameters = [],
            HttpMethod = apiDescription.HttpMethod ?? "GET",
            RelativeUrl = apiDescription.RelativePath ?? ""
        };
    }

    private Endpoint? FindEndpointForAction(ControllerActionDescriptor controllerDescriptor)
    {
        foreach (var endpoint in _endpointDataSource.Endpoints)
        {
            var actionMeta = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
            if (actionMeta is null) continue;
            if (string.Equals(actionMeta.Id, controllerDescriptor.Id, StringComparison.Ordinal))
                return endpoint;
            if (string.Equals(actionMeta.ControllerName, controllerDescriptor.ControllerName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(actionMeta.ActionName, controllerDescriptor.ActionName, StringComparison.OrdinalIgnoreCase))
                return endpoint;
        }
        return null;
    }
}
