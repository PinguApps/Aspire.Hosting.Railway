#pragma warning disable CA1819 // Aspire guest-language DTO transport requires arrays.
namespace Aspire.Hosting.Railway;

/// <summary>Uploads an explicit source context for a Dockerfile build owned and retained by Railway.</summary>
[AspireDto]
public sealed class RailwayBuildOptions
{
    /// <summary>Gets or sets the source directory. Relative paths resolve against the AppHost directory.</summary>
    public string ContextPath { get; set; } = string.Empty;
    /// <summary>Gets or sets the Dockerfile path relative to the uploaded context.</summary>
    public string DockerfilePath { get; set; } = "Dockerfile";
    /// <summary>Gets or sets non-secret Dockerfile ARG values. Railway exposes these as build variables.</summary>
    public RailwayBuildArgument[]? BuildArguments { get; set; }
}

/// <summary>Declares a non-secret build argument.</summary>
[AspireDto]
public sealed class RailwayBuildArgument
{
    /// <summary>Gets or sets the Dockerfile ARG name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the Dockerfile ARG value.</summary>
    public string Value { get; set; } = string.Empty;
}
