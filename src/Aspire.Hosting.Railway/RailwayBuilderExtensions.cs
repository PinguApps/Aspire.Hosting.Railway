#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Railway;

/// <summary>Publishes ordinary Aspire projects and containers into a site-owned Railway target.</summary>
public static class RailwayBuilderExtensions
{
    /// <summary>Adds a target for a pre-created and recorded site environment.</summary>
    [AspireExport("pinguapps.railway.addTarget", MethodName = "addRailwayTarget")]
    public static IResourceBuilder<RailwayTargetResource> AddRailwayTarget(
        this IDistributedApplicationBuilder builder,
        string name,
        IResourceBuilder<ParameterResource> projectId,
        IResourceBuilder<ParameterResource> environmentId,
        IResourceBuilder<ParameterResource> apiToken,
        IResourceBuilder<ParameterResource> siteKey,
        RailwayTargetOptionsDto? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(environmentId);
        ArgumentNullException.ThrowIfNull(apiToken);
        ArgumentNullException.ThrowIfNull(siteKey);
        if (!apiToken.Resource.Secret)
        {
            throw new ArgumentException("The infrastructure token must be a secret Aspire parameter.", nameof(apiToken));
        }

        RailwayTargetResource target = new(name, projectId.Resource, environmentId.Resource, apiToken.Resource, siteKey.Resource, options ?? new());
        return builder.AddResource(target).ExcludeFromManifest().WithPipelineStepFactory(factory => CreateTargetSteps(target, factory));
    }

