# C# AppHost

First allocate the project/environment and record their IDs and site key. Set the environment's shared `PINGUAPPS_SITE_KEY` through Railway's variables UI. Create an environment-scoped deployment token and store it outside source control. This package validates the allocation; it never creates projects/environments or invents ownership.

Declare normal local resources first. Add the target and deployment-only parameters inside a publish-mode guard. Supply current parameter values explicitly; merely updating appsettings can retain old cached Aspire values.

```csharp
using Aspire.Hosting.Railway;

var builder = DistributedApplication.CreateBuilder(args);
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
        options.Image = release.WebImage; // retained registry/image@sha256:...
        options.Port = 8080;
        options.PublicDomain = true;
        options.HealthCheckPath = "/health";
    });
}

builder.Build().Run();

string Current(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Missing deployment input: {name}");
```

`Projects.Web` and `release.WebImage` belong to the application's generated project references and release descriptor. This package consumes retained digests; it does not create descriptors or build/push images. See the compile-validated [snippet](../samples/RailwayAppHostSnippets.cs).

Set the four environment variables through your secret store or private shell configuration, then run `aspire deploy --non-interactive`. Local `aspire run` does not create target parameter resources or require deployment credentials. Keep production/staging allocations and credentials separate; Aspire's deployment environment name alone does not select a Railway environment.

`aspire deploy --non-interactive --list-steps` lists the pipeline. The actual deployment's read-only `railway-plan` step validates live ownership and scope before apply steps run.
