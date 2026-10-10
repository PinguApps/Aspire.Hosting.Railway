using Aspire.Hosting.Dashboard.Railway;
using Aspire.Hosting.Railway;

var builder = DistributedApplication.CreateBuilder(args);
if (builder.ExecutionContext.IsPublishMode)
{
    var target = builder.AddRailwayTarget("railway",
        builder.AddParameter("railway-project-id", Current("LIVE_RAILWAY_PROJECT_ID")),
        builder.AddParameter("railway-environment-id", Current("LIVE_RAILWAY_ENVIRONMENT_ID")),
        builder.AddParameter("railway-api-token", Current("LIVE_RAILWAY_TOKEN"), secret: true),
        builder.AddParameter("site-key", Current("LIVE_SITE_KEY")),
        new RailwayTargetOptionsDto { CliPath = Environment.GetEnvironmentVariable("LIVE_RAILWAY_CLI") ?? "railway" });
    RailwayBuildOptions Build(string name) => new()
    {
        ContextPath = Path.Combine(builder.AppHostDirectory, "Contexts", name),
        BuildArguments = [new() { Name = "PROOF_VERSION", Value = Current("LIVE_PROOF_VERSION") }],
    };
    void Configure(RailwayServiceOptions options)
    {
        options.Region = "ams";
        options.MemoryGB = 0.25;
        options.VCpus = 0.5;
        options.DeploymentTimeout = TimeSpan.FromMinutes(10);
        options.Port = 8080;
        options.PublicDomain = true;
    }
    builder.AddProject("live-core-project", Path.Combine(builder.AppHostDirectory, "Contexts", "Web", "Web.csproj"))
        .WithEnvironment("RUNTIME_PROOF", "runtime-binding-passed")
        .PublishToRailway(target, options =>
        {
            Configure(options);
            options.Build = Build("Web");
            options.HealthCheckPath = "/health";
        });
    builder.AddContainer("live-core-container", "busybox").PublishToRailway(target, options =>
    {
        Configure(options);
        options.Build = Build("Container");
        options.HealthCheckPath = "/";
    });
    builder.AddContainer("live-core-image", "busybox").PublishToRailway(target, options =>
    {
        Configure(options);
        options.Image = "docker.io/library/busybox@sha256:66a6306db78bf2dbf3487f293aa8d6990d8e506fdffab9cc43fe422becf886e4";
        options.StartCommand = "sh -c 'mkdir -p /www; echo LEGACY_IMAGE_READY > /www/index.html; httpd -f -p 8080 -h /www'";
        options.HealthCheckPath = "/";
    });
    builder.AddRailwayDashboard("live-core-dashboard",
        builder.AddParameter("dashboard-browser", Current("LIVE_DASHBOARD_BROWSER_TOKEN"), secret: true),
        builder.AddParameter("dashboard-otlp", Current("LIVE_DASHBOARD_OTLP_TOKEN"), secret: true))
        .PublishToRailway(target, (RailwayDashboardOptions options) =>
        {
            options.Region = "ams";
            options.MemoryGB = 0.5;
            options.VCpus = 0.5;
        });
}
builder.Build().Run();

string Current(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Missing live fixture input: {name}");
