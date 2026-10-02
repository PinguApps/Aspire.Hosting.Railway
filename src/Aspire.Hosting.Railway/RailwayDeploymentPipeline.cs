#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Railway.Deployment;
using Aspire.Hosting.Railway.Management;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Railway;

internal static class RailwayDeploymentPipeline
{
    private static readonly HttpClient _httpClient = new();
    private static readonly Action<ILogger, string, string, Exception?> _completed = LoggerMessage.Define<string, string>(
        LogLevel.Information, new EventId(1, "RailwayDeploymentCompleted"), "Railway resource '{Resource}' reconciled to service '{ServiceId}'.");

    internal static async Task PreflightAsync(RailwayTargetResource resource, PipelineStepContext context)
    {
        ValueProviderContext valueContext = new() { Caller = resource, ExecutionContext = context.ExecutionContext };
        string projectId = await ResolveRequiredAsync(resource.ProjectId, valueContext, context.CancellationToken).ConfigureAwait(false);
        string environmentId = await ResolveRequiredAsync(resource.EnvironmentId, valueContext, context.CancellationToken).ConfigureAwait(false);
        string siteKey = await ResolveRequiredAsync(resource.SiteKey, valueContext, context.CancellationToken).ConfigureAwait(false);
        string token = await ResolveRequiredAsync(resource.ApiToken, valueContext, context.CancellationToken).ConfigureAwait(false);
        RailwayResolvedTarget target = new(projectId, environmentId, siteKey, resource.Options);
        RailwayServiceReconciler reconciler = new(new RailwayManagementClient(_httpClient, token, resource.Options.AuthenticationMode));
        IDeploymentStateManager manager = context.Services.GetRequiredService<IDeploymentStateManager>();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (IResource candidate in context.Model.Resources)
        {
            RailwayServiceAnnotation? annotation = candidate.Annotations.OfType<RailwayServiceAnnotation>().SingleOrDefault();
            if (annotation is null || !ReferenceEquals(annotation.Target, resource))
            {
                continue;
            }

            string serviceName = annotation.Options.ServiceName ?? candidate.Name;
            if (!names.Add(serviceName))
            {
                throw new InvalidOperationException("Railway remote service names must be unique within a target.");
            }

            RailwayServiceValidation.Validate(annotation.Options);
            string image = annotation.Options.Image ?? GetRetainedImage(candidate);
            RailwayServiceValidation.ValidateImage(image);
            DeploymentStateSection section = await manager.AcquireSectionAsync($"PinguApps.Railway.{candidate.Name}", context.CancellationToken).ConfigureAwait(false);
            string plan = await reconciler.PreflightAsync(target, candidate.Name, serviceName, image, annotation.Options, section.Data, context.CancellationToken).ConfigureAwait(false);
            context.Summary.Add($"Railway plan: {candidate.Name}", plan);
        }
    }

    internal static async Task ExecuteAsync(IResourceWithEnvironment resource, RailwayServiceAnnotation annotation, PipelineStepContext context)
    {
        ValueProviderContext valueContext = new() { Caller = resource, ExecutionContext = context.ExecutionContext };
        string projectId = await ResolveRequiredAsync(annotation.Target.ProjectId, valueContext, context.CancellationToken).ConfigureAwait(false);
        string environmentId = await ResolveRequiredAsync(annotation.Target.EnvironmentId, valueContext, context.CancellationToken).ConfigureAwait(false);
        string siteKey = await ResolveRequiredAsync(annotation.Target.SiteKey, valueContext, context.CancellationToken).ConfigureAwait(false);
        string token = await ResolveRequiredAsync(annotation.Target.ApiToken, valueContext, context.CancellationToken).ConfigureAwait(false);
        RailwayResolvedTarget target = new(projectId, environmentId, siteKey, annotation.Target.Options);
        string image = annotation.Options.Image ?? GetRetainedImage(resource);
        RailwayServiceValidation.ValidateImage(image);
        Dictionary<string, object> rawEnvironment = new(StringComparer.Ordinal);
        EnvironmentCallbackContext environmentContext = new(context.ExecutionContext, resource, rawEnvironment, context.CancellationToken)
        {
            Logger = context.Logger,
        };
        foreach (EnvironmentCallbackAnnotation callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(environmentContext).ConfigureAwait(false);
        }

        Dictionary<string, string> environment = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object> variable in rawEnvironment)
        {
            string? value = await ResolveRuntimeValueAsync(variable.Value, valueContext, context.CancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                continue;
            }

            if (value.Contains(token, StringComparison.Ordinal) || variable.Key is "RAILWAY_TOKEN" or "RAILWAY_API_TOKEN" or "RAILWAY_API_KEY")
            {
                throw new InvalidOperationException("A Railway control-plane credential was included in workload configuration. Deployment refused.");
            }

            environment[variable.Key] = value;
        }

