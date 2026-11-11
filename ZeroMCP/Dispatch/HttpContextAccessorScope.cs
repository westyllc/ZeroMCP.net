using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace ZeroMCP.Dispatch;

/// <summary>
/// Makes the framework <see cref="HttpContextAccessor"/> resolve to a synthetic <see cref="HttpContext"/>
/// for the current async flow, and restores the previous value on <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// <para>
/// The public <see cref="HttpContextAccessor.HttpContext"/> setter cannot be used here. It nulls the
/// <c>Context</c> of the holder that is current when it is called. That holder is shared with the outer
/// <c>POST /mcp</c> request and with every flow that captured it. Calling the setter would therefore make
/// <c>IHttpContextAccessor.HttpContext</c> return null for the outer request once dispatch returns, and
/// restoring the old value afterwards inside the dispatcher's async method would not reach the caller's flow.
/// </para>
/// <para>
/// Instead, this swaps the accessor's static <c>AsyncLocal&lt;HttpContextHolder&gt;</c>, which is private, to a
/// NEW holder for the dispatch flow. The outer holder is never mutated. On dispose, the synthetic holder is
/// cleared, which matches the framework's own "context is done" behaviour for flows the handler forked, and the
/// previous holder is put back. The private members have the same shape on net8.0, net9.0 and net10.0. If a
/// future framework changes them, <see cref="IsSupported"/> is false and no swap happens. A test pins this.
/// </para>
/// </remarks>
internal sealed class HttpContextAccessorScope : IDisposable
{
    private static readonly object? AsyncLocalInstance;
    private static readonly PropertyInfo? AsyncLocalValue;
    private static readonly Type? HolderType;
    private static readonly FieldInfo? HolderContext;

    static HttpContextAccessorScope()
    {
        try
        {
            var field = typeof(HttpContextAccessor).GetField("_httpContextCurrent", BindingFlags.NonPublic | BindingFlags.Static);
            var holderType = typeof(HttpContextAccessor).GetNestedType("HttpContextHolder", BindingFlags.NonPublic);
            var contextField = holderType?.GetField("Context", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var asyncLocal = field?.GetValue(null);
            var valueProp = asyncLocal?.GetType().GetProperty("Value");

            if (holderType is null || contextField is null || asyncLocal is null || valueProp is null
                || contextField.FieldType != typeof(HttpContext)
                || valueProp.PropertyType != holderType)
                return;

            AsyncLocalInstance = asyncLocal;
            AsyncLocalValue = valueProp;
            HolderType = holderType;
            HolderContext = contextField;
        }
        catch
        {
            // Leave unsupported; Enter() becomes a no-op.
        }
    }

    /// <summary>True when the framework accessor internals were found and the swap can be performed.</summary>
    internal static bool IsSupported => AsyncLocalInstance is not null;

    private readonly object? _previousHolder;
    private readonly object _syntheticHolder;
    private bool _disposed;

    private HttpContextAccessorScope(object? previousHolder, object syntheticHolder)
    {
        _previousHolder = previousHolder;
        _syntheticHolder = syntheticHolder;
    }

    /// <summary>
    /// Points the framework accessor at <paramref name="context"/> for the current async flow.
    /// Returns null when the swap is not supported on this framework.
    /// </summary>
    internal static HttpContextAccessorScope? Enter(HttpContext context)
    {
        if (!IsSupported)
            return null;

        var previous = AsyncLocalValue!.GetValue(AsyncLocalInstance);
        var holder = Activator.CreateInstance(HolderType!, nonPublic: true)!;
        HolderContext!.SetValue(holder, context);
        AsyncLocalValue.SetValue(AsyncLocalInstance, holder);
        return new HttpContextAccessorScope(previous, holder);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        HolderContext!.SetValue(_syntheticHolder, null);
        AsyncLocalValue!.SetValue(AsyncLocalInstance, _previousHolder);
    }
}
