#pragma warning disable CA1819 // Aspire guest-language DTO transport requires arrays.
namespace Aspire.Hosting.Railway;

/// <summary>Provides the callback-free service publishing contract for TypeScript AppHosts.</summary>
[AspireDto]
public sealed class RailwayServiceOptionsDto
{
    /// <summary>Gets or sets the remote service name.</summary>
    public string? ServiceName { get; set; }
    /// <summary>Gets or sets the immutable retained container image.</summary>
    public string? Image { get; set; }
    /// <summary>Gets or sets the process start command.</summary>
    public string? StartCommand { get; set; }
    /// <summary>Gets or sets the ownership policy.</summary>
    public RailwayOwnershipMode? OwnershipMode { get; set; }
    /// <summary>Gets or sets an explicitly adopted service ID.</summary>
    public string? ExistingServiceId { get; set; }
    /// <summary>Gets or sets the restart policy.</summary>
    public RailwayRestartPolicy? RestartPolicy { get; set; }
    /// <summary>Gets or sets the restart limit.</summary>
    public int? RestartPolicyMaxRetries { get; set; }
    /// <summary>Gets or sets the cron schedule.</summary>
    public string? CronSchedule { get; set; }
    /// <summary>Gets or sets whether process completion is required.</summary>
    public bool? WaitForCompletion { get; set; }
    /// <summary>Gets or sets the deploy timeout in seconds.</summary>
    public int? DeploymentTimeoutSeconds { get; set; }
    /// <summary>Gets or sets the container HTTP port.</summary>
    public int? Port { get; set; }
    /// <summary>Gets or sets whether to provision a public domain.</summary>
    public bool? PublicDomain { get; set; }
    /// <summary>Gets or sets the HTTP readiness path.</summary>
    public string? HealthCheckPath { get; set; }
    /// <summary>Gets or sets the region identifier.</summary>
    public string? Region { get; set; }
    /// <summary>Gets or sets memory in GB.</summary>
    public double? MemoryGB { get; set; }
    /// <summary>Gets or sets vCPU count.</summary>
    public double? VCpus { get; set; }
    /// <summary>Gets or sets serverless sleeping.</summary>
    public bool? SleepApplication { get; set; }
    /// <summary>Gets or sets persistent mounts.</summary>
    public RailwayVolumeOptions[]? Volumes { get; set; }
    /// <summary>Gets or sets custom domains.</summary>
    public string[]? CustomDomains { get; set; }
    /// <summary>Gets or sets names of write-only runtime variables sealed by Railway.</summary>
    public string[]? SealedVariables { get; set; }

    internal void ApplyTo(RailwayServiceOptions target)
    {
        target.ServiceName = ServiceName;
        target.Image = Image;
        target.StartCommand = StartCommand;
        target.OwnershipMode = OwnershipMode ?? RailwayOwnershipMode.CreateOrAdopt;
        target.ExistingServiceId = ExistingServiceId;
        target.RestartPolicy = RestartPolicy ?? RailwayRestartPolicy.OnFailure;
        target.RestartPolicyMaxRetries = RestartPolicyMaxRetries ?? 3;
        target.CronSchedule = CronSchedule;
        target.WaitForCompletion = WaitForCompletion ?? false;
        target.DeploymentTimeout = TimeSpan.FromSeconds(DeploymentTimeoutSeconds ?? 600);
        target.Port = Port;
        target.PublicDomain = PublicDomain ?? false;
        target.HealthCheckPath = HealthCheckPath;
        target.Region = Region;
        target.MemoryGB = MemoryGB;
        target.VCpus = VCpus;
        target.SleepApplication = SleepApplication ?? false;
        foreach (RailwayVolumeOptions volume in Volumes ?? [])
        {
            target.Volumes.Add(volume);
        }

        foreach (string domain in CustomDomains ?? [])
        {
            target.CustomDomains.Add(domain);
        }

        foreach (string variable in SealedVariables ?? [])
        {
            target.SealedVariables.Add(variable);
        }
    }
}
