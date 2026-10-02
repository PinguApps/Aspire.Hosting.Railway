namespace Aspire.Hosting.Railway;

internal sealed class RailwayResolvedTarget
{
    internal RailwayResolvedTarget(string projectId, string environmentId, string siteKey, RailwayTargetOptionsDto options)
    {
        ProjectId = projectId;
        EnvironmentId = environmentId;
        SiteKey = siteKey;
        Options = options;
    }

    internal string ProjectId { get; }
    internal string EnvironmentId { get; }
    internal string SiteKey { get; }
    internal RailwayTargetOptionsDto Options { get; }
}
