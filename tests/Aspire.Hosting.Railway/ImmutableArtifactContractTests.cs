#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.Railway.Tests;

public sealed class ImmutableArtifactContractTests
{
    private static readonly string _digest = new('a', 64);
    private static readonly string _image = $"ghcr.io/pinguapps/example/web@sha256:{_digest}";

    [Fact]
    public void Container_ExplicitRetainedImageRemovesBuildAndPushCallbacks()
    {
        IDistributedApplicationBuilder app = CreateBuilder(publish: true);
        IResourceBuilder<ContainerResource> container = app.AddContainer("web", "original", "latest");
        AddBuildAnnotations(container.Resource);

        IResourceBuilder<ContainerResource> result = container.PublishToRailway(CreateTarget(app), options => options.Image = _image);

        Assert.Same(container, result);
        AssertNoImageBuild(result.Resource);
    }

    [Fact]
    public void Container_PreAttachedDigestPreservesTheRetainedImage()
    {
        IDistributedApplicationBuilder app = CreateBuilder(publish: true);
        IResourceBuilder<ContainerResource> container = app.AddContainer("worker", "example/worker")
            .WithImage("example/worker", tag: null)
            .WithImageRegistry("ghcr.io")
            .WithImageSHA256($"sha256:{_digest}");
        AddBuildAnnotations(container.Resource);

        container.PublishToRailway(CreateTarget(app));

        AssertNoImageBuild(container.Resource);
        ContainerImageAnnotation image = Assert.Single(container.Resource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("ghcr.io", image.Registry);
        Assert.Equal("example/worker", image.Image);
        Assert.Equal($"sha256:{_digest}", image.SHA256);
        Assert.Null(image.Tag);
    }

    [Fact]
    public async Task Project_ExplicitImagePreservesRuntimeConfigurationWithoutBuilding()
    {
        IDistributedApplicationBuilder app = CreateBuilder(publish: true);
        IResourceBuilder<ProjectResource> project = app.AddResource(new ProjectResource("web"))
            .WithEnvironment("Existing__Setting", "preserved")
            .WithHttpEndpoint(targetPort: 8080);
        AddBuildAnnotations(project.Resource);

        IResourceBuilder<ProjectResource> result = project.PublishToRailway(CreateTarget(app), options => options.Image = _image);

        Assert.Same(project, result);
        AssertNoImageBuild(result.Resource);
        Dictionary<string, object> environment = [];
        EnvironmentCallbackContext context = new(app.ExecutionContext, result.Resource, environment, CancellationToken.None);
        foreach (EnvironmentCallbackAnnotation callback in result.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        Assert.Equal("preserved", environment["Existing__Setting"]);
        Assert.Equal(8080, Assert.Single(result.Resource.Annotations.OfType<EndpointAnnotation>()).TargetPort);
    }

    [Fact]
    public void Project_PreAttachedImagePreservesTheDigest()
    {
        IDistributedApplicationBuilder app = CreateBuilder(publish: true);
        IResourceBuilder<ProjectResource> project = app.AddResource(new ProjectResource("migration"));
        ContainerImageAnnotation image = new() { Registry = "ghcr.io", Image = "example/migration", SHA256 = $"sha256:{_digest}" };
        project.Resource.Annotations.Add(image);
        AddBuildAnnotations(project.Resource);

        project.PublishToRailway(CreateTarget(app));

        AssertNoImageBuild(project.Resource);
        Assert.Same(image, Assert.Single(project.Resource.Annotations.OfType<ContainerImageAnnotation>()));
        Assert.Equal($"sha256:{_digest}", image.SHA256);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Project_DockerPublicationPreservesTheRailwayPublisher(bool railwayFirst)
    {
        IDistributedApplicationBuilder app = CreateBuilder(publish: true);
        string projectPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Aspire.Hosting.Railway.Tests.csproj"));
        IResourceBuilder<ProjectResource> project = app.AddProject("web", projectPath);
        IResourceBuilder<RailwayTargetResource> target = CreateTarget(app);
        if (railwayFirst)
        {
            project.PublishToRailway(target);
        }

        project.PublishAsDockerFile(container => container.WithImage("ghcr.io/pinguapps/example/web", tag: null).WithImageSHA256($"sha256:{_digest}"));
        if (!railwayFirst)
        {
            project.PublishToRailway(target);
        }

        ContainerResource container = Assert.Single(app.Resources.OfType<ContainerResource>());
        Assert.Single(container.Annotations.OfType<RailwayServiceAnnotation>());
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        PipelineContext pipeline = new(new DistributedApplicationModel(app.Resources), app.ExecutionContext, services, NullLogger.Instance, TestContext.Current.CancellationToken);
        PipelineStepFactoryContext context = new() { PipelineContext = pipeline, Resource = target.Resource };
        IEnumerable<PipelineStep> steps = await Assert.Single(target.Resource.Annotations.OfType<PipelineStepAnnotation>()).CreateStepsAsync(context);
        PipelineStep[] generated = [.. steps];
        PipelineStep plan = Assert.Single(generated, step => step.Name == "railway-plan-railway");
        Assert.Contains(WellKnownPipelineSteps.DeployPrereq, plan.RequiredBySteps);
        Assert.Contains(WellKnownPipelineSteps.ProcessParameters, plan.DependsOnSteps);
        Assert.Single(generated, step => step.Name == "railway-deploy-web");
        AssertNoImageBuild(container);
        Assert.Equal($"sha256:{_digest}", Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>()).SHA256);
    }

    [Fact]
    public void LocalContainer_PreservesBuildAndPushAnnotations()
    {
        IDistributedApplicationBuilder app = CreateBuilder(publish: false);
        IResourceBuilder<ContainerResource> container = app.AddContainer("local", "example/local", "development");
        AddBuildAnnotations(container.Resource);
        IResourceAnnotation[] annotations = [.. container.Resource.Annotations];
        bool requiredBuild = container.Resource.RequiresImageBuild();
        bool excludedFromPublish = container.Resource.IsExcludedFromPublish();

        IResourceBuilder<ContainerResource> result = container.PublishToRailway(CreateTarget(app), options => options.Image = _image);

        Assert.Same(container, result);
        foreach (IResourceAnnotation annotation in annotations)
        {
            Assert.Contains(annotation, result.Resource.Annotations);
        }

        Assert.Equal(requiredBuild, container.Resource.RequiresImageBuild());
        Assert.Equal(excludedFromPublish, container.Resource.IsExcludedFromPublish());
        Assert.Equal("development", Assert.Single(container.Resource.Annotations.OfType<ContainerImageAnnotation>()).Tag);
    }

    [Fact]
    public void LocalProject_RemainsTheResourceOfRecord()
    {
        IDistributedApplicationBuilder app = CreateBuilder(publish: false);
        IResourceBuilder<ProjectResource> project = app.AddResource(new ProjectResource("web"));
        bool excludedFromPublish = project.Resource.IsExcludedFromPublish();

        IResourceBuilder<ProjectResource> result = project.PublishToRailway(CreateTarget(app), options => options.Image = _image);

        Assert.Same(project, result);
        Assert.Same(project.Resource, Assert.Single(app.Resources.OfType<ProjectResource>()));
        Assert.Equal(excludedFromPublish, project.Resource.IsExcludedFromPublish());
        Assert.DoesNotContain(result.Resource.Annotations, annotation => annotation is ContainerImageAnnotation or DockerfileBuildAnnotation);
    }

    [Fact]
    public void Target_RejectsAnUnmarkedInfrastructureSecret()
    {
        IDistributedApplicationBuilder app = CreateBuilder(publish: true);

        Assert.Throws<ArgumentException>(() => app.AddRailwayTarget("railway",
            app.AddParameter("project-id"), app.AddParameter("environment-id"),
            app.AddParameter("api-token"), app.AddParameter("site-key")));
    }

    [Fact]
    public void Resource_RejectsDuplicateRailwayPublishers()
    {
        IDistributedApplicationBuilder app = CreateBuilder(publish: true);
        IResourceBuilder<RailwayTargetResource> target = CreateTarget(app);
        IResourceBuilder<ContainerResource> resource = app.AddContainer("web", "example/web")
            .PublishToRailway(target, options => options.Image = _image);

        Assert.Throws<InvalidOperationException>(() => resource.PublishToRailway(target, options => options.Image = _image));
    }

    [Theory]
    [InlineData("ghcr.io/pinguapps/example/web:1.0.0")]
    [InlineData("ghcr.io/pinguapps/example/web@sha256:short")]
    [InlineData("ghcr.io/pinguapps/example/web@sha256:sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Resource_RejectsMutableOrMalformedRetainedImages(string image)
    {
        IDistributedApplicationBuilder app = CreateBuilder(publish: true);
        IResourceBuilder<RailwayTargetResource> target = CreateTarget(app);
        IResourceBuilder<ContainerResource> container = app.AddContainer("web", "example/web");

        Assert.Throws<ArgumentException>(() => container.PublishToRailway(target, options => options.Image = image));
    }

    private static IDistributedApplicationBuilder CreateBuilder(bool publish) => DistributedApplication.CreateBuilder(
        new DistributedApplicationOptions { Args = publish ? ["--publisher", "manifest"] : [], DisableDashboard = true });

    private static IResourceBuilder<RailwayTargetResource> CreateTarget(IDistributedApplicationBuilder app) => app.AddRailwayTarget("railway",
        app.AddParameter("project-id"), app.AddParameter("environment-id"),
        app.AddParameter("api-token", secret: true), app.AddParameter("site-key"));

    private static void AddBuildAnnotations(IResource resource)
    {
        resource.Annotations.Add(new DockerfileBuildAnnotation(AppContext.BaseDirectory, "Dockerfile", null));
        resource.Annotations.Add(new ContainerImagePushOptionsCallbackAnnotation(_ => { }));
    }

    private static void AssertNoImageBuild(IResource resource)
    {
        Assert.DoesNotContain(resource.Annotations, annotation => annotation is DockerfileBuildAnnotation or ContainerImagePushOptionsCallbackAnnotation);
        Assert.False(resource.RequiresImageBuild());
        Assert.False(resource.RequiresImageBuildAndPush());
    }
}
