using ZeroMCP.Dispatch;

namespace ZeroMCP;

/// <summary>Runtime support signals, so hosts can fail fast at startup instead of on the first tool call.</summary>
public static class ZeroMcpCompatibility
{
    /// <summary>
    /// True when ZeroMCP recognises the framework <c>HttpContextAccessor</c> internals on this runtime and can therefore
    /// point <c>IHttpContextAccessor</c> at the synthetic request during tool dispatch. When false, dispatch is refused
    /// while the framework accessor is registered (unless <c>ZeroMCPOptions.AllowDegradedAccessorDispatch</c> is set).
    /// </summary>
    public static bool AccessorScopeSupported => HttpContextAccessorScope.IsSupported;
}
