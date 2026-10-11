using System.Diagnostics;
using System.Text.Json.Nodes;
using Aspire.Hosting.Railway.Deployment;
using Aspire.Hosting.Railway.Management;
using Reqnroll;
using Xunit;

namespace Aspire.Hosting.Railway.Tests;

[Binding]
public sealed class VolumeRegionSteps : IDisposable
{
    private const string Image = "example/image@sha256:" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly ReconciliationContractTests.Provider _provider = new();
    private readonly RailwayServiceOptions _options = new();
    private readonly JsonObject _identity = [];
    private Exception? _error;
    private int _mutationsBeforeReplay;
    private RailwayServiceResult? _first;
    private RailwayServiceResult? _replay;
    private CancellationTokenSource? _callerCancellation;
    private bool _outerDeadlineReached;
    private TimeSpan _applyElapsed;

    [Given("a Railway service with region alias (.*)")]
    public void Region(string region) => _options.Region = region;

    [Given("it needs a new persistent volume")]
    public void NewVolume() => _options.Volumes.Add(new() { MountPath = "/data" });

    [Given("Railway defaults deployments without an explicit service region to SFO")]
    public void ProviderDefault() => _provider.MaterializeDefaultWithoutExplicitRegion = true;

    [Given("the regional workload uses a source build")]
    public void SourceBuild() => _options.Build = new RailwayBuildOptions { ContextPath = Path.GetTempPath() };

    [Given("the regional volume workload uses (.*) publishing")]
    public void PublishingMode(string mode)
    {
        if (mode == "source")
        { SourceBuild(); }
    }

    [Given("the created volume initially omits its (.*) proof")]
    public void IncompleteCreatedVolume(string field)
    {
        _provider.CreatedVolumeIncompleteField = field == "service" ? "serviceId" : "region";
        _provider.CreatedVolumeIncompleteReads = 1;
    }

    [Given("the created volume never supplies its service proof")]
    public void IncompleteForever()
    {
        _provider.CreatedVolumeIncompleteReads = int.MaxValue;
        _options.DeploymentTimeout = TimeSpan.FromMilliseconds(100);
    }

    [Given("a created volume read takes longer than the deployment budget and (.*) cancellation")]
    public void SlowVolumeRead(string behavior)
    {
        _options.DeploymentTimeout = TimeSpan.FromMilliseconds(250);
        _provider.CreatedVolumeReadDelay = TimeSpan.FromSeconds(2);
        _provider.IgnoreCreatedVolumeReadCancellation = behavior == "ignores";
    }

    [Given("two new volume proofs together exceed one deployment budget")]
    public void SharedVolumeBudget()
    {
        NewVolume();
        _options.Volumes.Add(new() { MountPath = "/second" });
        _options.DeploymentTimeout = TimeSpan.FromMilliseconds(900);
        _provider.CreatedVolumeReadDelay = TimeSpan.FromMilliseconds(700);
    }

    [Given("region configuration exhausts the deployment budget")]
    public void ConfigurationBudget()
    {
        _options.DeploymentTimeout = TimeSpan.FromMilliseconds(250);
        _provider.RegionUpdateDelay = TimeSpan.FromMilliseconds(600);
    }

    [Given("the caller cancels during the created volume read")]
    public void CancelVolumeRead()
    {
        _callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _provider.CreatedVolumeRead = _callerCancellation.Cancel;
    }

    [Given("the created volume has (.*) drift")]
    public void CreatedDrift(string field) => _provider.CreatedVolumeDrift = field;

    [Given("the deployment materializes in SFO")]
    public void DeploymentDrift() => _provider.ForcedDeploymentRegion = "sfo";

    [Given("an existing owned volume is in SFO")]
    public void ExistingDrift() => ExistingVolume("sfo");

    [Given("an existing owned volume reports its Amsterdam provider name")]
    public void ExistingProviderName() => ExistingVolume("europe-west4-drams3a");

    private void ExistingVolume(string region)
    {
        NewVolume();
        _provider.CreateService(marked: true);
        _provider.Volumes.Add(new JsonObject
        {
            ["node"] = new JsonObject
            { ["volumeId"] = "existing-volume", ["serviceId"] = "service", ["mountPath"] = "/data", ["region"] = region }
        });
    }

    [When("the regional service is deployed")]
    public async Task Deploy() => _first = await ApplyAsync();

    [When("the regional service is deployed twice")]
    public async Task DeployTwice()
    {
        await Deploy();
        _mutationsBeforeReplay = _provider.Mutations;
        _replay = await ApplyAsync();
    }

    [When("the regional service is rejected")]
    public async Task Rejected()
    {
        _error = await Record.ExceptionAsync(ApplyAsync);
        Assert.IsType<InvalidOperationException>(_error);
    }

    [When("the created volume readback reaches its deadline")]
    public async Task ReadbackDeadline()
    {
        using CancellationTokenSource outer = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        outer.CancelAfter(TimeSpan.FromSeconds(3));
        Stopwatch elapsed = Stopwatch.StartNew();
        _error = await Record.ExceptionAsync(() => ApplyAsync(outer.Token));
        _applyElapsed = elapsed.Elapsed;
        _outerDeadlineReached = outer.IsCancellationRequested;
        Assert.IsType<TimeoutException>(_error);
    }