    /// <summary>Publishes a project or container using a retained immutable image, or an explicitly selected Railway-owned build.</summary>
    [AspireExportIgnore(Reason = "TypeScript uses resource-specific callback-free DTO exports.")]
    public static IResourceBuilder<T> PublishToRailway<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<RailwayTargetResource> target,
        Action<RailwayServiceOptions>? configure = null)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(target);
        if (builder.Resource is not (ProjectResource or ContainerResource))
        {
            throw new ArgumentException("Railway publishes project and container resources.", nameof(builder));
        }

        if (builder.Resource.Annotations.OfType<RailwayServiceAnnotation>().Any())
        {
            throw new InvalidOperationException("The resource already has a Railway publisher.");
        }

        RailwayServiceOptions options = new();
        configure?.Invoke(options);
        RailwayServiceValidation.Validate(options);
        if (options.Build is RailwayBuildOptions build)
        {
            build.ContextPath = Path.GetFullPath(build.ContextPath, builder.ApplicationBuilder.AppHostDirectory);
        }
        RailwayServiceAnnotation annotation = new(target.Resource, options, new RailwayServiceOutputs(builder.Resource));
        builder.WithAnnotation(annotation);
        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.ExcludeFromManifest();
            foreach (IResourceAnnotation imageBuild in builder.Resource.Annotations
                .Where(item => item is DockerfileBuildAnnotation or ContainerImagePushOptionsCallbackAnnotation)
                .ToArray())
            {
                builder.Resource.Annotations.Remove(imageBuild);
            }
        }

        return builder;
    }

    private static IEnumerable<PipelineStep> CreateTargetSteps(RailwayTargetResource target, PipelineStepFactoryContext factory)
    {
        yield return new PipelineStep
        {
            Name = $"railway-plan-{target.Name}",
            Action = context => RailwayDeploymentPipeline.PreflightAsync(target, context),
            DependsOnSteps = [WellKnownPipelineSteps.ProcessParameters],
            RequiredBySteps = [WellKnownPipelineSteps.DeployPrereq],
            Tags = [WellKnownPipelineTags.ProvisionInfrastructure],
            Description = "Validate the complete Railway target and plan before changing infrastructure.",
        };
        foreach (IResourceWithEnvironment resource in factory.PipelineContext.Model.Resources.OfType<IResourceWithEnvironment>())
        {
            RailwayServiceAnnotation? annotation = resource.Annotations.OfType<RailwayServiceAnnotation>().SingleOrDefault();
            if (annotation is null || !ReferenceEquals(annotation.Target, target))
            {
                continue;
            }

            foreach (IResourceAnnotation imageBuild in resource.Annotations.Where(item => item is DockerfileBuildAnnotation or ContainerImagePushOptionsCallbackAnnotation).ToArray())
            {
                resource.Annotations.Remove(imageBuild);
            }

            List<string> dependencies = [WellKnownPipelineSteps.DeployPrereq, WellKnownPipelineSteps.PushPrereq];
            dependencies.AddRange(annotation.Options.DeploymentDependsOn.Select(GetDeploymentStepName));
            dependencies.AddRange(resource.Annotations.OfType<ResourceRelationshipAnnotation>()
                .Where(relationship => relationship.Type == "Reference" && relationship.Resource.Annotations.OfType<RailwayServiceAnnotation>().Any())
                .Select(relationship => GetDeploymentStepName(relationship.Resource)));
            yield return new PipelineStep
            {
                Name = GetDeploymentStepName(resource),
                Action = context => RailwayDeploymentPipeline.ExecuteAsync(resource, annotation, context),
                DependsOnSteps = [.. dependencies.Distinct(StringComparer.Ordinal)],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                Description = "Plan and apply the site-owned Railway container service.",
                Tags = [WellKnownPipelineTags.DeployCompute],
            };
        }
    }

    /// <summary>Publishes a container from a TypeScript AppHost.</summary>
    [AspireExport("pinguapps.railway.container.publish", MethodName = "publishToRailway")]
    public static IResourceBuilder<ContainerResource> PublishContainerToRailway(
        this IResourceBuilder<ContainerResource> builder,
        IResourceBuilder<RailwayTargetResource> target,
        RailwayServiceOptionsDto? options = null) => builder.PublishToRailway(target, options is null ? null : options.ApplyTo);

    /// <summary>Publishes a .NET project from a TypeScript AppHost.</summary>
    [AspireExport("pinguapps.railway.project.publish", MethodName = "publishToRailway")]
    public static IResourceBuilder<ProjectResource> PublishProjectToRailway(
        this IResourceBuilder<ProjectResource> builder,
        IResourceBuilder<RailwayTargetResource> target,
        RailwayServiceOptionsDto? options = null) => builder.PublishToRailway(target, options is null ? null : options.ApplyTo);

    /// <summary>Gets the typed deployment outputs for a Railway service.</summary>
    [AspireExportIgnore(Reason = "TypeScript uses individual output exports.")]
    public static RailwayServiceOutputs GetRailwayOutputs<T>(this IResourceBuilder<T> builder) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Resource.Annotations.OfType<RailwayServiceAnnotation>().Single().Outputs;
    }

    /// <summary>Gets the stable pipeline step name for dependencies, including finite-job completion.</summary>
    [AspireExportIgnore(Reason = "Pipeline names are a C# integration seam.")]
    public static string GetDeploymentStepName(IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return $"railway-deploy-{resource.Name}";
    }

    /// <summary>Requires a Railway-backed resource to deploy successfully before this container.</summary>
    [AspireExport("pinguapps.railway.container.dependsOn", MethodName = "withRailwayDeploymentDependency")]
    public static IResourceBuilder<ContainerResource> WithRailwayDeploymentDependency(this IResourceBuilder<ContainerResource> builder, IResourceBuilder<IResource> prerequisite)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(prerequisite);
        builder.Resource.Annotations.OfType<RailwayServiceAnnotation>().Single().Options.DeploymentDependsOn.Add(prerequisite.Resource);
        return builder;
    }

    /// <summary>Requires a Railway-backed resource to deploy successfully before this project.</summary>
    [AspireExport("pinguapps.railway.project.dependsOn", MethodName = "withRailwayDeploymentDependency")]
    public static IResourceBuilder<ProjectResource> WithRailwayDeploymentDependency(this IResourceBuilder<ProjectResource> builder, IResourceBuilder<IResource> prerequisite)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(prerequisite);
        builder.Resource.Annotations.OfType<RailwayServiceAnnotation>().Single().Options.DeploymentDependsOn.Add(prerequisite.Resource);
        return builder;
    }

    /// <summary>Gets a container's Railway private hostname as a guest-language expression.</summary>
    [AspireExport("pinguapps.railway.container.privateHost", MethodName = "getRailwayPrivateHostname")]
    public static ReferenceExpression GetRailwayPrivateHostname(this IResourceBuilder<ContainerResource> builder) => ReferenceExpression.Create($"{builder.GetRailwayOutputs().PrivateHostname}");

    /// <summary>Gets a project's Railway private hostname as a guest-language expression.</summary>
    [AspireExport("pinguapps.railway.project.privateHost", MethodName = "getRailwayPrivateHostname")]
    public static ReferenceExpression GetRailwayPrivateHostname(this IResourceBuilder<ProjectResource> builder) => ReferenceExpression.Create($"{builder.GetRailwayOutputs().PrivateHostname}");

    /// <summary>Gets the exact container deployment identity as a guest-language expression.</summary>
    [AspireExport("pinguapps.railway.container.deploymentId", MethodName = "getRailwayDeploymentId")]
    public static ReferenceExpression GetRailwayDeploymentId(this IResourceBuilder<ContainerResource> builder) => ReferenceExpression.Create($"{builder.GetRailwayOutputs().DeploymentId}");

    /// <summary>Gets the exact project deployment identity as a guest-language expression.</summary>
    [AspireExport("pinguapps.railway.project.deploymentId", MethodName = "getRailwayDeploymentId")]
    public static ReferenceExpression GetRailwayDeploymentId(this IResourceBuilder<ProjectResource> builder) => ReferenceExpression.Create($"{builder.GetRailwayOutputs().DeploymentId}");

    /// <summary>Gets the image observed for the completed container deployment.</summary>
    [AspireExport("pinguapps.railway.container.image", MethodName = "getRailwayImage")]
    public static ReferenceExpression GetRailwayImage(this IResourceBuilder<ContainerResource> builder) => ReferenceExpression.Create($"{builder.GetRailwayOutputs().Image}");

    /// <summary>Gets the image observed for the completed project deployment.</summary>
    [AspireExport("pinguapps.railway.project.image", MethodName = "getRailwayImage")]
    public static ReferenceExpression GetRailwayImage(this IResourceBuilder<ProjectResource> builder) => ReferenceExpression.Create($"{builder.GetRailwayOutputs().Image}");

    /// <summary>Gets the source snapshot identity associated with a completed container build.</summary>
    [AspireExport("pinguapps.railway.container.buildFingerprint", MethodName = "getRailwayBuildFingerprint")]
    public static ReferenceExpression GetRailwayBuildFingerprint(this IResourceBuilder<ContainerResource> builder) => ReferenceExpression.Create($"{builder.GetRailwayOutputs().BuildFingerprint}");

    /// <summary>Gets the source snapshot identity associated with a completed project build.</summary>
    [AspireExport("pinguapps.railway.project.buildFingerprint", MethodName = "getRailwayBuildFingerprint")]
    public static ReferenceExpression GetRailwayBuildFingerprint(this IResourceBuilder<ProjectResource> builder) => ReferenceExpression.Create($"{builder.GetRailwayOutputs().BuildFingerprint}");

    /// <summary>Gets the SHA256 digest of the exact completed container image.</summary>
    [AspireExport("pinguapps.railway.container.imageDigest", MethodName = "getRailwayImageDigest")]
    public static ReferenceExpression GetRailwayImageDigest(this IResourceBuilder<ContainerResource> builder) => ReferenceExpression.Create($"{builder.GetRailwayOutputs().ImageDigest}");

    /// <summary>Gets the SHA256 digest of the exact completed project image.</summary>
    [AspireExport("pinguapps.railway.project.imageDigest", MethodName = "getRailwayImageDigest")]
    public static ReferenceExpression GetRailwayImageDigest(this IResourceBuilder<ProjectResource> builder) => ReferenceExpression.Create($"{builder.GetRailwayOutputs().ImageDigest}");

    /// <summary>Configures infrastructure-only pull credentials for a private container registry.</summary>
    [AspireExport("pinguapps.railway.container.registryCredentials", MethodName = "withRailwayRegistryCredentials")]
    public static IResourceBuilder<ContainerResource> WithRailwayRegistryCredentials(this IResourceBuilder<ContainerResource> builder, IResourceBuilder<ParameterResource> username, IResourceBuilder<ParameterResource> password)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ConfigureRegistry(builder.Resource, username, password);
        return builder;
    }

    /// <summary>Configures infrastructure-only pull credentials for a private project image.</summary>
    [AspireExport("pinguapps.railway.project.registryCredentials", MethodName = "withRailwayRegistryCredentials")]
    public static IResourceBuilder<ProjectResource> WithRailwayRegistryCredentials(this IResourceBuilder<ProjectResource> builder, IResourceBuilder<ParameterResource> username, IResourceBuilder<ParameterResource> password)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ConfigureRegistry(builder.Resource, username, password);
        return builder;
    }

    private static void ConfigureRegistry(IResource resource, IResourceBuilder<ParameterResource> username, IResourceBuilder<ParameterResource> password)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);
        if (!password.Resource.Secret)
        {
            throw new ArgumentException("The registry password must be a secret parameter.", nameof(password));
        }

        RailwayServiceOptions options = resource.Annotations.OfType<RailwayServiceAnnotation>().Single().Options;
        options.RegistryUsername = username.Resource;
        options.RegistryPassword = password.Resource;
    }
}

internal sealed class RailwayServiceAnnotation : IResourceAnnotation
{
    internal RailwayServiceAnnotation(RailwayTargetResource target, RailwayServiceOptions options, RailwayServiceOutputs outputs)
    {
        Target = target;
        Options = options;
        Outputs = outputs;
    }
    internal RailwayTargetResource Target { get; }
    internal RailwayServiceOptions Options { get; }
    internal RailwayServiceOutputs Outputs { get; }
}
