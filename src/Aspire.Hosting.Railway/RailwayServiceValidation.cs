using System.Text.RegularExpressions;

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
