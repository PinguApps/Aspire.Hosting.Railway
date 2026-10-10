using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Railway.Deployment;

internal sealed class RailwaySourceUpload : IDisposable
{
    private RailwaySourceUpload(string path, string fingerprint)
    {
        ContextPath = path;
        Fingerprint = fingerprint;
    }

    internal string Fingerprint { get; }
    internal string ContextPath { get; }

    internal static void ValidatePaths(RailwayBuildOptions build)
    {
        string context = Path.GetFullPath(build.ContextPath);
        string dockerfile = Path.GetFullPath(Path.Combine(context, build.DockerfilePath));
        string contextPrefix = Path.EndsInDirectorySeparator(context) ? context : context + Path.DirectorySeparatorChar;
        if (!Directory.Exists(context) || !File.Exists(dockerfile) || !dockerfile.StartsWith(contextPrefix, StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(context) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The Railway build context and its Dockerfile must exist below the explicit context directory.");
        }
    }

    internal static async Task ValidateCliAsync(string cliPath, CancellationToken cancellationToken)
    {
        string result = await RunCliAsync(cliPath, ["--version"], null, null, cancellationToken).ConfigureAwait(false);
        string[] parts = result.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !Version.TryParse(parts[1], out Version? version) || version < new Version(5, 63, 1))
        {
            throw new InvalidOperationException("Railway source uploads require Railway CLI version 5.63.1 or later.");
        }
    }

    internal static async Task<RailwaySourceUpload> CreateAsync(RailwayBuildOptions build, string token, CancellationToken cancellationToken)
    {
        ValidatePaths(build);
        string path = Path.Combine(Path.GetTempPath(), "pinguapps-railway-upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] credential = Encoding.UTF8.GetBytes(token);
            foreach (string file in EnumerateFiles(build.ContextPath).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(build.ContextPath, file);
                byte[] contents = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
                if (contents.AsSpan().IndexOf(credential) >= 0)
                {
                    throw new InvalidOperationException("The Railway source context contains its control-plane credential. Upload refused.");
                }

                string destination = Path.Combine(path, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllBytesAsync(destination, contents, cancellationToken).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows())
                {
                    UnixFileMode mode = File.GetUnixFileMode(file);
                    File.SetUnixFileMode(destination, mode);
                    hash.AppendData(BitConverter.GetBytes((int)mode));
                }
                hash.AppendData(Encoding.UTF8.GetBytes(relative.Replace('\\', '/') + "\0"));
                hash.AppendData(SHA256.HashData(contents));
            }

            string ignore = string.Empty;
            foreach (string name in new[] { ".dockerignore", ".railwayignore" })
            {
                string ignorePath = Path.Combine(path, name);
                if (File.Exists(ignorePath))
                {
                    ignore += await File.ReadAllTextAsync(ignorePath, cancellationToken).ConfigureAwait(false) + "\n";
                }
            }
            ignore += "!/.dockerignore\n";
            string dockerfilePath = Path.GetRelativePath(build.ContextPath,
                Path.GetFullPath(Path.Combine(build.ContextPath, build.DockerfilePath))).Replace('\\', '/');
            string[] segments = dockerfilePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int index = 1; index < segments.Length; index++)
            {
                ignore += "!/" + string.Join('/', segments.Take(index)) + "/\n";
            }
            ignore += "!/" + dockerfilePath + "\n";
            await File.WriteAllTextAsync(Path.Combine(path, ".railwayignore"), ignore, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(Path.Combine(path, build.DockerfilePath)))
            {
                throw new InvalidOperationException("The Dockerfile was excluded by source-upload security rules.");
            }
            return new RailwaySourceUpload(path, "source:sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch
        {
            Directory.Delete(path, recursive: true);
            throw;
        }
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        foreach (string item in Directory.EnumerateFileSystemEntries(directory))
        {
            string name = Path.GetFileName(item);
            if (new[] { ".git", ".aspire", ".railway", "node_modules", "bin", "obj", "secrets.json" }.Contains(name, StringComparer.OrdinalIgnoreCase)
                || name.Equals(".env", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            FileAttributes attributes = File.GetAttributes(item);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Railway source uploads reject symbolic links; use a self-contained build context.");
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (string file in EnumerateFiles(item))
                {
                    yield return file;
                }
            }
            else
            {
                yield return item;
            }
        }
    }

    internal async Task<string> UploadAsync(RailwayResolvedTarget target, string serviceId, string requestId, string token, CancellationToken cancellationToken)
    {
        string output = await RunCliAsync(target.Options.CliPath,
            ["up", ContextPath, "--path-as-root", "--project", target.ProjectId, "--environment", target.EnvironmentId,
                "--service", serviceId, "--detach", "--json", "--message", requestId],
            target.Options.AuthenticationMode, token, cancellationToken).ConfigureAwait(false);
        try
        {
            JsonObject result = JsonNode.Parse(output)!.AsObject();
            string deploymentId = (string?)result["deploymentId"] ?? string.Empty;
            if (!Guid.TryParse(deploymentId, out _))
            {
                throw new InvalidOperationException("Railway upload did not return an exact deployment identity.");
            }
            return deploymentId;
        }
        catch (System.Text.Json.JsonException)
        {
            throw new InvalidOperationException("Railway upload returned invalid deployment JSON. Reconcile the recorded request before retrying.");
        }
    }

    private static async Task<string> RunCliAsync(string cliPath, string[] arguments, RailwayAuthenticationMode? mode, string? token, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new(cliPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment.Remove("RAILWAY_TOKEN");
        start.Environment.Remove("RAILWAY_API_TOKEN");
        start.Environment.Remove("RAILWAY_API_KEY");
        start.Environment["CI"] = "true";
        if (token is not null)
        {
            start.Environment[mode == RailwayAuthenticationMode.ProjectToken ? "RAILWAY_TOKEN" : "RAILWAY_API_TOKEN"] = token;
        }
        using Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Railway CLI could not be started.");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("Railway CLI is unavailable. Install Railway CLI 5.63.1 or later, or set the target CliPath.");
        }
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            string result = await output.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Railway CLI failed. Its output is withheld because upload diagnostics may contain credentials; inspect the scoped deployment in Railway.");
            }
            return result;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    public void Dispose() => Directory.Delete(ContextPath, recursive: true);
}
