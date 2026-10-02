# Deployment correlation readiness

On 2 October 2026, a published 1.0.1 Template deployment repeat failed its request-association check. The exact activation deployment later showed the correct image, marker, scope, patch, and successful process termination. Its failing response was not captured, so attributing that historical failure to missing snapshot data remains an inference.

A separate bounded probe using the official NuGet 1.0.1 package established the provider timing behavior directly. It ran one harmless Busybox `echo`/exit-0 job in the dedicated [Core Integration project](https://railway.com/project/c702abd1-b897-4c2d-a7d9-3468fad4271a), adopting the existing `image-update-job-proof` service without changing its image or command.

| Read | Exact deployment | Observation |
| --- | --- | --- |
| 21:59:07.553 UTC | `92410c8e-1b70-4f40-834e-b090e8d29220` | Scope, metadata patch ID, snapshot, and variables were present; request marker was null. |
| 21:59:08.110 UTC | Same ID | Request marker matched the recorded request, 0.558 seconds later. |
| 21:59:08.567 UTC | Same ID | The publisher's later correlation query matched and the finite job passed. |

Read-only sampling and response observation recorded only IDs, the nonsecret request marker, field-presence flags, and match results. They neither altered provider responses nor sent additional deployment requests. The scope token and raw deployment state remain outside the repository.

The readiness fix polls only missing fields for the selected exact ID, using the existing overall deadline. Twenty-six focused regression cases cover missing deployment/scope/metadata/patch/snapshot/variables/marker fields, immediate present-value mismatches, timeout, cancellation, in-flight read deadlines, and finite-attempt resume without another execution. No public API or TypeScript contract changes are required.

The locally packed, unpublished 1.0.2 fix passed 133 nonlive tests, the configured live scope test, version-pin validation, and the packed TypeScript gate. Its external isolated consumer then deployed that same harmless job as `59c6caa5-5f2d-41ed-96bf-3c7998622ea6`: exact Busybox digest, `SUCCESS`, stopped, and instance `EXITED`. Correlation was already complete on that read; this deployment verifies the packed route without claiming it naturally exercised missing-field polling.

The complete Template v0.1.7 graph also passed both first deployment and same-state repeat with the local 1.0.2 package: 19/19 steps, eight successful retained services, five unchanged long-running deployment IDs, three new finite executions, and four exact requested workload digests. Health, cache, media, release identity, sixteen CDN hash/length/source checks, and RabbitMQ access/active-connection checks passed. These checks used an external worktree and isolated package cache; the official 1.0.1 cache was not modified.

Maintainer review, merge, and NuGet publication are still required. Local-package validation does not demonstrate that the published 1.0.1 package has been corrected, and the original GitHub Actions repeat remains failed.
