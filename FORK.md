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
   members are identical on net8/9/10. If a future framework changes them, the swap is impossible and dispatch is
   refused (patch 3, item 6), and the test `FrameworkInternals_AreRecognised` fails on that TFM.

### Patch 3 — fail closed, unambiguous resolution, gating CI (security review follow-up)

6. **Fail-closed accessor scope.** When an `IHttpContextAccessor` is registered but cannot be pointed at the synthetic
   request — a custom (non-framework) implementation, or framework internals ZeroMCP does not recognise —
   `EnterHttpContextAccessorScope` throws `ZeroMcpAccessorScopeRefusedException`. The dispatcher returns a **500**
   naming the reason and the opt-out, and neither authorization nor the tool runs (streaming tools get an error chunk).
   Previously this silently fell back to the outer `POST /mcp` request, whose query string the caller controls.
   No accessor registered remains a no-op (nothing can observe the wrong context).
   **Insecure opt-out:** `ZeroMCPOptions.AllowDegradedAccessorDispatch = true` restores the old behaviour, with a warning
   on every degraded dispatch. The test `OptOut_Hazard_OuterQueryStringSwaysAuthorization` shows what that permits.
7. **`ZeroMcpCompatibility.AccessorScopeSupported`** — public startup signal (mirrors the internal tripwire), so hosts
   can fail fast instead of on the first tool call.
8. **Resolver ambiguity → unresolved.** The pattern tier of `ApiDescriptionResolver` only accepts a *unique* same-shape
   candidate. With several, it returns null and logs a warning, so the fail-loud guard (item 3) stays on instead of
   binding against another endpoint's metadata.
9. **CI gates.** `.github/workflows/dotnet-desktop.yml` runs on `westy/**` pushes/PRs and no longer has
   `continue-on-error` on the test step. The test project (and `ZeroMCP.Sample`, which it references) multi-target
   `net10.0;net9.0;net8.0`, so the tripwires run against each framework the package ships for.
10. **Fixes surfaced by the above.** Form-file tools threw a `NullReferenceException` on net8 (`FormFeature` built without
    a request); the synthetic request now uses a request-aware `FormFeature`. The long-standing `StreamOrders` test flake
    was a race on the Sample's shared order list; the Sample now streams a snapshot.

## Limitations

- During dispatch, only the framework `HttpContextAccessor` (or a subclass) is redirected to the synthetic request. Apps
  that register a custom `IHttpContextAccessor` get every tool call refused with a 500 unless they set
  `AllowDegradedAccessorDispatch` — and with it, accessor reads see the outer MCP request (caller-controlled).
- The swap relies on private `HttpContextAccessor` members (identical on net8/9/10). On a framework where they change,
  tool dispatch is refused while the framework accessor is registered; check `ZeroMcpCompatibility.AccessorScopeSupported`
  at startup.
- A minimal-API endpoint whose ApiDescription can only be matched by shape, and matches several, stays unresolved:
  unsafe calls with non-route arguments are rejected (400) rather than guessed.

## Known upstream issues (not patched)

- `MapZeroMCP()` pre-warms the tool registry before the app's endpoint data sources are registered with routing (this
  happens when the app uses `UseRouting()` without calling `UseEndpoints()` first). Minimal-API tools are then missing,
  and ApiExplorer's cached description collection stays frozen without them. `McpToolDiscoveryService.InvalidateCache()`
  brings the tools back, but they stay *unresolved*. Under patch 1 (item 3), unsafe calls with body arguments then fail loudly.
  Workaround: call `app.UseEndpoints(_ => { });` after mapping your endpoints and before `app.MapZeroMCP()`.
