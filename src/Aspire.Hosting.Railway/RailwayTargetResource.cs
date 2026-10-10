using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Railway;

/// <summary>Represents a pre-created, site-owned Railway environment.</summary>
public sealed class RailwayTargetResource : Resource
{
    /// <summary>Initializes a target from infrastructure-only parameter resources.</summary>
    public RailwayTargetResource(string name, ParameterResource projectId, ParameterResource environmentId, ParameterResource apiToken, ParameterResource siteKey, RailwayTargetOptionsDto options)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(environmentId);
        ArgumentNullException.ThrowIfNull(apiToken);
        ArgumentNullException.ThrowIfNull(siteKey);
        ArgumentNullException.ThrowIfNull(options);
        if (!apiToken.Secret)
        {
            throw new ArgumentException("Railway control-plane token parameters must be secret.", nameof(apiToken));
        }
        ProjectId = projectId;
        EnvironmentId = environmentId;
        ApiToken = apiToken;
        SiteKey = siteKey;
        Options = options;
    }

    /// <summary>Gets the expected project identity.</summary>
    public ParameterResource ProjectId { get; }
    /// <summary>Gets the expected environment identity.</summary>
    public ParameterResource EnvironmentId { get; }
    /// <summary>Gets the infrastructure token. This must never be supplied to a workload.</summary>
    [AspireExportIgnore(Reason = "Control-plane credentials are infrastructure-only.")]
    public ParameterResource ApiToken { get; }
    /// <summary>Gets the site allocation identity.</summary>
    public ParameterResource SiteKey { get; }
    /// <summary>Gets target authentication and allocation constraints.</summary>
    public RailwayTargetOptionsDto Options { get; }
}

/// <summary>Controls target authentication and allocation validation.</summary>
[AspireDto]
public sealed class RailwayTargetOptionsDto
{
    /// <summary>Gets or sets the Railway CLI executable used for source uploads. Requires version 5.63.1 or later.</summary>
    public string CliPath { get; set; } = "railway";
    /// <summary>Gets or sets the token mode. Environment-scoped project tokens are the default.</summary>
    public RailwayAuthenticationMode AuthenticationMode { get; set; } = RailwayAuthenticationMode.ProjectToken;
    /// <summary>Gets or sets the expected project name from the site allocation record. Required for account mode.</summary>
    public string? ExpectedProjectName { get; set; }
    /// <summary>Gets or sets the expected workspace identity. Required for account mode.</summary>
    public string? ExpectedWorkspaceId { get; set; }
}

/// <summary>Specifies the Railway management credential format.</summary>
public enum RailwayAuthenticationMode
{
    /// <summary>Use a scoped project token, validating its project and environment before any writes.</summary>
    [AspireValue("railwayAuthenticationMode", Name = "projectToken")]
    ProjectToken,
    /// <summary>Use an explicitly selected account/workspace Bearer credential and allocation constraints.</summary>
    [AspireValue("railwayAuthenticationMode", Name = "bearer")]
    Bearer,
}
