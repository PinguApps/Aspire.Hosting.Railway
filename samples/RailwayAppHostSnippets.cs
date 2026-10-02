using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Railway;

namespace RailwaySamples;

internal static class RailwayAppHostSnippets
{
    internal static void Configure(IDistributedApplicationBuilder builder)
    {
        IResourceBuilder<ContainerResource> web = builder.AddContainer("web", "traefik/whoami");
        IResourceBuilder<ContainerResource> worker = builder.AddContainer("private-worker", "traefik/whoami");
        if (builder.ExecutionContext.IsRunMode)
        {
            return;
        }

        IResourceBuilder<ParameterResource> project = builder.AddParameter("railway-project-id", Current("Parameters__railway_project_id"));
        IResourceBuilder<ParameterResource> environment = builder.AddParameter("railway-environment-id", Current("Parameters__railway_environment_id"));
        IResourceBuilder<ParameterResource> token = builder.AddParameter("railway-api-token", Current("Parameters__railway_api_token"), secret: true);
        IResourceBuilder<ParameterResource> site = builder.AddParameter("site-key", Current("Parameters__site_key"));
        IResourceBuilder<RailwayTargetResource> target = builder.AddRailwayTarget("railway", project, environment, token, site);
        web.PublishToRailway(target, options =>
            {
                options.Image = "traefik/whoami@sha256:c4717a8d1f0134a7444e24f881160e033991f23027c6c5a9a3f8fd22e70d1d44";
                options.Port = 80;
                options.PublicDomain = true;
                options.HealthCheckPath = "/";
            });
        worker.PublishToRailway(target, options => options.Image = "traefik/whoami@sha256:c4717a8d1f0134a7444e24f881160e033991f23027c6c5a9a3f8fd22e70d1d44");
    }

    private static string Current(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing deployment input: {name}");
}
