using System.Text.Json.Nodes;
using Aspire.Hosting.Railway.Deployment;
using Aspire.Hosting.Railway.Management;
using Reqnroll;
using Xunit;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

namespace Aspire.Hosting.Railway.Tests;

[Binding]
public sealed class SourceBuildSteps : IDisposable
{
    private readonly ReconciliationContractTests.Provider _provider = new();
    private readonly JsonObject _identity = [];
    private readonly string _context = Path.Combine(Path.GetTempPath(), "railway-source-contract-" + Guid.NewGuid().ToString("N"));
    private readonly RailwayServiceOptions _options = new();
    private RailwayServiceResult? _result;
    private RailwaySourceUpload? _snapshot;
    private string? _fingerprint;
    private Exception? _error;
    private int _uploads;
    private string _sourceFingerprint = "source:sha256:explicit-snapshot";

    [Given("a Railway owned source service")]
    public void SourceService() => _options.Build = new RailwayBuildOptions { ContextPath = _context };

    [Given("a Railway owned finite source service")]
    public void FiniteService()
    {
        SourceService();
        _options.WaitForCompletion = true;
        _options.RestartPolicy = RailwayRestartPolicy.Never;
        _provider.InstanceStatus = "EXITED";
        _provider.Stopped = true;
    }

    [Given("the credential belongs to another environment")]
    public void ForeignCredential() => _provider.TokenEnvironment = "another-environment";

    [Given("an unowned Railway service already exists")]
    public void UnownedService() => _provider.CreateService(marked: false);

    [Given("the existing service has a custom config as code path")]
    public void ExistingConfigFile()
    {
        _provider.CreateService(marked: true);
        _provider.SetConfigFile("/custom-config.json");
    }

    [Then("the operator is told to clear the custom config file setting")]
    public void ClearConfigFile() => Assert.Contains("Clear its Railway Config File setting", _error!.Message, StringComparison.Ordinal);

    [Given("the upload metadata belongs to another request")]
    public void WrongUploadMarker() => _provider.WrongCliMessage = true;

    [Given("Railway omits the built image digest")]
    public void MissingBuiltDigest() => _provider.OmitBuiltImageDigest = true;

    [When("the finite source service completes")]
    public async Task CompleteFinite() => _result = await ApplyAsync();

    [Then("its exact deployment and source snapshot remain proven")]
    public void ProvenWithoutDigest()
    {
        Assert.Equal("deployment", _result!.DeploymentId);
        Assert.Equal(_sourceFingerprint, _result.BuildFingerprint);
        Assert.Null(_result.ImageDigest);
    }

    [When("runtime binding exceeds the publisher deadline")]
    public async Task PublisherDeadline() => _error = await RunDeadlineAsync(callerCancellation: false);

    [When("the caller cancels runtime binding")]
    public async Task CallerCancellation() => _error = await RunDeadlineAsync(callerCancellation: true);

