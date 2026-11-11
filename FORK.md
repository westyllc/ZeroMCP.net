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

## Known upstream issues (not patched)

- `MapZeroMCP()` pre-warms the tool registry before the app's endpoint data sources are registered with routing (this
  happens when the app uses `UseRouting()` without calling `UseEndpoints()` first). Minimal-API tools are then missing,
  and ApiExplorer's cached description collection stays frozen without them. `McpToolDiscoveryService.InvalidateCache()`
  brings the tools back, but they stay *unresolved*. Under patch 3, unsafe calls with body arguments then fail loudly.
  Workaround: call `app.UseEndpoints(_ => { });` after mapping your endpoints and before `app.MapZeroMCP()`.
