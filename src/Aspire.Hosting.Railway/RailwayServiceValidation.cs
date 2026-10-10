using System.Text.RegularExpressions;
using Aspire.Hosting.Railway.Deployment;

namespace Aspire.Hosting.Railway;

internal static partial class RailwayServiceValidation
{
    [GeneratedRegex(@"\A[^\s@]+@sha256:[a-f0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();

    internal static void ValidateImage(string image)
    {
        if (!DigestPattern().IsMatch(image))
        {
            throw new ArgumentException("Railway requires an immutable image@sha256 digest reference.", nameof(image));
        }
    }

    internal static void Validate(RailwayServiceOptions options)
    {
        if (options.Build is RailwayBuildOptions build)
        {
            if (options.Image is not null || options.RegistryUsername is not null || options.RegistryPassword is not null)
            {
                throw new ArgumentException("Railway source builds cannot also select a retained image or registry credentials.", nameof(options));
            }

            if (string.IsNullOrWhiteSpace(build.ContextPath) || string.IsNullOrWhiteSpace(build.DockerfilePath)
                || Path.IsPathRooted(build.DockerfilePath) || build.DockerfilePath.Replace('\\', '/').Split('/').Contains("..", StringComparer.Ordinal))
            {
                throw new ArgumentException("Source builds require an explicit context and a Dockerfile below that context.", nameof(options));
            }

            RailwayBuildArgument[] arguments = build.BuildArguments ?? [];
            if (arguments.Any(argument => string.IsNullOrWhiteSpace(argument.Name) || argument.Name.StartsWith("PINGUAPPS_", StringComparison.Ordinal)
                    || argument.Name.StartsWith("RAILWAY_", StringComparison.Ordinal) || argument.Name.Contains('=', StringComparison.Ordinal) || argument.Value is null)
                || arguments.Select(argument => argument.Name).Distinct(StringComparer.Ordinal).Count() != arguments.Length)
            {
                throw new ArgumentException("Build arguments must have unique non-reserved names and non-secret values.", nameof(options));
            }
        }

        if ((options.RegistryUsername is null) != (options.RegistryPassword is null) || options.RegistryPassword is { Secret: false })
        {
            throw new ArgumentException("Registry credentials require both parameters and a secret password.", nameof(options));
        }

        if (!Enum.IsDefined(options.OwnershipMode) || !Enum.IsDefined(options.RestartPolicy))
        {
            throw new ArgumentException("Unsupported Railway ownership or restart policy.", nameof(options));
        }

        if (options.SealedVariables.Any(name => string.IsNullOrWhiteSpace(name) || name.StartsWith("PINGUAPPS_", StringComparison.Ordinal)
                || name is "RAILWAY_TOKEN" or "RAILWAY_API_TOKEN" or "RAILWAY_API_KEY")
            || options.SealedVariables.Distinct(StringComparer.Ordinal).Count() != options.SealedVariables.Count)
        {
            throw new ArgumentException("Sealed variables must use unique runtime names; ownership markers and control-plane credentials cannot be sealed workload variables.", nameof(options));
        }

        if (options.Image is not null)
        {
            ValidateImage(options.Image);
        }

        if (options.Region is string region)
        {
            RailwayServiceReconciler.ResolveRegion(region);
        }

        if (options.CustomDomains.Any(domain => string.IsNullOrWhiteSpace(domain) || domain.EndsWith('.')
                || !domain.Contains('.', StringComparison.Ordinal) || Uri.CheckHostName(domain) != UriHostNameType.Dns)
            || options.CustomDomains.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.CustomDomains.Count)
        {
            throw new ArgumentException("Custom domains must be unique DNS host names without a scheme, path or trailing dot.", nameof(options));
        }

        if (options.Port is < 1 or > 65535 || options.RestartPolicyMaxRetries < 0
            || (options.RestartPolicy != RailwayRestartPolicy.Never && options.RestartPolicyMaxRetries == 0) || options.DeploymentTimeout <= TimeSpan.Zero
            || options.MemoryGB is <= 0 || options.VCpus is <= 0
            || (options.MemoryGB is double memory && !double.IsFinite(memory))
            || (options.VCpus is double cpu && !double.IsFinite(cpu)))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Ports, resource limits, retries and timeouts must be valid.");
        }

        if ((options.PublicDomain || options.CustomDomains.Count != 0 || options.HealthCheckPath is not null) && options.Port is null)
        {
            throw new ArgumentException("HTTP exposure or readiness requires an explicit container port.", nameof(options));
        }

        if ((options.WaitForCompletion && (options.RestartPolicy != RailwayRestartPolicy.Never || options.CronSchedule is not null))
            || (options.CronSchedule is not null && options.RestartPolicy != RailwayRestartPolicy.Never))
        {
            throw new ArgumentException("Finite and scheduled jobs require Never restart; a scheduled job cannot be a release completion gate.", nameof(options));
        }

        if (options.HealthCheckPath is not null && !options.HealthCheckPath.StartsWith('/'))
        {
            throw new ArgumentException("Readiness paths must be absolute HTTP paths.", nameof(options));
        }

        if (options.Volumes.Any(volume => !volume.MountPath.StartsWith('/') || volume.MountPath == "/" || volume.MountPath.Contains("..", StringComparison.Ordinal))
            || options.Volumes.Select(volume => volume.MountPath).Distinct(StringComparer.Ordinal).Count() != options.Volumes.Count)
        {
            throw new ArgumentException("Persistent mounts must use unique absolute paths below the container root.", nameof(options));
        }
    }
}
