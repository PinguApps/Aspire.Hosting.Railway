# Source-build deployment rehearsal

The source-build implementation was exercised against a disposable Railway project on 10 October 2026 using .NET 10, Aspire CLI 13.6.0, Railway CLI 5.63.1, and a locally packed `PinguApps.Aspire.Hosting.Railway` 1.1.0. These were real `aspire deploy` runs, not mocked provider calls. No GitHub registry credential was supplied.

## Repeat the core rehearsal

Allocate a disposable Railway project/environment and a scoped project token. Set these process environment variables without committing their values:

- `LIVE_RAILWAY_PROJECT_ID`, `LIVE_RAILWAY_ENVIRONMENT_ID`, `LIVE_RAILWAY_TOKEN`, `LIVE_SITE_KEY`.
- `LIVE_DASHBOARD_BROWSER_TOKEN` and `LIVE_DASHBOARD_OTLP_TOKEN`: separate dashboard authentication secrets of at least 32 characters. Keep the same values across repeat runs.
- Optional `LIVE_RAILWAY_CLI`: the Railway executable path. Aspire, .NET, and Railway must otherwise be on PATH.

From the repository, run:

```powershell
pwsh ./eng/Test-LiveRailwaySourceBuilds.ps1 -WorkDirectory '<external disposable directory>' -ProofVersion v1
pwsh ./eng/Test-LiveRailwaySourceBuilds.ps1 -WorkDirectory '<same directory>' -ProofVersion v1
pwsh ./eng/Test-LiveRailwaySourceBuilds.ps1 -WorkDirectory '<same directory>' -ProofVersion v2
```

The script packs the current source, creates an external consumer with an isolated NuGet cache, and calls `aspire deploy --non-interactive`. It creates two source-built services, a public immutable-image service, and a dashboard using the existing dashboard package 1.0.1. It does not delete cloud resources. Preserve its Aspire deployment state after a failure so the next run reconciles the exact recorded upload.

Check the source services' public URLs from the Aspire summary: project HTTP 200 must contain `RAILWAY_PROJECT_BUILD_v1_runtime-binding-passed`, and container HTTP 200 must contain `RAILWAY_CONTAINER_BUILD_v1`. The second run must retain their deployment IDs. The third run must produce the corresponding `v2` bodies and new source deployment IDs. The immutable image must keep returning `LEGACY_IMAGE_READY`; unauthenticated dashboard access must redirect to `/login`.

The fixture deliberately excludes its Dockerfile and `.dockerignore` in Docker ignore rules. Its container selects `docker/Dockerfile` below an excluded parent directory. Successful real builds prove that the upload preserves the selected build control files. Context copying and cleanup happen only in the marked external consumer directory; credentials and deployment state are not copied into source contexts.

## Observed results

Initial deployment, unchanged replay, changed build argument, and ignored/nested Dockerfile runs all completed successfully. The final checked-in harness run completed all 13 deployment steps in 36 seconds. Final HTTP evidence was:

| Resource | Exact deployment | HTTP result |
| --- | --- | --- |
| Project source build | `a7221fb7-4cdc-4a12-94e7-7916807f8615` | 200, `RAILWAY_PROJECT_BUILD_v2_runtime-binding-passed` |
| Container source build | `4c3188f4-68d4-46ba-8f65-5c6439e8f568` | 200, `RAILWAY_CONTAINER_BUILD_v2` |
| Existing immutable image | `31cd5fa3-eeac-4eca-937f-d1e2a839a137` | 200, `LEGACY_IMAGE_READY`; unchanged deployment |
| Existing dashboard package | `af2b3796-45d7-4e64-900c-a461ca19b760` | 200 login page after authentication redirect; unchanged deployment |

The source snapshots and provider-reported image digests were captured alongside these deployment IDs. The unchanged `v1` run reused all four exact deployments. Changing the source build argument rebuilt only the source services.

A separate mixed core/Jobs consumer also exercised finite success before a dependent web deployment, a failing finite process, a bounded finite timeout, source cron, source-content changes without changing a build argument, and existing-image/source transitions in both directions. Workload-variable inspection verified that neither Railway control-plane tokens nor GitHub tokens reached the source services.

Real deployments exposed two provider behaviors that the implementation now handles: build metadata can initially contain default builder/path values before the requested Dockerfile configuration appears, and cached finite builds can omit the optional image digest. Request scope, snapshot marker, and CLI upload message remain mandatory; the publisher waits on the exact deployment and never fabricates an image identity.

Repository checks also passed: 155 tests with zero failures or skips, and the packed TypeScript AppHost restore, typecheck, publish-step listing, and deploy dependency listing.
