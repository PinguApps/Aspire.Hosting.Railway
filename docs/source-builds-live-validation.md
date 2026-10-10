# Source-build deployment rehearsal

The source-build implementation was exercised against a disposable Railway project on 10 October 2026 using .NET 10, Aspire CLI 13.6.0, Railway CLI 5.63.1, and a locally packed `PinguApps.Aspire.Hosting.Railway` 1.1.0. These were real `aspire deploy` runs, not mocked provider calls. No GitHub registry credential was supplied.

## Repeat the core rehearsal

Allocate a disposable Railway project/environment and a scoped project token. Add the shared environment variable `PINGUAPPS_SITE_KEY` in Railway with the same value you will supply as `LIVE_SITE_KEY`; the publisher refuses environments without this allocation marker. Set these process environment variables without committing their values:

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

The disposable project was deleted after verification. Deployment IDs and responses below are historical evidence, not currently running endpoints.

Initial deployment, unchanged replay, changed build argument, and ignored/nested Dockerfile runs all completed successfully. The nested Dockerfile upload run completed all 13 deployment steps in 36 seconds. A final replay with config-as-code guards and cooperative CLI cleanup passed 13/13 steps in 7.64 seconds, retaining all four exact deployment IDs. Final HTTP evidence was:

| Resource | Exact deployment | HTTP result |
| --- | --- | --- |
| Project source build | `a7221fb7-4cdc-4a12-94e7-7916807f8615` | 200, `RAILWAY_PROJECT_BUILD_v2_runtime-binding-passed` |
| Container source build | `4c3188f4-68d4-46ba-8f65-5c6439e8f568` | 200, `RAILWAY_CONTAINER_BUILD_v2` |
| Existing immutable image | `31cd5fa3-eeac-4eca-937f-d1e2a839a137` | 200, `LEGACY_IMAGE_READY`; unchanged deployment |
| Existing dashboard package | `af2b3796-45d7-4e64-900c-a461ca19b760` | 200 login page after authentication redirect; unchanged deployment |

The source snapshots and provider-reported image digests were captured alongside these deployment IDs. The unchanged `v1` run reused all four exact deployments. Changing the source build argument rebuilt only the source services.

A separate mixed core/Jobs consumer also exercised finite success before a dependent web deployment, a failing finite process, a bounded finite timeout, source cron, source-content changes without changing a build argument, and existing-image/source transitions in both directions. Workload-variable inspection verified that neither Railway control-plane tokens nor GitHub tokens reached the source services.

## Recovery after revoking the upload credential

After both package rehearsals finished, the scoped upload token was revoked. Its original lookup was rejected with `Project Token not found`. Railway then restarted the mixed consumer's source project/container successfully. An independently authenticated Railway account redeployed their retained builds without the original token, another source upload, or a GitHub PAT:

| Source service | Exact retained-build redeployment | Result |
| --- | --- | --- |
| Project | `da46a9f2-ee7d-49db-bd6c-03e064d38a71` | SUCCESS; HTTP 200, `RAILWAY_PROJECT_BUILD_v2_runtime-binding-passed_CODE_UPDATED` |
| Container | `793a7266-bcef-4dc7-a161-7eb082aabda6` | SUCCESS; HTTP 200, `RAILWAY_CONTAINER_BUILD_v2` |

Both reached SUCCESS at 22:07:45 UTC; HTTP responses were checked at 22:07:57 UTC. Provider image digests matched the preceding source builds, and workload credentials remained absent. This directly tested credential independence after revocation; it did not wait several days. Railway's own retention rules still apply.

To repeat this recovery check before cleanup, record the completed source deployment IDs/digests, revoke the disposable scoped upload token in project settings, and confirm it no longer authenticates. Using your separate Railway account session, select each source deployment's **Restart**, verify readiness/HTTP response, then **Redeploy** using its previous image. Confirm the new deployment succeeds, keeps the image digest when supplied, and returns the same application marker without a source upload. A pull/build authentication error or a changed application marker is a failure. Do not revoke credentials used by unrelated projects.

Finally delete only the disposable project in Railway. The rehearsal project was removed successfully, and cached secret parameters were removed from the task consumers while retaining non-secret deployment evidence.

Real deployments exposed two provider behaviors that the implementation now handles: build metadata can initially contain default builder/path values before the requested Dockerfile configuration appears, and cached finite builds can omit the optional image digest. Request scope, snapshot marker, and CLI upload message remain mandatory; the publisher waits on the exact deployment and never fabricates an image identity.

Repository checks also passed: 159 tests with zero failures or skips, and the packed TypeScript AppHost restore, typecheck, publish-step listing, and deploy dependency listing. An external subprocess rehearsal cancelled a real long-lived CLI child and verified that cancellation returned only after child termination, preserving the snapshot cleanup order.

## Nested Dockerfile upload boundary regression

A second disposable allocation exercised the review fix that stages nested Dockerfiles at the reserved root transport path. Both services used a Dockerfile that copied the entire uploaded context and asserted its contents during the real Railway build. Global Docker ignore rules did not exclude the `prod.secrets` sentinel, so its absence demonstrated the Railway CLI archive boundary rather than a later Docker filter.

| Context case | Exact deployment | Build and HTTP proof |
| --- | --- | --- |
| Dockerfile parent excluded only by `.railwayignore` | `f2d07d6b-f52b-4e59-b3e9-efd176fae6f6` | SUCCESS; ignored sibling absent; HTTP 200, `IGNORED_PARENT_UPLOAD_OK` |
| Allowed Dockerfile parent with an ignored sibling | `0056bd40-69af-43b2-9b4c-1465760a24ed` | SUCCESS; ignored sibling absent, required sibling retained, selected Dockerfile-specific ignore honored; HTTP 200, `ALLOWED_PARENT_INPUTS_OK` |

The actual deployment completed 11/11 steps in 33.04 seconds. Provider configuration selected `.pinguapps-railway.Dockerfile`; the original declared nested paths were unchanged. This verifies that retaining the selected Dockerfile neither unignores its siblings nor excludes otherwise permitted application inputs.

An unchanged replay using the final compiled review changes passed 11/11 steps in 5.61 seconds at 22:23:11 UTC, retaining both exact deployment IDs. Provider SUCCESS and HTTP 200 markers were reverified at 22:25:54 UTC. The second disposable project and its private task-consumer deployment state were removed after verification at 22:25:57 UTC.

The final review changes also passed all 31 focused source-build scenarios and the packed TypeScript gate. The complete suite passed 164 tests with zero failures; its one optional live scoped-token test was skipped because the original disposable upload token had already been revoked. Its earlier live run and the independently authenticated real upload checks above remain recorded separately.
