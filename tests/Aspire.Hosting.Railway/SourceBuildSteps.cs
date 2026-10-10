using System.Text.Json.Nodes;
using Aspire.Hosting.Railway.Deployment;
using Aspire.Hosting.Railway.Management;
using Reqnroll;
using Xunit;

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

    [When("the source service is rejected")]
    public async Task RejectService() => await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync());

    private async Task<RailwayServiceResult> ApplyAsync(bool loseResponse = false)
    {
        using HttpClient http = new(_provider, disposeHandler: false);
        RailwayServiceReconciler reconciler = new(new RailwayManagementClient(http, "secret-token", RailwayAuthenticationMode.ProjectToken));
        return await reconciler.ApplyAsync(new RailwayResolvedTarget("project", "environment", "site", new()),
            "web", "web", "source:sha256:explicit-snapshot", _options, [], _identity, () => Task.CompletedTask,
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

    [Then("the completed deployment exposes its Railway image")]
    public void ExactImage() => Assert.Equal("registry.railway.app/railway-build:retained", _result!.Image);

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
