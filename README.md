# PinguApps.Aspire.Hosting.Railway

[![PinguApps.Aspire.Hosting.Railway version](https://img.shields.io/nuget/v/PinguApps.Aspire.Hosting.Railway?style=for-the-badge&label=PinguApps.Aspire.Hosting.Railway)](https://www.nuget.org/packages/PinguApps.Aspire.Hosting.Railway/) [![PinguApps.Aspire.Hosting.Railway downloads](https://img.shields.io/nuget/dt/PinguApps.Aspire.Hosting.Railway?style=for-the-badge&label=downloads)](https://www.nuget.org/packages/PinguApps.Aspire.Hosting.Railway/)

Deploy normal Aspire projects and containers into a pre-created, site-owned Railway environment. Local development stays unchanged. Retained images deploy without building or pushing containers.

## Install

Requires .NET 10 and Aspire 13.6.0.

```powershell
dotnet add package PinguApps.Aspire.Hosting.Railway --version 1.0.0
```

## C# AppHost

```csharp
using Aspire.Hosting.Railway;

var web = builder.AddProject<Projects.Web>("web");
if (builder.ExecutionContext.IsPublishMode)
{
    var target = builder.AddRailwayTarget("railway",
        builder.AddParameter("railway-project-id", Current("Parameters__railway_project_id")),
        builder.AddParameter("railway-environment-id", Current("Parameters__railway_environment_id")),
        builder.AddParameter("railway-api-token", Current("Parameters__railway_api_token"), secret: true),
        builder.AddParameter("site-key", Current("Parameters__site_key")));

    web.PublishToRailway(target, options =>
    {
        options.Image = release.WebImage; // ghcr.io/...@sha256:...
        options.Port = 8080;
        options.PublicDomain = true;
        options.HealthCheckPath = "/health";
        options.Region = "europe-west4-drams3a";
        options.MemoryGB = 1;
        options.VCpus = 1;
        options.SealedVariables.Add("ConnectionStrings__database");
    });
}

string Current(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Missing deployment input: {name}");
```

Before deployment, set the environment's shared `PINGUAPPS_SITE_KEY` to its allocation record's site key. The package never creates projects, environments, or allocation records. Default authentication uses an environment-scoped project token and validates its exact scope and the live site marker before writes.

Supply **current** destination and credential parameters on every deployment. Aspire itself can retain parameter values in deployment state; do not rely on cached credentials after rotation. The [C# guide](docs/getting-started-dotnet.md) shows explicit current inputs. Account/workspace Bearer mode additionally requires the recorded project name and workspace ID.

```powershell
aspire deploy --non-interactive
aspire deploy --non-interactive --list-steps
```

The read-only target plan validates all declared services before provider mutations. Then each service waits for provider prerequisites and explicit release dependencies, resolves runtime bindings, applies changes, and verifies the exact deployment ID.

## TypeScript AppHost

```json
{
  "appHost": { "path": "apphost.mts", "language": "typescript/nodejs" },
  "sdk": { "version": "13.6.0" },
  "packages": { "PinguApps.Aspire.Hosting.Railway": "1.0.0" }
}
```

```typescript
import { createBuilder, railwayOwnershipMode } from "./.aspire/modules/aspire.mjs";
const builder = await createBuilder();
let web = await builder.addContainer("web", "example/web");
if (await builder.executionContext().isPublishMode()) {
  const project = await builder.addParameter("railway-project-id", { value: current("Parameters__railway_project_id") });
  const environment = await builder.addParameter("railway-environment-id", { value: current("Parameters__railway_environment_id") });
  const token = await builder.addParameter("railway-api-token", { value: current("Parameters__railway_api_token"), secret: true });
  const site = await builder.addParameter("site-key", { value: current("Parameters__site_key") });
  const target = await builder.addRailwayTarget("railway", project, environment, token, site);
  web = await web.publishToRailway(target, {
    image: "ghcr.io/example/web@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    ownershipMode: railwayOwnershipMode.createOrAdopt,
    port: 8080, publicDomain: true, healthCheckPath: "/health"
  });
}
const app = await builder.build();
await app.run();

function current(name: string): string {
  const value = process.env[name];
  if (!value) throw new Error(`Missing deployment input: ${name}`);
  return value;
}
```

The [packed fixture](tests/Aspire.Hosting.Railway/Fixtures/TypeScriptAppHost) validates actual NuGet-generated exports. Explicitly declare this package alongside companion packages so the shared target API is generated.

## Ownership and repeatability

- `CreateOnly`: create a missing service; later deployments reuse its recorded identity. Do not adopt unrelated pre-existing infrastructure.
- `ExistingOnly`: require a proven existing service; never create one.
- `CreateOrAdopt`: create a missing service or adopt a service marked for the same site/resource. Unmarked adoption additionally requires an explicit `ExistingServiceId`.
- Cached IDs remain bound to site, project, environment, and name. Missing or replaced identities fail safely.
- Volume identity, mount, region, and unexpected public-domain drift require operator reconciliation. Infrastructure is never deleted automatically.
- Pending application state allows retries without duplicate resources or skipping an unfinished deployment.

## Runtime and release configuration

Use normal `WithEnvironment`, `WithReference`, and parameter expressions. Published endpoints resolve to Railway private hostnames and container ports. `DeploymentDependsOn` and `WithRailwayDeploymentDependency` gate dependent deployments, including finite jobs. Workers get no public domain unless requested.

Ordinary services must reach Railway `SUCCESS`. Finite execution additionally must stop with every instance `EXITED`; a running deployment is insufficient. Failure, removal, or a timeout prevents downstream deployment. Cron workloads use `Never` restart and must exit. The Jobs companion package supplies workload-specific APIs and cron validation.

Private GHCR images need Railway private-registry support (currently a Pro feature) and a narrowly scoped pull credential:

```csharp
web.WithRailwayRegistryCredentials(
    builder.AddParameter("ghcr-user"),
    builder.AddParameter("ghcr-pull-token", secret: true));
```

Pull credentials go only to Railway's control plane. Variables listed in `SealedVariables` become write-only runtime secrets. State stores keyed desired-value fingerprints, never their values or registry passwords. Infrastructure credentials in workload configuration cause refusal.

## Development and release

```powershell
dotnet test Aspire.Hosting.Railway.slnx -c Release
pwsh ./eng/Test-AspireVersionPins.ps1
pwsh ./eng/Validate-TypeScriptAppHostPackage.ps1
```

Workflows follow the existing integration repositories: pinned actions, release drafting, NuGet OIDC publishing, symbols, and the packed TypeScript gate. Configure Blacksmith access and `NUGET_USER`; register the GitHub repository/workflow as a NuGet trusted publisher. No persistent NuGet API key is required.

See [configuration](docs/configuration.md), [deployment behavior](docs/deployment-behaviour.md), [security](docs/outputs-and-security.md), and [live validation](docs/live-validation.md).
