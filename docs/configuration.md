# Configuration

`AddRailwayTarget` takes parameter resources for the recorded project ID, environment ID, infrastructure token, and site key. The token must be secret. The existing environment's shared `PINGUAPPS_SITE_KEY` must already match; deployment never establishes its own ownership marker.

`RailwayTargetOptionsDto.AuthenticationMode` defaults to `ProjectToken`. Explicit `Bearer` mode also requires `ExpectedProjectName` and `ExpectedWorkspaceId` from the allocation record. Project-token authentication validates the exact project/environment scope before writes.

| Target input / option | Type | Default / constraint |
| --- | --- | --- |
| `projectId` | Parameter resource | Required recorded project ID. |
| `environmentId` | Parameter resource | Required recorded environment ID. |
| `apiToken` | Secret parameter resource | Required current infrastructure credential; never a workload variable. |
| `siteKey` | Parameter resource | Required recorded key matching the existing shared `PINGUAPPS_SITE_KEY`. |
| `AuthenticationMode` | `RailwayAuthenticationMode` | `ProjectToken`; explicit `Bearer` is account/workspace authentication. |
| `ExpectedProjectName` | Nullable string | Required in `Bearer` mode. |
| `ExpectedWorkspaceId` | Nullable string | Required in `Bearer` mode. |
| `CliPath` | String | `railway`; source uploads require Railway CLI 5.63.1 or later. |

C# callbacks use `RailwayServiceOptions`; TypeScript uses callback-free `RailwayServiceOptionsDto`, with camel-case properties. Exactly one replica is published.

| Option | Default | Meaning / constraint |
| --- | --- | --- |
| `ServiceName` | Aspire resource name | Recorded remote identity; renaming an allocation requires operator reconciliation. |
| `Image` | Pre-attached image annotation | Required immutable `registry/image@sha256:<64 lowercase hex>` reference. Tags are rejected. |
| `Build` | Unset | Opt-in `RailwayBuildOptions` with explicit `ContextPath`, relative `DockerfilePath` (default `Dockerfile`), and optional non-secret `BuildArguments` (`Name` / `Value`). Mutually exclusive with explicit image and registry credentials. |
| `OwnershipMode` | `CreateOrAdopt` | See the [ownership rules](../README.md#ownership-and-repeatability). |
| `ExistingServiceId` | Unset | Required to explicitly adopt an unmarked existing service. |
| `StartCommand` | Image default | Optional container command override. |
| `RestartPolicy` | `OnFailure` | `Never`, `OnFailure`, or `Always`. |
| `RestartPolicyMaxRetries` | `3` | At least 1 for restarting policies. `Never` accepts 0 and omits this provider setting. |
| `DeploymentTimeout` | 10 minutes | Positive C# `TimeSpan`; TypeScript uses `deploymentTimeoutSeconds` (default 600). |
| `WaitForCompletion` | `false` | Finite execution; requires `Never` restart and no cron schedule. |
| `CronSchedule` | Unset | UTC schedule for an executable that exits; requires `Never`. Use Jobs for schedule validation. |
| `Port` | Unset | Container HTTP port, 1–65535. Required for HTTP domains or readiness checks. |
| `PublicDomain` | `false` | Request a Railway HTTPS domain. Workers remain private by default. |
| `CustomDomains` | Empty | Requested hostnames; DNS configuration remains external. |
| `HealthCheckPath` | Unset | Readiness path beginning with `/`. |
| `Region` | Provider default | Supported names listed below. |
| `MemoryGB` / `VCpus` | Provider default | Positive finite numeric resource limits. |
| `SleepApplication` | `false` | Explicit serverless sleeping; active brokers/workers may prevent sleep. |
| `Volumes` | Empty | Persistent mounts with unique absolute `MountPath` values below `/`, without `..` segments. |
| `SealedVariables` | Empty | Unique, non-empty runtime names to seal. Ownership names beginning `PINGUAPPS_` and control-plane credential names are reserved. |
| `RegistryUsername` / `RegistryPassword` | Unset | C# parameter resources, both required together; password must be secret. TypeScript uses `withRailwayRegistryCredentials`. |
| `DeploymentDependsOn` | Empty | C# resource collection of required Railway deployments. TypeScript uses `withRailwayDeploymentDependency`. |

Service identity, image, command, schedule, path and region fields are nullable strings; port/retries are integers, limits are doubles, flags are booleans, and domain/sealed-name collections contain strings. `Volumes` contains `RailwayVolumeOptions` with a string `MountPath`. The DTO exposes nullable scalar overrides and arrays, inheriting the same defaults when omitted.

Internal regions `ams`, `sfo`, `iad`, and `sin` also accept public aliases `europe-west4-drams3a`, `us-west2`, `us-east4-eqdc4a`, and `asia-southeast1-eqsg3a`, respectively. Existing volume region/mount drift requires operator migration; deployment never deletes or moves data.

Use `WithRailwayRegistryCredentials(usernameParameter, passwordParameter)` for private pulls. Both are required; the password must be secret. Credentials go to the provider control plane, not workload variables.

Use `WithRailwayDeploymentDependency(prerequisite)` to require another Railway deployment's success. C# can also populate `DeploymentDependsOn`. Normal Aspire `WithEnvironment` / `WithReference` bindings resolve published endpoints to private Railway hostnames and container ports.

Pre-attached `WithImageSHA256` annotations are supported. The deployment pipeline removes build/push annotations even when `PublishAsDockerFile` follows `PublishToRailway`; local development retains its original resource configuration.

See [Railway-owned source builds](source-builds.md) for upload boundaries, CLI prerequisites, release identity, and retention limits.