    [When("the cancelled regional service is applied")]
    public async Task Cancelled()
    {
        _error = await Record.ExceptionAsync(() => ApplyAsync(_callerCancellation!.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(_error);
    }

    [Then("the deployment deadline stops the in-flight read without caller cancellation")]
    public void OwnDeadline()
    {
        Assert.False(_outerDeadlineReached);
        Assert.True(_applyElapsed < _options.DeploymentTimeout + TimeSpan.FromMilliseconds(300),
            $"The operation took {_applyElapsed} with a deployment budget of {_options.DeploymentTimeout}.");
    }

    [Then("both created volume IDs remain recorded")]
    public void BothVolumesRecorded()
    {
        Assert.Equal("volume-0", (string?)_identity["volumes"]?["/data"]);
        Assert.Equal("volume-1", (string?)_identity["volumes"]?["/second"]);
    }

    [Then("no volume is created after the expired configuration budget")]
    public void NoLateVolume() => Assert.Empty(_provider.Volumes);

    [Then("caller cancellation remains cancellation")]
    public void CallerCancellation() => Assert.True(_callerCancellation!.IsCancellationRequested);

    [Then("the explicit provider region is (.*)")]
    public void ProviderRegion(string region) => Assert.Equal(region, _provider.ConfiguredProviderRegion);

    [Then("the deployment has one replica in (.*)")]
    public void SingleReplica(string region)
    {
        Assert.Equal(region, Assert.Single(_provider.Regions).Key);
        Assert.Equal(1, (int)_provider.Regions[region]!["numReplicas"]!);
    }

    [Then("region configuration precedes volume creation and deployment")]
    public void Ordering()
    {
        Assert.True(_provider.Operations.IndexOf("region-update") < _provider.Operations.IndexOf("volume-create"));
        Assert.True(_provider.Operations.IndexOf("volume-create") < _provider.Operations.IndexOf("deployment"));
        Assert.Equal("europe-west4-drams3a", _provider.ConfiguredProviderRegion);
    }

    [Then("the volume binding completes before deployment")]
    public void CompleteBinding()
    {
        Assert.Equal(1, _provider.IncompleteVolumeReadCount);
        Assert.True(_provider.Operations.IndexOf("incomplete-volume-read") < _provider.Operations.IndexOf("volume-proof-read"));
        Assert.True(_provider.Operations.IndexOf("volume-proof-read") < _provider.Operations.IndexOf("deployment"));
    }

    [Then("the unchanged replay has no provider mutations")]
    public void Replay()
    {
        Assert.Equal(_mutationsBeforeReplay, _provider.Mutations);
        Assert.False(_replay!.Deployed);
        Assert.Equal(_first!.ServiceId, _replay.ServiceId);
        Assert.Equal(_first.DeploymentId, _replay.DeploymentId);
    }

    [Then("the persistent volume remains in AMS")]
    public void VolumeRegion()
    {
        string region = (string)Assert.Single(_provider.Volumes)!["node"]!["region"]!;
        Assert.True(region is "ams" or "europe-west4-drams3a");
    }

    [Then("no deployment was requested")]
    public void NoDeployment() => Assert.Equal(0, _provider.DeployRequests);

    [Then("no provider mutation or deployment was requested")]
    public void ReadOnly()
    {
        NoDeployment();
        Assert.Equal(0, _provider.Mutations);
    }

    [Then("the created volume remains recorded for operator reconciliation")]
    public void RecordedVolume()
    {
        Assert.Equal("volume-0", (string?)_identity["volumes"]?["/data"]);
        Assert.Contains("created Railway volume", _error!.Message, StringComparison.Ordinal);
    }

    [Then("its sent deployment remains recorded for reconciliation")]
    public void RecordedDeployment()
    {
        Assert.True((bool?)_identity["pending"]);
        Assert.True((bool?)_identity["deploymentAttempt"]?["sent"]);
        Assert.Contains("requested single region", _error!.Message, StringComparison.Ordinal);
    }

    [Then("the Railway integration assembly version is 1.0.0.0")]
    public void AssemblyIdentity() => Assert.Equal(new Version(1, 0, 0, 0), typeof(RailwayServiceOptions).Assembly.GetName().Version);

    private Task<RailwayServiceResult> ApplyAsync() => ApplyAsync(TestContext.Current.CancellationToken);

    private async Task<RailwayServiceResult> ApplyAsync(CancellationToken cancellationToken)
    {
        using HttpClient http = new(_provider, disposeHandler: false);
        RailwayServiceReconciler reconciler = new(new RailwayManagementClient(http, "secret-token", RailwayAuthenticationMode.ProjectToken));
        return await reconciler.ApplyAsync(new RailwayResolvedTarget("project", "environment", "site", new()),
            "web", "web", _options.Build is null ? Image : "source:explicit-snapshot", _options, new(StringComparer.Ordinal), _identity, () => Task.CompletedTask,
            cancellationToken, upload: _options.Build is null ? null : (_, _, _) => Task.FromResult(_provider.UploadSource()));
    }

    public void Dispose()
    {
        _callerCancellation?.Dispose();
        _provider.Dispose();
    }
}
