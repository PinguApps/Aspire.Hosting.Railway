# Retained image update validation

Validated on 2 October 2026 using a locally packed, unpublished `PinguApps.Aspire.Hosting.Railway` 1.0.1 package, Aspire 13.6.0, and the existing dedicated [Core Integration allocation](https://railway.com/project/c702abd1-b897-4c2d-a7d9-3468fad4271a). The consumer and isolated package caches were outside the repository at `V:/pin646-core-image-update-consumer-v2`.

The 1.0.0 sequence committed configuration with `skipDeploys: true` then called `serviceInstanceDeployV2`. In the integrated template test, Railway configured the new source but launched the previous image. A trial of `serviceInstanceDeploy(latestCommit: true)` also launched the previous web image during a live update; the new digest guard rejected it. The final correction commits the complete source/settings/runtime-variable patch with deployments enabled and tracks its one exact scoped deployment. No additional redeploy is issued for that patch.

| Service | Image A | Image B | Exact successful B deployment |
|---|---|---|---|
| `image-update-web-proof`, `e7d971cc-e788-4599-b881-b747af2eef9f` | `traefik/whoami@sha256:c4717a8d1f0134a7444e24f881160e033991f23027c6c5a9a3f8fd22e70d1d44` | `traefik/whoami@sha256:43a68d10b9dfcfc3ffbfe4dd42100dc9aeaf29b3a5636c856337a5940f1b4f1c` | `28e40f79-f968-4263-a804-b48d5c7b7fed` |
| `image-update-job-proof`, `bfc52faf-27d2-483a-b386-60bf91f8398f` | `busybox@sha256:73aaf090f3d85aa34ee199857f03fa3a95c8ede2ffd4cc2cdb5b94e566b11662` | `busybox@sha256:bdf57e528e45e4433820e045b29b4597825a1c9e38353532d90a01445013f82e` | `7b2e59ca-b3e5-454a-9cbc-50c77ea4a46a` |

Both A and B packed consumer deployments completed all 11 steps. Provider metadata matched each requested image exactly. The finite job reported `SUCCESS`, stopped, and `EXITED` before the dependent web step ran. The [public web endpoint](https://image-update-web-proof-production.up.railway.app) returned HTTP 200; the web service remains running for inspection.

A repeat B deployment also passed all 11 steps. It preserved web deployment `28e40f79-f968-4263-a804-b48d5c7b7fed` and intentionally ran the finite job again as `ae76a309-1fb7-4038-a23f-1e02caaba569`, with the same requested B digest, stopped state, and `EXITED` instance.

A separate scoped provider probe first configured a new source with skipped deployment, then committed that identical source with deployments enabled, without an extra changed variable. Railway launched the configured image. This verifies recovery when configuration already points to the desired image but the latest deployment still uses its predecessor.

All 97 tests passed, including the live scoped-token test; the packed-NuGet TypeScript gate passed. Contract tests reproduce the previous source-selection behavior, validate ordinary and finite image updates, reject wrong or missing successful image metadata, and verify recovery of lost configuration-commit and explicit-deploy responses without a second accepted execution. The existing ownership, scope, secrets, partial application, and pipeline tests remain active.

This package is not published. Template consumption requires maintainer review/merge/publication of 1.0.1 followed by the template dependency update. The existing 1.0.0 publication cannot be overwritten.
