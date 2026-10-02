# Configuration

`AddRailwayTarget` takes parameter resources for the recorded project ID, environment ID, infrastructure token, and site key. The token must be secret. The existing environment's shared `PINGUAPPS_SITE_KEY` must already match; deployment never establishes its own ownership marker.

`RailwayTargetOptionsDto.AuthenticationMode` defaults to `ProjectToken`. Explicit `Bearer` mode also requires `ExpectedProjectName` and `ExpectedWorkspaceId` from the allocation record. Project-token authentication validates the exact project/environment scope before writes.

C# callbacks use `RailwayServiceOptions`; TypeScript uses callback-free `RailwayServiceOptionsDto`, with camel-case properties. Exactly one replica is published.

| Option | Default | Meaning / constraint |
| --- | --- | --- |
| `ServiceName` | Aspire resource name | Recorded remote identity; renaming an allocation requires operator reconciliation. |
| `Image` | Pre-attached image annotation | Required immutable `registry/image@sha256:<64 lowercase hex>` reference. Tags are rejected. |
| `OwnershipMode` | `CreateOrAdopt` | See the [ownership rules](../README.md#ownership-and-repeatability). |
| `ExistingServiceId` | Unset | Required to explicitly adopt an unmarked existing service. |
| `StartCommand` | Image default | Optional container command override. |
| `RestartPolicy` | `OnFailure` | `Never`, `OnFailure`, or `Always`. |
| `RestartPolicyMaxRetries` | `3` | Non-negative restart retry limit. |
| `DeploymentTimeout` | 10 minutes | Positive C# `TimeSpan`; TypeScript uses `deploymentTimeoutSeconds` (default 600). |
| `WaitForCompletion` | `false` | Finite execution; requires `Never` restart and no cron schedule. |
| `CronSchedule` | Unset | UTC schedule for an executable that exits; requires `Never`. Use Jobs for schedule validation. |
| `Port` | Unset | Container HTTP port, 1–65535. Required for HTTP domains or readiness checks. |
| `PublicDomain` | `false` | Request a Railway HTTPS domain. Workers remain private by default. |
| `CustomDomains` | Empty | Requested hostnames; DNS configuration remains external. |
| `HealthCheckPath` | Unset | Readiness path beginning with `/`. |
| `Region` | Provider default | Supported names listed below. |
| `MemoryGB` / `VCpus` | Provider default | Positive resource limits. |
| `SleepApplication` | `false` | Explicit serverless sleeping; active brokers/workers may prevent sleep. |
| `Volumes` | Empty | Persistent mounts with unique absolute `MountPath` values below `/`, without `..` segments. |
| `SealedVariables` | Empty | Names of runtime variables to seal; values come from normal Aspire environment bindings. |

Internal regions `ams`, `sfo`, `iad`, and `sin` also accept public aliases `europe-west4-drams3a`, `us-west2`, `us-east4-eqdc4a`, and `asia-southeast1-eqsg3a`, respectively. Existing volume region/mount drift requires operator migration; deployment never deletes or moves data.

Use `WithRailwayRegistryCredentials(usernameParameter, passwordParameter)` for private pulls. Both are required; the password must be secret. Credentials go to the provider control plane, not workload variables.

Use `WithRailwayDeploymentDependency(prerequisite)` to require another Railway deployment's success. C# can also populate `DeploymentDependsOn`. Normal Aspire `WithEnvironment` / `WithReference` bindings resolve published endpoints to private Railway hostnames and container ports.

Pre-attached `WithImageSHA256` annotations are supported. The deployment pipeline removes build/push annotations even when `PublishAsDockerFile` follows `PublishToRailway`; local development retains its original resource configuration.
