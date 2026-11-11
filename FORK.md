# Westy fork of ZeroMCP.net

- **Upstream:** https://github.com/ZeroMCP/ZeroMCP.net (remote `upstream`)
- **Fork:** https://github.com/westyllc/ZeroMCP.net (remote `origin`)

## Purpose

Carry the minimal set of patches Westy needs on top of upstream ZeroMCP until they are merged upstream.
The main one closes a production hazard: on API-versioned minimal-API routes (`api/v{version:apiVersion}/…`),
tool arguments were silently diverted to the query string, so required-body calls failed and
optional-body writes silently ran on default values.

Packages built from this fork are versioned `<upstream>-westy.<n>` (e.g. `2.0.0-westy.1`) so they never
collide with, or outrank, an upstream release of the same version.

## Branch policy

- `main` — **pristine mirror** of `upstream/main`. Never commit to it directly; only fast-forward it from upstream.
- `westy/*` — patch branches (e.g. `westy/fix-body-binding`). Each is based on `main` and is **rebased**
  (not merged) onto upstream when syncing, so the patch set stays a clean, reviewable stack on top of upstream.
- When a patch lands upstream, drop it from the `westy/*` branch on the next rebase.

## Syncing with upstream

```bash
export GIT_PAGER=cat
git fetch upstream
git checkout main
git merge --ff-only upstream/main
git push origin main

git checkout westy/fix-body-binding
git rebase main
dotnet build ZeroMCP.slnx && dotnet test ZeroMCP.Tests
git push --force-with-lease origin westy/fix-body-binding
```

When rebasing onto a new upstream version, bump `<Version>` in `ZeroMCP/ZeroMCP.csproj` to
`<new-upstream-version>-westy.1`. Bump only the `-westy.<n>` suffix for fork-only changes.

## Patches on `westy/fix-body-binding`

### Patch 1 — minimal-API body binding on versioned routes

1. **Shared `ApiDescriptionResolver`** (`ZeroMCP/Discovery/ApiDescriptionResolver.cs`). The tool, resource and prompt
   discovery services all use it to resolve a minimal-API endpoint's ApiDescription. It tries, in order: endpoint identity
   (`ActionDescriptor.EndpointMetadata` reference identity, plus an `Endpoint` property if a future framework exposes one),
   then an exact path match, then pattern equivalence (each `{…}` matches one path segment, with the same method and
   segment count).
2. **`McpToolDescriptor.ApiDescriptionResolved`** tells dispatch whether query/body metadata is known.
3. **Fail loud.** `SyntheticHttpContextFactory` throws, and the dispatcher returns a 400 `Failed to bind arguments`, when an
   unresolved minimal-API descriptor gets leftover arguments on POST/PUT/PATCH/DELETE. Safe methods and resolved
   descriptors keep the query-string fallback.
4. **`CanHaveBody`.** Synthetic requests now report `IHttpRequestBodyDetectionFeature.CanHaveBody = true` when they carry a
   JSON body. Without it, minimal-API body binding never read the body.

### Patch 2 — `IHttpContextAccessor` sees the synthetic request during dispatch

5. **Accessor scope.** During tool dispatch, `IHttpContextAccessor.HttpContext` now returns the **synthetic** request
   instead of the outer `POST /mcp`. This covers authorization (`EnforceAuthorizationAsync`), filters, the action, and,
   for streaming tools, enumeration of the result. Without it, requirement handlers that read route values or query
   through the accessor (e.g. Westy's `CustomerTransactionAccess`) saw no id and refused every call with 403.
   `SyntheticHttpContextFactory.EnterHttpContextAccessorScope` resolves the accessor from the dispatch scope. It swaps
   only when the accessor is, or derives from, the framework `HttpContextAccessor`, and the dispatcher disposes the scope
   (a `finally`) once the invocation completes. The public setter is *not* used, because it nulls the holder the outer
   request shares, so the outer request would read null after dispatch. Instead, `HttpContextAccessorScope` installs a
   new holder in the accessor's private static `AsyncLocal` and puts the previous one back on dispose. The private
   members are identical on net8/9/10. If a future framework changes them, the swap becomes a no-op, a warning is logged,
   and the test `FrameworkInternals_AreRecognised` fails.
   **Limitation:** custom (non-framework) `IHttpContextAccessor` implementations are left alone.

## Known upstream issues (not patched)

- `MapZeroMCP()` pre-warms the tool registry before the app's endpoint data sources are registered with routing (this
  happens when the app uses `UseRouting()` without calling `UseEndpoints()` first). Minimal-API tools are then missing,
  and ApiExplorer's cached description collection stays frozen without them. `McpToolDiscoveryService.InvalidateCache()`
  brings the tools back, but they stay *unresolved*. Under patch 3, unsafe calls with body arguments then fail loudly.
  Workaround: call `app.UseEndpoints(_ => { });` after mapping your endpoints and before `app.MapZeroMCP()`.