        if (resource is ProjectResource && annotation.Options.Port is int port)
        {
            environment["ASPNETCORE_HTTP_PORTS"] = port.ToString(CultureInfo.InvariantCulture);
        }

        IDeploymentStateManager manager = context.Services.GetRequiredService<IDeploymentStateManager>();
        DeploymentStateSection section = await manager.AcquireSectionAsync($"PinguApps.Railway.{resource.Name}", context.CancellationToken).ConfigureAwait(false);
        RailwayManagementClient client = new(_httpClient, token, annotation.Target.Options.AuthenticationMode);
        JsonObject? registryCredentials = null;
        string? registryFingerprint = null;
        if (annotation.Options.RegistryUsername is not null && annotation.Options.RegistryPassword is not null)
        {
            string username = await ResolveRequiredAsync(annotation.Options.RegistryUsername, valueContext, context.CancellationToken).ConfigureAwait(false);
            string password = await ResolveRequiredAsync(annotation.Options.RegistryPassword, valueContext, context.CancellationToken).ConfigureAwait(false);
            ValidateRegistryCredentials(token, username, password);

            registryCredentials = new JsonObject { ["username"] = username, ["password"] = password };
            registryFingerprint = Fingerprint(token, $"registry:{username}:{password}");
        }

        Dictionary<string, string> sealedFingerprints = GetSealedFingerprints(resource.Name, token, annotation.Options.SealedVariables, environment);
        RailwayServiceResult result = await new RailwayServiceReconciler(client).ApplyAsync(
            target, resource.Name, annotation.Options.ServiceName ?? resource.Name, image, annotation.Options, environment, section.Data,
            () => manager.SaveSectionAsync(section, context.CancellationToken), context.CancellationToken,
            registryCredentials, registryFingerprint, sealedFingerprints).ConfigureAwait(false);
        annotation.Outputs.Populate(result.ServiceId, result.PrivateHostname, result.PublicUrl, result.DeploymentId);
        context.Summary.Add($"Railway service: {resource.Name}", result.ServiceId);
        if (result.PublicUrl is not null)
        {
            context.Summary.Add($"Railway URL: {resource.Name}", result.PublicUrl);
        }
        _completed(context.Logger, resource.Name, result.ServiceId, null);
    }

    private static string Fingerprint(string key, string value) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value)));

    internal static void ValidateRegistryCredentials(string token, string username, string password)
    {
        if (username.Contains(token, StringComparison.Ordinal) || password.Contains(token, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Railway control-plane credentials cannot be used as registry credentials.");
        }
    }

    internal static Dictionary<string, string> GetSealedFingerprints(string resourceName, string token, IEnumerable<string> names, IReadOnlyDictionary<string, string> environment)
    {
        Dictionary<string, string> fingerprints = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (!environment.TryGetValue(name, out string? value))
            {
                throw new InvalidOperationException($"Sealed Railway variable '{name}' is not supplied by resource '{resourceName}'.");
            }

            fingerprints[name] = Fingerprint(token, $"variable:{name}:{value}");
        }

        return fingerprints;
    }

    private static string GetRetainedImage(IResource resource)
    {
        ContainerImageAnnotation image = resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault()
            ?? throw new InvalidOperationException("Railway requires an explicit retained image digest on this resource.");
        if (string.IsNullOrWhiteSpace(image.SHA256))
        {
            throw new InvalidOperationException("Railway requires an immutable image digest; image tags cannot select a retained release.");
        }

        string registry = string.IsNullOrWhiteSpace(image.Registry) ? string.Empty : $"{image.Registry}/";
        string digest = image.SHA256.StartsWith("sha256:", StringComparison.Ordinal) ? image.SHA256 : $"sha256:{image.SHA256}";
        return $"{registry}{image.Image}@{digest}";
    }

    private static async Task<string> ResolveRequiredAsync(ParameterResource parameter, ValueProviderContext context, CancellationToken cancellationToken) =>
        await parameter.GetValueAsync(context, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Railway parameter '{parameter.Name}' is missing.");

    internal static async Task<string?> ResolveRuntimeValueAsync(object value, ValueProviderContext context, CancellationToken cancellationToken)
    {
        if (value is ConnectionStringReference connection)
        {
            IResourceWithConnectionString source = connection.Resource;
            HashSet<IResourceWithConnectionString> seen = [];
            while (source.Annotations.OfType<ConnectionStringRedirectAnnotation>().LastOrDefault() is ConnectionStringRedirectAnnotation redirect)
            {
                if (!seen.Add(source))
                {
                    throw new InvalidOperationException("Connection-string redirection contains a cycle.");
                }

                source = redirect.Resource;
            }

            return await ResolveRuntimeValueAsync(source.ConnectionStringExpression, context, cancellationToken).ConfigureAwait(false);
        }

        if (value is ReferenceExpression expression)
        {
            if (expression.IsConditional)
            {
                string? condition = await ResolveRuntimeValueAsync(expression.Condition!, context, cancellationToken).ConfigureAwait(false);
                ReferenceExpression selected = condition == expression.MatchValue ? expression.WhenTrue! : expression.WhenFalse!;
                return await ResolveRuntimeValueAsync(selected, context, cancellationToken).ConfigureAwait(false);
            }

            object?[] values = new object?[expression.ValueProviders.Count];
            for (int index = 0; index < values.Length; index++)
            {
                string? resolved = await ResolveRuntimeValueAsync(expression.ValueProviders[index], context, cancellationToken).ConfigureAwait(false);
                values[index] = expression.StringFormats[index] == "uri" && resolved is not null ? Uri.EscapeDataString(resolved) : resolved;
            }

            return string.Format(CultureInfo.InvariantCulture, expression.Format, values);
        }

        if (value is EndpointReferenceExpression endpoint)
        {
            RailwayServiceAnnotation? published = endpoint.Endpoint.Resource.Annotations.OfType<RailwayServiceAnnotation>().SingleOrDefault();
            if (published is not null)
            {
                string host = await published.Outputs.PrivateHostname.GetValueAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Referenced Railway endpoint has no private hostname.");
                int port = endpoint.Endpoint.EndpointAnnotation.TargetPort ?? published.Options.Port
                    ?? throw new InvalidOperationException("Referenced Railway endpoint has no container port.");
                if (endpoint.Property == EndpointProperty.Host)
                {
                    return host;
                }

                if (endpoint.Property is EndpointProperty.Port or EndpointProperty.TargetPort)
                {
                    return port.ToString(CultureInfo.InvariantCulture);
                }

                if (endpoint.Property == EndpointProperty.Scheme)
                {
                    return endpoint.Endpoint.Scheme;
                }

                if (endpoint.Property == EndpointProperty.Url)
                {
                    return $"{endpoint.Endpoint.Scheme}://{host}:{port}";
                }

                if (endpoint.Property == EndpointProperty.HostAndPort)
                {
                    return $"{host}:{port}";
                }

                throw new InvalidOperationException("Published Railway endpoint property is unsupported; refusing local endpoint resolution.");
            }
        }

        if (value is EndpointReference reference)
        {
            return await ResolveRuntimeValueAsync(reference.Property(EndpointProperty.Url), context, cancellationToken).ConfigureAwait(false);
        }

        if (value is IValueProvider provider)
        {
            return await provider.GetValueAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
