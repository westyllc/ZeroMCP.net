using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using ZeroMCP.Discovery;

namespace ZeroMCP.Dispatch;

/// <summary>
/// The result of an in-process MCP tool dispatch.
/// </summary>
public sealed class DispatchResult
{
    public bool IsSuccess { get; init; }
    public int StatusCode { get; init; }
    public string Content { get; init; } = string.Empty;
    public string ContentType { get; init; } = "application/json";

    public static DispatchResult Success(int statusCode, string content, string contentType = "application/json") =>
        new() { IsSuccess = true, StatusCode = statusCode, Content = content, ContentType = contentType };

    public static DispatchResult Failure(int statusCode, string error) =>
        new() { IsSuccess = false, StatusCode = statusCode, Content = error };
}

/// <summary>
/// Dispatches MCP tool calls directly in-process by constructing a synthetic HttpContext,
/// invoking the action through ASP.NET Core's full pipeline, and capturing the response.
/// 
/// This means all action filters, validation, and authorization attributes on the target
/// controller action execute normally — MCP is just another caller.
/// </summary>
public sealed class McpToolDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SyntheticHttpContextFactory _contextFactory;
    private readonly IActionDescriptorCollectionProvider _actionDescriptorProvider;
    private readonly ILogger<McpToolDispatcher> _logger;

    public McpToolDispatcher(
        IServiceScopeFactory scopeFactory,
        SyntheticHttpContextFactory contextFactory,
        IActionDescriptorCollectionProvider actionDescriptorProvider,
        ILogger<McpToolDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _contextFactory = contextFactory;
        _actionDescriptorProvider = actionDescriptorProvider;
        _logger = logger;

        if (!HttpContextAccessorScope.IsSupported)
            _logger.LogWarning("ZeroMCP could not recognise HttpContextAccessor internals on this framework; " +
                "IHttpContextAccessor.HttpContext will resolve to the outer MCP request during tool dispatch.");
    }

    /// <summary>
    /// Dispatches the given tool with the provided JSON arguments.
    /// Returns the serialized response from the action.
    /// </summary>
    /// <param name="sourceContext">Optional MCP request context; when set, configured headers (e.g. Authorization) are forwarded to the synthetic request.</param>
    public async Task<DispatchResult> DispatchAsync(
        McpToolDescriptor descriptor,
        IReadOnlyDictionary<string, JsonElement> args,
        CancellationToken cancellationToken = default,
        HttpContext? sourceContext = null)
    {
        _logger.LogDebug("Dispatching MCP tool '{ToolName}' with {ArgCount} argument(s)",
            descriptor.Name, args.Count);

        // Each dispatch gets its own DI scope, mirroring real request scoping
        await using var scope = _scopeFactory.CreateAsyncScope();

        HttpContext context;
        try
        {
            context = _contextFactory.Build(descriptor, args, scope, sourceContext, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build synthetic HttpContext for tool '{ToolName}'", descriptor.Name);
            return DispatchResult.Failure(400, $"Failed to bind arguments: {ex.Message}");
        }

        // IHttpContextAccessor must resolve to the synthetic request (not the outer POST /mcp) for authorization,
        // filters and the action; disposal (end of method, i.e. a finally) restores the previous value.
        using var accessorScope = _contextFactory.EnterHttpContextAccessorScope(context, scope.ServiceProvider);

        // Set endpoint when available so pipeline (e.g. CreatedAtAction, LinkGenerator) sees a matched endpoint
        if (descriptor.Endpoint is not null)
            context.SetEndpoint(descriptor.Endpoint);

        var authResult = await EnforceAuthorizationAsync(descriptor, context, scope.ServiceProvider);
        if (authResult is not null)
            return authResult; 


        if (descriptor.Endpoint is not null && descriptor.ActionDescriptor is null)
        {
            return await DispatchMinimalEndpointAsync(descriptor, context);
        }

        if (descriptor.ActionDescriptor is null)
        {
            _logger.LogError("Tool '{ToolName}' has neither ActionDescriptor nor Endpoint", descriptor.Name);
            return DispatchResult.Failure(500, "Invalid tool descriptor");
        }

        // Controller action path: build ActionContext and invoke via IActionInvokerFactory
        var routeData = new RouteData(context.Request.RouteValues);
        var actionContext = new ActionContext(context, routeData, descriptor.ActionDescriptor!);

        // Get the invoker factory and create an invoker for this action
        var invokerFactory = scope.ServiceProvider.GetRequiredService<IActionInvokerFactory>();
        var invoker = invokerFactory.CreateInvoker(actionContext);

        if (invoker is null)
        {
            _logger.LogError("Could not create action invoker for tool '{ToolName}'", descriptor.Name);
            return DispatchResult.Failure(500, "Failed to create action invoker");
        }

        try
        {
            // Execute the action — this runs the full filter pipeline
            await invoker.InvokeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception during dispatch of tool '{ToolName}'", descriptor.Name);
            return DispatchResult.Failure(500, $"Internal error: {ex.Message}");
        }

        return await ExtractResponseAsync(context, descriptor.Name);
    }

    /// <summary>
    /// Dispatches a streaming tool (IAsyncEnumerable) and yields individual chunks via a Channel.
    /// The action is invoked through the normal pipeline; the IAsyncEnumerable result is
    /// captured by McpStreamingCaptureFormatter and enumerated in a background task.
    /// </summary>
    public IAsyncEnumerable<DispatchStreamChunk> DispatchStreamingAsync(
        McpToolDescriptor descriptor,
        IReadOnlyDictionary<string, JsonElement> args,
        int maxItems,
        CancellationToken cancellationToken = default,
        HttpContext? sourceContext = null)
    {
        var channel = Channel.CreateUnbounded<DispatchStreamChunk>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _ = ProduceStreamingChunksAsync(channel.Writer, descriptor, args, maxItems, cancellationToken, sourceContext);
        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    private async Task ProduceStreamingChunksAsync(
        ChannelWriter<DispatchStreamChunk> writer,
        McpToolDescriptor descriptor,
        IReadOnlyDictionary<string, JsonElement> args,
        int maxItems,
        CancellationToken cancellationToken,
        HttpContext? sourceContext)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();

            HttpContext context;
            try
            {
                context = _contextFactory.Build(descriptor, args, scope, sourceContext, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build synthetic HttpContext for streaming tool '{ToolName}'", descriptor.Name);
                await writer.WriteAsync(new DispatchStreamChunk { Content = $"Failed to bind arguments: {ex.Message}", IsLast = true, IsError = true }, cancellationToken);
                return;
            }

            // Covers authorization, invocation and enumeration of the captured stream; restored on exit.
            using var accessorScope = _contextFactory.EnterHttpContextAccessorScope(context, scope.ServiceProvider);

            if (descriptor.Endpoint is not null)
                context.SetEndpoint(descriptor.Endpoint);

            var authResult = await EnforceAuthorizationAsync(descriptor, context, scope.ServiceProvider);
            if (authResult is not null)
            {
                await writer.WriteAsync(new DispatchStreamChunk { Content = authResult.Content, IsLast = true, IsError = true }, cancellationToken);
                return;
            }

            context.Items[McpStreamingCaptureFormatter.CaptureFlag] = true;

            try
            {
                if (descriptor.Endpoint is not null && descriptor.ActionDescriptor is null)
                {
                    context.SetEndpoint(descriptor.Endpoint);
                    await descriptor.Endpoint.RequestDelegate!(context);
                }
                else if (descriptor.ActionDescriptor is not null)
                {
                    var routeData = new RouteData(context.Request.RouteValues);
                    var actionContext = new ActionContext(context, routeData, descriptor.ActionDescriptor);
                    var invokerFactory = scope.ServiceProvider.GetRequiredService<IActionInvokerFactory>();
                    var invoker = invokerFactory.CreateInvoker(actionContext);
                    if (invoker is null)
                    {
                        await writer.WriteAsync(new DispatchStreamChunk { Content = "Failed to create action invoker", IsLast = true, IsError = true }, cancellationToken);
                        return;
                    }
                    await invoker.InvokeAsync();
                }
                else
                {
                    await writer.WriteAsync(new DispatchStreamChunk { Content = "Invalid tool descriptor", IsLast = true, IsError = true }, cancellationToken);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception during streaming dispatch of tool '{ToolName}'", descriptor.Name);
                await writer.WriteAsync(new DispatchStreamChunk { Content = $"Internal error: {ex.Message}", IsLast = true, IsError = true }, cancellationToken);
                return;
            }

            if (!context.Items.TryGetValue(McpStreamingCaptureFormatter.CapturedEnumerable, out var captured) || captured is null)
            {
                var fallback = await ExtractResponseAsync(context, descriptor.Name);
                await writer.WriteAsync(new DispatchStreamChunk { Content = fallback.Content, IsLast = true, IsError = !fallback.IsSuccess }, cancellationToken);
                return;
            }

            var itemCount = 0;
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

            await using var enumerator = ((IAsyncEnumerable<object>)captured).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (await enumerator.MoveNextAsync())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    itemCount++;
                    if (maxItems > 0 && itemCount > maxItems)
                    {
                        await writer.WriteAsync(new DispatchStreamChunk
                        {
                            Content = $"MaxStreamingItems limit ({maxItems}) exceeded",
                            IsLast = true,
                            IsError = true
                        }, cancellationToken);
                        return;
                    }

                    var json = JsonSerializer.Serialize(enumerator.Current, jsonOptions);
                    await writer.WriteAsync(new DispatchStreamChunk { Content = json, IsLast = false, IsError = false }, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                await writer.WriteAsync(new DispatchStreamChunk { Content = "Stream cancelled", IsLast = true, IsError = true }, cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during streaming enumeration of tool '{ToolName}'", descriptor.Name);
                await writer.WriteAsync(new DispatchStreamChunk { Content = $"Streaming error: {ex.Message}", IsLast = true, IsError = true }, cancellationToken);
                return;
            }

            await writer.WriteAsync(new DispatchStreamChunk { Content = "", IsLast = true, IsError = false }, cancellationToken);
        }
        finally
        {
            writer.Complete();
        }
    }

    private async Task<DispatchResult> DispatchMinimalEndpointAsync(McpToolDescriptor descriptor, HttpContext context)
    {
        context.SetEndpoint(descriptor.Endpoint!);
        try
        {
            await descriptor.Endpoint!.RequestDelegate!(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception during dispatch of minimal tool '{ToolName}'", descriptor.Name);
            return DispatchResult.Failure(500, $"Internal error: {ex.Message}");
        }
        return await ExtractResponseAsync(context, descriptor.Name);
    }

    private static async Task<DispatchResult?> EnforceAuthorizationAsync(
    McpToolDescriptor descriptor,
    HttpContext context,
    IServiceProvider sp)
    {
        var endpoint = descriptor.Endpoint;
        var authorizeData = endpoint?.Metadata.GetOrderedMetadata<IAuthorizeData>()?.ToList()
                            ?? new List<IAuthorizeData>();

        var allowAnonymous = endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null;

        if (authorizeData.Count == 0 && !allowAnonymous && descriptor.ActionDescriptor is ControllerActionDescriptor cad)
        {
            var methodAttrs = cad.MethodInfo.GetCustomAttributes<AuthorizeAttribute>(inherit: true);
            var classAttrs = cad.ControllerTypeInfo.GetCustomAttributes<AuthorizeAttribute>(inherit: true);
            authorizeData.AddRange(methodAttrs);
            authorizeData.AddRange(classAttrs);

            allowAnonymous = cad.MethodInfo.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) is not null
                          || cad.ControllerTypeInfo.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) is not null;
        }

        if (allowAnonymous || authorizeData.Count == 0)
            return null;

        var policyProvider = sp.GetRequiredService<IAuthorizationPolicyProvider>();
        var policyEvaluator = sp.GetRequiredService<IPolicyEvaluator>();

        var policy = await AuthorizationPolicy.CombineAsync(policyProvider, authorizeData);
        if (policy is null) return null;

        var authenticateResult = await policyEvaluator.AuthenticateAsync(policy, context);
        var authorizeResult = await policyEvaluator.AuthorizeAsync(
                    policy,
                    authenticateResult,
                    context,
                    descriptor.Endpoint is not null ? descriptor.Endpoint : descriptor.ActionDescriptor);

        if (authorizeResult.Challenged)
            return DispatchResult.Failure(StatusCodes.Status401Unauthorized, "Unauthorized");

        if (authorizeResult.Forbidden)
            return DispatchResult.Failure(StatusCodes.Status403Forbidden, "Forbidden");

        return null;
    }

    private async Task<DispatchResult> ExtractResponseAsync(HttpContext context, string toolName)
    {
        var statusCode = context.Response.StatusCode;

        // Rewind the response body stream
        if (context.Response.Body is MemoryStream ms)
        {
            ms.Position = 0;
            var responseBody = await new StreamReader(ms, Encoding.UTF8).ReadToEndAsync();
            var contentType = context.Response.ContentType ?? "application/json";

            _logger.LogDebug("Tool '{ToolName}' returned {StatusCode}, {Bytes} bytes",
                toolName, statusCode, ms.Length);

            if (statusCode >= 200 && statusCode < 300)
                return DispatchResult.Success(statusCode, responseBody, contentType);

            // Non-2xx: still return content but mark as failure
            return DispatchResult.Failure(statusCode, responseBody);
        }

        // No body
        if (statusCode >= 200 && statusCode < 300)
            return DispatchResult.Success(statusCode, string.Empty);

        return DispatchResult.Failure(statusCode, $"Request failed with status {statusCode}");
    }
}
