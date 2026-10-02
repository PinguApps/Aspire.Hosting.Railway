using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Railway;

/// <summary>Declares an immutable container service in a Railway environment.</summary>
public sealed class RailwayServiceOptions
{
    /// <summary>Gets or sets the explicit remote service name. Defaults to the Aspire resource name.</summary>
    public string? ServiceName { get; set; }
    /// <summary>Gets or sets the retained image reference, including its sha256 digest.</summary>
    public string? Image { get; set; }
    /// <summary>Gets or sets the start command override.</summary>
    public string? StartCommand { get; set; }
    /// <summary>Gets or sets the ownership policy.</summary>
    public RailwayOwnershipMode OwnershipMode { get; set; } = RailwayOwnershipMode.CreateOrAdopt;
    /// <summary>Gets or sets an explicit service identity required when adopting unmarked infrastructure.</summary>
    public string? ExistingServiceId { get; set; }
    /// <summary>Gets or sets the restart policy.</summary>
    public RailwayRestartPolicy RestartPolicy { get; set; } = RailwayRestartPolicy.OnFailure;
    /// <summary>Gets or sets the restart retry limit.</summary>
    public int RestartPolicyMaxRetries { get; set; } = 3;
    /// <summary>Gets or sets a UTC cron schedule for a finite executable.</summary>
    public string? CronSchedule { get; set; }
    /// <summary>Gets or sets whether this deployment waits for confirmed successful process termination.</summary>
    public bool WaitForCompletion { get; set; }
    /// <summary>Gets or sets the bound on deployment and optional process completion.</summary>
    public TimeSpan DeploymentTimeout { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Gets or sets the container HTTP port.</summary>
    public int? Port { get; set; }
    /// <summary>Gets or sets whether Railway should provision a public HTTPS domain.</summary>
    public bool PublicDomain { get; set; }
    /// <summary>Gets or sets the HTTP readiness path.</summary>
    public string? HealthCheckPath { get; set; }
    /// <summary>Gets or sets the deployment region identifier.</summary>
    public string? Region { get; set; }
    /// <summary>Gets or sets the memory limit in GB.</summary>
    public double? MemoryGB { get; set; }
    /// <summary>Gets or sets the vCPU limit.</summary>
    public double? VCpus { get; set; }
    /// <summary>Gets or sets whether Railway serverless sleeping is enabled.</summary>
    public bool SleepApplication { get; set; }
    /// <summary>Gets names of write-only runtime secrets sealed by Railway. Their values are never cached.</summary>
    public IList<string> SealedVariables { get; } = [];
    /// <summary>Gets or sets the private image registry username parameter.</summary>
    public ParameterResource? RegistryUsername { get; set; }
    /// <summary>Gets or sets the private image registry password parameter. Must be secret.</summary>
    public ParameterResource? RegistryPassword { get; set; }
    /// <summary>Gets the persistent mounts. Existing mount drift fails without deleting data.</summary>
    public IList<RailwayVolumeOptions> Volumes { get; } = [];
    /// <summary>Gets public custom domains. Their DNS remains an operator responsibility.</summary>
    public IList<string> CustomDomains { get; } = [];
    /// <summary>Gets dependencies which must complete their Railway deployment first.</summary>
    public IList<IResource> DeploymentDependsOn { get; } = [];
}

/// <summary>Declares a persistent container mount.</summary>
[AspireDto]
public sealed class RailwayVolumeOptions
{
    /// <summary>Gets or sets the absolute persistent mount path.</summary>
    public string MountPath { get; set; } = string.Empty;
}

/// <summary>Controls explicit creation and adoption.</summary>
public enum RailwayOwnershipMode
{
    /// <summary>Create a missing service or reconcile its already recorded identity.</summary>
    [AspireValue("railwayOwnershipMode", Name = "createOnly")]
    CreateOnly,
    /// <summary>Use a proven existing service; never create one.</summary>
    [AspireValue("railwayOwnershipMode", Name = "existingOnly")]
    ExistingOnly,
    /// <summary>Create a missing service or adopt one after ownership validation.</summary>
    [AspireValue("railwayOwnershipMode", Name = "createOrAdopt")]
    CreateOrAdopt,
}

/// <summary>Controls container restart behavior.</summary>
public enum RailwayRestartPolicy
{
    /// <summary>Never restart a completed process.</summary>
    [AspireValue("railwayRestartPolicy", Name = "never")]
    Never,
    /// <summary>Restart after a failure up to the configured retry limit.</summary>
    [AspireValue("railwayRestartPolicy", Name = "onFailure")]
    OnFailure,
    /// <summary>Always restart.</summary>
    [AspireValue("railwayRestartPolicy", Name = "always")]
    Always,
}