    private static async Task<Exception?> RunDeadlineAsync(bool callerCancellation)
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        IDistributedApplicationBuilder app = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = ["--publisher", "manifest"], DisableDashboard = true });
        IResourceBuilder<RailwayTargetResource> target = app.AddRailwayTarget("railway",
            app.AddParameter("project-id", "project"), app.AddParameter("environment-id", "environment"),
            app.AddParameter("api-token", "secret-token", secret: true), app.AddParameter("site-key", "site"));
        IResourceBuilder<ProjectResource> project = app.AddResource(new ProjectResource("deadline"))
            .WithEnvironment(async context =>
            {
                if (callerCancellation)
                {
                    await cancellation.CancelAsync();
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            })
            .PublishToRailway(target, options =>
            {
                options.Image = "example/image@sha256:" + new string('a', 64);
                options.DeploymentTimeout = callerCancellation ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(100);
            });
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        PipelineContext pipeline = new(new DistributedApplicationModel(app.Resources), app.ExecutionContext, services, NullLogger.Instance, cancellation.Token);
        PipelineStepContext step = new() { PipelineContext = pipeline, ReportingStep = null! };
        return await Record.ExceptionAsync(() => RailwayDeploymentPipeline.ExecuteAsync(project.Resource,
            project.Resource.Annotations.OfType<RailwayServiceAnnotation>().Single(), step));
    }

    [Then("a sanitized completion deadline timeout is reported")]
    public void ExplicitDeadline()
    {
        TimeoutException error = Assert.IsType<TimeoutException>(_error);
        Assert.Contains("exceeded its completion deadline", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", error.ToString(), StringComparison.Ordinal);
    }

    [Then("caller cancellation is preserved")]
    public void PreserveCallerCancellation() => Assert.IsAssignableFrom<OperationCanceledException>(_error);

    [Given("source proof (.*) is temporarily absent")]
    public void DelayedProof(string field)
    {
        if (field == "defaultDockerfile")
        {
            _options.Build!.DockerfilePath = "jobs/Dockerfile";
        }
        _provider.MissingCorrelationField = field;
        _provider.MissingCorrelationResponses = 1;
    }

    [When("the source service is published twice")]
    public async Task PublishTwice()
    {
        _result = await ApplyAsync();
        _result = await ApplyAsync();
        Assert.False(_result.Deployed);
    }

    [When("an accepted upload response is lost and publication is resumed")]
    public async Task LoseAndResume()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => ApplyAsync(loseResponse: true));
        Assert.True((bool)_identity["deploymentAttempt"]!["sent"]!);
        _result = await ApplyAsync();
    }

    [When("the source content changes after successful publication")]
    public async Task ChangedSource()
    {
        await ApplyAsync();
        _sourceFingerprint = "source:sha256:changed-snapshot";
        _result = await ApplyAsync();
        Assert.True(_result.Deployed);
    }

    [When("the source service is rejected")]
    public async Task RejectService() => _error = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync());

    [When("upload correlation is rejected and publication is resumed")]
    public async Task RejectCorrelationAndResume()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync());
    }

    [Then("the sent upload remains recorded for recovery")]
    public void PreserveSentRequest() => Assert.True((bool)_identity["deploymentAttempt"]!["sent"]!);

    private async Task<RailwayServiceResult> ApplyAsync(bool loseResponse = false)
    {
        using HttpClient http = new(_provider, disposeHandler: false);
        RailwayServiceReconciler reconciler = new(new RailwayManagementClient(http, "secret-token", RailwayAuthenticationMode.ProjectToken));
        return await reconciler.ApplyAsync(new RailwayResolvedTarget("project", "environment", "site", new()),
            "web", "web", _sourceFingerprint, _options, [], _identity, () => Task.CompletedTask,
            TestContext.Current.CancellationToken, upload: (service, request, cancellation) =>
            {
                Assert.Equal("service", service);
                Assert.NotEmpty(request);
                cancellation.ThrowIfCancellationRequested();
                _uploads++;
                string id = _provider.UploadSource();
                if (loseResponse)
                {
                    throw new HttpRequestException("The upload response was lost.");
                }
                return Task.FromResult(id);
            });
    }

    [Then("only one source upload has run")]
    public void SingleUpload() => Assert.Equal(1, _uploads);

    [Then("two source uploads have run")]
    public void ChangedUpload() => Assert.Equal(2, _uploads);

    [Then("no source upload or provider mutation has run")]
    public void NoMutations()
    {
        Assert.Equal(0, _uploads);
        Assert.Equal(0, _provider.Mutations);
    }

    [Then("the configuration was committed without triggering a registry deployment")]
    public void SourceConfiguration()
    {
        Assert.Equal(1, _provider.DeployRequests);
        Assert.Null(_provider.LastPatch!["source"]!["image"]);
        Assert.Null(_provider.LastPatch["deploy"]!["registryCredentials"]);
        Assert.Equal("DOCKERFILE", (string?)_provider.LastPatch["build"]!["builder"]);
    }

    [Then("the completed deployment exposes its Railway image digest")]
    public void ExactImage()
    {
        Assert.Null(_result!.Image);
        Assert.Equal("sha256:" + new string('b', 64), _result.ImageDigest);
        Assert.Equal(_sourceFingerprint, _result.BuildFingerprint);
    }

    [Given("a source context containing local secrets")]
    public void ContextWithSecrets()
    {
        Directory.CreateDirectory(_context);
        File.WriteAllText(Path.Combine(_context, "Dockerfile"), "FROM scratch\nCOPY marker /marker\n");
        File.WriteAllText(Path.Combine(_context, "marker"), "release");
        File.WriteAllText(Path.Combine(_context, ".env"), "RAILWAY_TOKEN=secret-token");
        Directory.CreateDirectory(Path.Combine(_context, ".aspire"));
        File.WriteAllText(Path.Combine(_context, ".aspire", "secrets.json"), "secret-token");
    }

    [Given("a source context containing the control plane credential")]
    public void ContextWithEmbeddedCredential()
    {
        ContextWithSecrets();
        File.WriteAllText(Path.Combine(_context, "marker"), "prefix-secret-token-suffix");
    }

    [When("the source context is snapshotted twice")]
    public async Task SnapshotTwice()
    {
        RailwayBuildOptions build = new() { ContextPath = _context };
        using RailwaySourceUpload first = await RailwaySourceUpload.CreateAsync(build, "secret-token", TestContext.Current.CancellationToken);
        _fingerprint = first.Fingerprint;
        _snapshot = await RailwaySourceUpload.CreateAsync(build, "secret-token", TestContext.Current.CancellationToken);
    }

    [Then("the snapshot content identities match")]
    public void StableIdentity() => Assert.Equal(_fingerprint, _snapshot!.Fingerprint);

    [Then("local secrets are absent from the upload directory")]
    public void SecretsAbsent()
    {
        Assert.False(File.Exists(Path.Combine(_snapshot!.ContextPath, ".env")));
        Assert.False(Directory.Exists(Path.Combine(_snapshot.ContextPath, ".aspire")));
        Assert.True(File.Exists(Path.Combine(_snapshot.ContextPath, "marker")));
    }

    [When("the source snapshot is rejected")]
    public async Task RejectSnapshot() => _error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        RailwaySourceUpload.CreateAsync(new RailwayBuildOptions { ContextPath = _context }, "secret-token", TestContext.Current.CancellationToken));

    [Then("the upload error does not disclose the credential")]
    public void RedactedError() => Assert.DoesNotContain("secret-token", _error!.ToString(), StringComparison.Ordinal);

    [Given("a source context containing Railway config (.*)")]
    public void ConflictingConfig(string path)
    {
        ContextWithSecrets();
        string config = Path.Combine(_context, path);
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "private-config-content");
    }

    [Then("conflicting config as code is reported without its contents")]
    public void ConfigDiagnostic()
    {
        Assert.Contains("config-as-code overrides the declared deployment options", _error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-config-content", _error.ToString(), StringComparison.Ordinal);
    }

    [Given("a source context ignoring its nested Dockerfile and Docker ignore file")]
    public void IgnoredControlFiles()
    {
        ContextWithSecrets();
        Directory.CreateDirectory(Path.Combine(_context, "jobs"));
        File.WriteAllText(Path.Combine(_context, "jobs", "Dockerfile"), "FROM scratch\n");
        File.WriteAllText(Path.Combine(_context, ".dockerignore"), "jobs/\n.dockerignore\n");
        File.WriteAllText(Path.Combine(_context, ".railwayignore"), "jobs/Dockerfile\n");
    }

    [When("the nested source context is snapshotted")]
    public async Task SnapshotNested() => _snapshot = await RailwaySourceUpload.CreateAsync(
        new RailwayBuildOptions { ContextPath = _context, DockerfilePath = "./jobs/Dockerfile" }, "secret-token", TestContext.Current.CancellationToken);

    [Then("Docker build control files are explicitly retained in the CLI upload rules")]
    public void RetainedControlFiles()
    {
        string rules = File.ReadAllText(Path.Combine(_snapshot!.ContextPath, ".railwayignore"));
        Assert.EndsWith("!/.dockerignore\n!/jobs/\n!/jobs/Dockerfile\n", rules, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_snapshot.ContextPath, "jobs", "Dockerfile")));
        Assert.True(File.Exists(Path.Combine(_snapshot.ContextPath, ".dockerignore")));
    }

    [When("source snapshots surround an executable mode change where supported")]
    public async Task SnapshotModeChange()
    {
        RailwayBuildOptions build = new() { ContextPath = _context };
        using RailwaySourceUpload first = await RailwaySourceUpload.CreateAsync(build, "secret-token", TestContext.Current.CancellationToken);
        _fingerprint = first.Fingerprint;
        if (!OperatingSystem.IsWindows())
        {
            string file = Path.Combine(_context, "marker");
            File.SetUnixFileMode(file, File.GetUnixFileMode(file) ^ UnixFileMode.UserExecute);
        }
        _snapshot = await RailwaySourceUpload.CreateAsync(build, "secret-token", TestContext.Current.CancellationToken);
    }

    [Then("Unix executable mode changes are copied and alter source identity")]
    public void ModeIdentity()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(_fingerprint, _snapshot!.Fingerprint);
        }
        else
        {
            Assert.NotEqual(_fingerprint, _snapshot!.Fingerprint);
            Assert.Equal(File.GetUnixFileMode(Path.Combine(_context, "marker")), File.GetUnixFileMode(Path.Combine(_snapshot.ContextPath, "marker")));
        }
    }

    [Given("an invalid source declaration with (.*)")]
    public void InvalidDeclaration(string conflict)
    {
        SourceService();
        if (conflict == "retained image")
        {
            _options.Image = "example/image@sha256:" + new string('a', 64);
        }
        else if (conflict == "escaping Dockerfile")
        {
            _options.Build!.DockerfilePath = "../Dockerfile";
        }
        else
        {
            _options.Build!.BuildArguments = [new RailwayBuildArgument { Name = "RAILWAY_TOKEN", Value = "unsafe" }];
        }
    }

    [When("the source declaration is validated")]
    public void ValidateDeclaration() => _error = Record.Exception(() => RailwayServiceValidation.Validate(_options));

    [Then("the declaration is rejected")]
    public void RejectedDeclaration() => Assert.IsType<ArgumentException>(_error);

    public void Dispose()
    {
        _snapshot?.Dispose();
        _provider.Dispose();
        if (Directory.Exists(_context))
        {
            Directory.Delete(_context, recursive: true);
        }
    }
}
