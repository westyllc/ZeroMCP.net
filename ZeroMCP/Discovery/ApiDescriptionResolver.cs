using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;

namespace ZeroMCP.Discovery;

/// <summary>
/// Resolves the <see cref="ApiDescription"/> (query/body parameter metadata from ApiExplorer) for a
/// minimal-API <see cref="Endpoint"/>. Shared by the tool, resource and prompt discovery services.
/// </summary>
/// <remarks>
/// A plain string comparison of the endpoint's raw route template against <see cref="ApiDescription.RelativePath"/>
/// fails for API-versioned routes: the template is <c>api/v{version:apiVersion}/…</c> while ApiExplorer
/// (with Asp.Versioning's <c>SubstituteApiVersionInUrl</c>) reports <c>api/v1/…</c>. Resolution order:
/// <list type="number">
/// <item>Endpoint identity: an <c>Endpoint</c> property on the ActionDescriptor (if a future framework exposes one),
/// else reference-identity of the <see cref="ActionDescriptor.EndpointMetadata"/> items, which ApiExplorer copies
/// from the endpoint's metadata collection (verified on net8/net9/net10).</item>
/// <item>Exact relative-path match (the historical behavior).</item>
/// <item>Pattern equivalence: every <c>{…}</c> route segment becomes a single-segment wildcard, matched against
/// candidates with the same HTTP method and segment count.</item>
/// </list>
/// Returns null when nothing matches. Controller actions are never candidates.
/// </remarks>
internal static class ApiDescriptionResolver
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> EndpointPropertyCache = new();

    public static ApiDescription? Resolve(
        IApiDescriptionGroupCollectionProvider provider,
        Endpoint endpoint,
        string relativeUrl,
        string httpMethod)
    {
        var candidates = provider.ApiDescriptionGroups.Items
            .SelectMany(g => g.Items)
            .Where(d => d.ActionDescriptor is not ControllerActionDescriptor
                        && string.Equals(d.HttpMethod ?? "", httpMethod, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
            return null;

        // (a) Endpoint identity
        foreach (var desc in candidates)
        {
            if (IsSameEndpoint(desc.ActionDescriptor, endpoint))
                return desc;
        }

        var normalized = NormalizePath(relativeUrl);

        // (b1) Exact path match — preserves pre-existing behavior for unversioned routes
        foreach (var desc in candidates)
        {
            if (string.Equals(NormalizePath(desc.RelativePath), normalized, StringComparison.OrdinalIgnoreCase))
                return desc;
        }

        // (b2) Pattern equivalence ({version:apiVersion} etc. → single-segment wildcard)
        var regex = BuildTemplateRegex(normalized);
        var segmentCount = CountSegments(normalized);
        foreach (var desc in candidates)
        {
            var path = NormalizePath(desc.RelativePath);
            if (CountSegments(path) == segmentCount && regex.IsMatch(path))
                return desc;
        }

        return null;
    }

    private static bool IsSameEndpoint(ActionDescriptor actionDescriptor, Endpoint endpoint)
    {
        var prop = EndpointPropertyCache.GetOrAdd(actionDescriptor.GetType(), t =>
        {
            var p = t.GetProperty("Endpoint", BindingFlags.Public | BindingFlags.Instance);
            return p is not null && typeof(Endpoint).IsAssignableFrom(p.PropertyType) ? p : null;
        });
        if (prop is not null)
            return ReferenceEquals(prop.GetValue(actionDescriptor), endpoint);

        var described = actionDescriptor.EndpointMetadata;
        var actual = endpoint.Metadata;
        if (described is null || described.Count == 0 || described.Count != actual.Count)
            return false;
        for (var i = 0; i < described.Count; i++)
        {
            if (!ReferenceEquals(described[i], actual[i]))
                return false;
        }
        return true;
    }

    internal static string NormalizePath(string? path)
    {
        var p = (path ?? "").Trim('/');
        while (p.Contains("//", StringComparison.Ordinal))
            p = p.Replace("//", "/", StringComparison.Ordinal);
        return p;
    }

    private static int CountSegments(string normalizedPath) =>
        normalizedPath.Length == 0 ? 0 : normalizedPath.Split('/').Length;

    /// <summary>Escapes literal text and turns every <c>{…}</c> parameter (brace-depth aware) into <c>[^/]+</c>.</summary>
    internal static Regex BuildTemplateRegex(string normalizedTemplate)
    {
        var sb = new StringBuilder("^");
        var literal = new StringBuilder();
        var i = 0;
        while (i < normalizedTemplate.Length)
        {
            var c = normalizedTemplate[i];
            if (c == '{' && i + 1 < normalizedTemplate.Length && normalizedTemplate[i + 1] == '{')
            {
                literal.Append('{'); i += 2; continue;
            }
            if (c == '}' && i + 1 < normalizedTemplate.Length && normalizedTemplate[i + 1] == '}')
            {
                literal.Append('}'); i += 2; continue;
            }
            if (c == '{')
            {
                sb.Append(Regex.Escape(literal.ToString()));
                literal.Clear();
                var depth = 0;
                while (i < normalizedTemplate.Length)
                {
                    if (normalizedTemplate[i] == '{') depth++;
                    else if (normalizedTemplate[i] == '}' && --depth == 0) { i++; break; }
                    i++;
                }
                sb.Append("[^/]+");
                continue;
            }
            literal.Append(c);
            i++;
        }
        sb.Append(Regex.Escape(literal.ToString()));
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
