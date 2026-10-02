using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Railway;

namespace RailwaySamples;

internal static class RailwayAppHostSnippets
{
    internal static void Configure(IDistributedApplicationBuilder builder)
    {
        IResourceBuilder<ParameterResource> project = builder.AddParameter("railway-project-id");
        IResourceBuilder<ParameterResource> environment = builder.AddParameter("railway-environment-id");
        IResourceBuilder<ParameterResource> token = builder.AddParameter("railway-api-token", secret: true);
        IResourceBuilder<ParameterResource> site = builder.AddParameter("site-key");
        IResourceBuilder<RailwayTargetResource> target = builder.AddRailwayTarget("railway", project, environment, token, site);
        builder.AddContainer("web", "traefik/whoami")
            .PublishToRailway(target, options =>
            {
                options.Image = "traefik/whoami@sha256:c4717a8d1f0134a7444e24f881160e033991f23027c6c5a9a3f8fd22e70d1d44";
                options.Port = 80;
                options.PublicDomain = true;
                options.HealthCheckPath = "/";
            });
        builder.AddContainer("private-worker", "traefik/whoami")
            .PublishToRailway(target, options => options.Image = "traefik/whoami@sha256:c4717a8d1f0134a7444e24f881160e033991f23027c6c5a9a3f8fd22e70d1d44");
    }
}
