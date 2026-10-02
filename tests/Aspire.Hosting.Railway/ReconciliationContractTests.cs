using System.Net;
using System.Text.Json.Nodes;
using Aspire.Hosting.Railway.Deployment;
using Aspire.Hosting.Railway.Management;
using Xunit;

namespace Aspire.Hosting.Railway.Tests;

public sealed class ReconciliationContractTests
{
    private const string Image = "ghcr.io/pinguapps/test@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string UpdatedImage = "ghcr.io/pinguapps/test@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData("deployment")]
    [InlineData("project")]
    [InlineData("environment")]
    [InlineData("service")]
    [InlineData("meta")]
    [InlineData("patch")]
    [InlineData("snapshot")]
    [InlineData("variables")]
    [InlineData("marker")]
    public async Task MissingCorrelationFieldWaitsForTheSameDeploymentWithoutAnotherRequest(string field)
    {
        using Provider provider = new() { MissingCorrelationField = field, MissingCorrelationResponses = 1 };
        RailwayServiceResult result = await ApplyAsync(provider, new(), []);
        Assert.Equal("deployment", result.DeploymentId);
        Assert.Equal(["deployment", "deployment"], provider.CorrelationReadIds);
        Assert.Equal(1, provider.DeployRequests);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("environment")]
    [InlineData("service")]
    [InlineData("patch")]
    [InlineData("marker")]
    public async Task PresentCorrelationMismatchFailsImmediatelyEvenWhenAnotherFieldIsMissing(string field)
    {
        using Provider provider = new() { WrongCorrelationField = field, MissingCorrelationField = field == "marker" ? "patch" : "marker", MissingCorrelationResponses = int.MaxValue };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(provider, new(), []));
        Assert.Single(provider.CorrelationReadIds);
        Assert.Equal(1, provider.DeployRequests);
    }

    [Theory]
    [InlineData("deployment")]
    [InlineData("project")]
    [InlineData("environment")]
    [InlineData("service")]
    [InlineData("meta")]
    [InlineData("patch")]
    [InlineData("snapshot")]
    [InlineData("variables")]
    [InlineData("marker")]
    public async Task UnavailableCorrelationTimesOutAndResumesWithoutAnotherFiniteExecution(string field)
    {
        using Provider provider = new() { MissingCorrelationField = field, MissingCorrelationResponses = int.MaxValue, InstanceStatus = "EXITED", Stopped = true };
        JsonObject identity = [];
        RailwayServiceOptions options = new() { WaitForCompletion = true, RestartPolicy = RailwayRestartPolicy.Never, DeploymentTimeout = TimeSpan.FromMilliseconds(75) };
        TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(() => ApplyAsync(provider, options, identity));
        Assert.Contains("association", error.Message, StringComparison.Ordinal);
        Assert.True((bool)identity["deploymentAttempt"]!["sent"]!);
        Assert.Null(identity["deploymentAttempt"]!["id"]);
        provider.MissingCorrelationResponses = 0;
        options.DeploymentTimeout = TimeSpan.FromSeconds(5);
        RailwayServiceResult resumed = await ApplyAsync(provider, options, identity);
        Assert.Equal("deployment", resumed.DeploymentId);
        Assert.All(provider.CorrelationReadIds, id => Assert.Equal("deployment", id));
        Assert.Equal(1, provider.DeployRequests);
    }

    [Fact]
    public async Task CancellationWhileCorrelationIsUnavailablePreservesTheSentAttempt()
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using Provider provider = new() { MissingCorrelationField = "marker", MissingCorrelationResponses = int.MaxValue, CorrelationRead = cancellation.Cancel, InstanceStatus = "EXITED", Stopped = true };
        JsonObject identity = [];
        RailwayServiceOptions options = new() { WaitForCompletion = true, RestartPolicy = RailwayRestartPolicy.Never };
        using HttpClient httpClient = new(provider, disposeHandler: false);
        RailwayServiceReconciler reconciler = new(new RailwayManagementClient(httpClient, "secret-token", RailwayAuthenticationMode.ProjectToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconciler.ApplyAsync(Target(), "web", "web", Image, options, new(StringComparer.Ordinal), identity, () => Task.CompletedTask, cancellation.Token));
        Assert.True((bool)identity["deploymentAttempt"]!["sent"]!);
        provider.CorrelationRead = null;
        provider.MissingCorrelationResponses = 0;
        await ApplyAsync(provider, options, identity);
        Assert.Equal(1, provider.DeployRequests);
    }

    [Fact]
    public async Task ScopedCommitQueueReferenceCorrelatesWithTheDeploymentPatchId()
    {
        using Provider provider = new() { ReturnCompositePatchReference = true };
        RailwayServiceResult result = await ApplyAsync(provider, new(), []);
        Assert.Equal("deployment", result.DeploymentId);
        Assert.Equal(1, provider.DeployRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkerlessSentLegacyRequestRequiresAnAlreadyRecordedExactId(bool recordedId)
    {
        using Provider provider = new() { FailNextOutputRead = true, InstanceStatus = "EXITED", Stopped = true };
        JsonObject identity = [];
        RailwayServiceOptions options = new() { WaitForCompletion = true, RestartPolicy = RailwayRestartPolicy.Never };
        await Assert.ThrowsAsync<HttpRequestException>(() => ApplyAsync(provider, options, identity));
        identity["deploymentAttempt"]!.AsObject().Remove("requestId");
        if (recordedId)
        {
            RailwayServiceResult result = await ApplyAsync(provider, options, identity);
            Assert.Equal("deployment", result.DeploymentId);
        }
        else
        {
            identity["deploymentAttempt"]!.AsObject().Remove("id");
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(provider, options, identity));
            Assert.Contains("reconciliation", error.Message, StringComparison.Ordinal);
        }
        Assert.Equal(1, provider.DeployRequests);
    }

    [Fact]
    public async Task UnrelatedSameImageDeploymentCannotSatisfyTheRecordedRequest()
    {
        using Provider provider = new() { WrongRequestMarker = true, InstanceStatus = "EXITED", Stopped = true };
        JsonObject identity = [];
        RailwayServiceOptions options = new() { WaitForCompletion = true, RestartPolicy = RailwayRestartPolicy.Never };
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(provider, options, identity));
        Assert.Contains("association", error.Message, StringComparison.Ordinal);
        Assert.Equal(Image, provider.DeployedImage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(provider, options, identity));
        Assert.Equal(1, provider.DeployRequests);
    }

    [Fact]
    public async Task ReservedRequestVariableFailsBeforeAnyMutationOrAttempt()
    {
        using Provider provider = new();
        JsonObject identity = [];
        using HttpClient httpClient = new(provider, disposeHandler: false);
        RailwayServiceReconciler reconciler = new(new RailwayManagementClient(httpClient, "secret-token", RailwayAuthenticationMode.ProjectToken));
        await Assert.ThrowsAsync<ArgumentException>(() => reconciler.ApplyAsync(Target(), "web", "web", Image, new(),
            new(StringComparer.Ordinal) { ["PINGUAPPS_DEPLOYMENT_REQUEST"] = "user-value" }, identity, () => Task.CompletedTask, TestContext.Current.CancellationToken));
        Assert.Equal(0, provider.Mutations);
        Assert.Null(identity["deploymentAttempt"]);
    }

    [Fact]
    public async Task ChangedOrdinaryIntentRemovesVariablesFromAnUncertainAcceptedPatch()
    {
        using Provider provider = new() { LoseNextDeployResponse = true };
        JsonObject identity = [];
        await Assert.ThrowsAsync<HttpRequestException>(() => ApplyAsync(provider, new(), identity,
            variables: new(StringComparer.Ordinal) { ["REMOVED"] = "runtime-value" }));
        Assert.Contains("REMOVED", identity["deploymentAttempt"]!["managedVariables"]!.AsArray().Select(name => (string)name!));
        await ApplyAsync(provider, new(), identity);
        Assert.False(provider.HasVariable("REMOVED"));
        Assert.True(provider.LastPatch!["variables"]!.AsObject().ContainsKey("REMOVED"));
        Assert.Null(provider.LastPatch["variables"]!["REMOVED"]);
    }

    [Fact]
    public async Task UnchangedCronActivationStillRequiresExactImageMetadata()
    {
        using Provider provider = new() { InstanceStatus = "CREATED" };
        JsonObject identity = [];
        RailwayServiceOptions options = new() { CronSchedule = "*/5 * * * *", RestartPolicy = RailwayRestartPolicy.Never };
        await ApplyAsync(provider, options, identity);
        provider.OmitReportedImage = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(provider, options, identity));
        Assert.Equal(1, provider.DeployRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedRetainedImageDeploysTheConfiguredSource(bool finite)
    {
        using Provider provider = new() { InstanceStatus = finite ? "EXITED" : "RUNNING", Stopped = finite };
        JsonObject identity = [];
        RailwayServiceOptions options = new() { WaitForCompletion = finite, RestartPolicy = finite ? RailwayRestartPolicy.Never : RailwayRestartPolicy.OnFailure };
        await ApplyAsync(provider, options, identity);
        RailwayServiceResult updated = await ApplyAsync(provider, options, identity, UpdatedImage);
        Assert.Equal("deployment-2", updated.DeploymentId);
        Assert.Equal(UpdatedImage, provider.DeployedImage);
        Assert.True(provider.LastDeployWasFromSource);
        if (!finite)
        {
            int requests = provider.DeployRequests;
            Assert.False((await ApplyAsync(provider, options, identity, UpdatedImage)).Deployed);
            Assert.Equal(requests, provider.DeployRequests);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongInitialDeploymentImageCannotPassCompletion(bool finite)
    {
        using Provider provider = new() { ForcedDeploymentImage = UpdatedImage, InstanceStatus = finite ? "EXITED" : "RUNNING", Stopped = finite };
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ApplyAsync(provider,
            new() { WaitForCompletion = finite, RestartPolicy = finite ? RailwayRestartPolicy.Never : RailwayRestartPolicy.OnFailure }, []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingImageMetadataCannotProveSuccessfulCompletion(bool finite)
    {
        using Provider provider = new() { OmitReportedImage = true, InstanceStatus = finite ? "EXITED" : "RUNNING", Stopped = finite };
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ApplyAsync(provider,
            new() { WaitForCompletion = finite, RestartPolicy = finite ? RailwayRestartPolicy.Never : RailwayRestartPolicy.OnFailure }, []));
    }

    [Fact]
    public async Task ConfiguredNewImageWithStaleDeploymentIsRecoveredByThePatch()
    {
        using Provider provider = new();
        JsonObject identity = [];
        await ApplyAsync(provider, new(), identity);
        provider.SetConfiguredImage(UpdatedImage);
        RailwayServiceResult result = await ApplyAsync(provider, new(), identity, UpdatedImage);
        Assert.Equal("deployment-2", result.DeploymentId);
        Assert.Equal(UpdatedImage, provider.DeployedImage);
        Assert.Equal(2, provider.DeployRequests);
    }

    [Fact]
    public async Task UnchangedFiniteInvocationForcesOneRealConfigurationChange()
    {
        using Provider provider = new() { InstanceStatus = "EXITED", Stopped = true };
        RailwayServiceOptions options = new() { WaitForCompletion = true, RestartPolicy = RailwayRestartPolicy.Never };
        JsonObject identity = [];
        await ApplyAsync(provider, options, identity);
        await ApplyAsync(provider, options, identity);
        Assert.Equal(2, provider.AcceptedRequestMarkers.Count);
        Assert.NotEqual(provider.AcceptedRequestMarkers[0], provider.AcceptedRequestMarkers[1]);
    }

    [Fact]
    public async Task LostChangedConfigurationResponseRecoversWithoutAnotherPatch()
    {
        using Provider provider = new() { InstanceStatus = "EXITED", Stopped = true };
        RailwayServiceOptions options = new() { WaitForCompletion = true, RestartPolicy = RailwayRestartPolicy.Never };
        JsonObject identity = [];
        await ApplyAsync(provider, options, identity);
        provider.LoseNextDeployResponse = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => ApplyAsync(provider, options, identity, UpdatedImage));
        string requestId = (string)identity["deploymentAttempt"]!["requestId"]!;
        RailwayServiceResult recovered = await ApplyAsync(provider, options, identity, UpdatedImage);
        Assert.Equal("deployment-2", recovered.DeploymentId);
        Assert.Equal(UpdatedImage, provider.DeployedImage);
        Assert.Equal(2, provider.DeployRequests);
        Assert.Equal(requestId, provider.AcceptedRequestMarkers[^1]);
    }

    [Fact]
    public async Task WrongRetainedDeploymentImageCannotPassUnchangedReconciliation()
    {
        using Provider provider = new();
        JsonObject identity = [];
        await ApplyAsync(provider, new(), identity);
        provider.ReportedImage = UpdatedImage;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ApplyAsync(provider, new(), identity));
        Assert.Equal(1, provider.DeployRequests);
    }

    [Fact]
    public async Task WrongRecoveredFiniteImageCannotPassOrExecuteAgain()
    {
        using Provider provider = new() { Status = "SUCCESS", InstanceStatus = "EXITED", Stopped = true, ForcedDeploymentImage = UpdatedImage, LoseNextDeployResponse = true };
        JsonObject identity = [];
        RailwayServiceOptions options = new() { RestartPolicy = RailwayRestartPolicy.Never, WaitForCompletion = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => ApplyAsync(provider, options, identity));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ApplyAsync(provider, options, identity));
        Assert.Equal(1, provider.DeployRequests);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("environment")]
    [InlineData("site")]
    public async Task ScopeMismatchCannotMutate(string mismatch)
    {
        using Provider provider = new();
        if (mismatch == "project")
        { provider.TokenProject = "another-project"; }
        if (mismatch == "environment")
        { provider.TokenEnvironment = "another-environment"; }
        if (mismatch == "site")
        { provider.Site = "another-site"; }
        await Assert.ThrowsAsync<InvalidOperationException>(() => PreflightAsync(provider, new(), []));
        Assert.Equal(0, provider.Mutations);
    }

    [Fact]
    public async Task NewServiceUnsupportedRegionFailsBeforeAnyMutation()
    {
        using Provider provider = new();
        RailwayServiceOptions options = new() { Region = "unsupported" };
        await Assert.ThrowsAsync<ArgumentException>(() => PreflightAsync(provider, options, []));
        await Assert.ThrowsAsync<ArgumentException>(() => ApplyAsync(provider, options, []));
        Assert.Equal(0, provider.Mutations);
        Assert.Equal(0, provider.Creates);
    }

    [Theory]
    [InlineData(RailwayOwnershipMode.CreateOnly)]
    [InlineData(RailwayOwnershipMode.ExistingOnly)]
    [InlineData(RailwayOwnershipMode.CreateOrAdopt)]
    public async Task UnprovenExistingServiceCannotBeAdopted(RailwayOwnershipMode mode)
    {
        using Provider provider = new();
        provider.CreateService(marked: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PreflightAsync(provider, new() { OwnershipMode = mode }, []));
        Assert.Equal(0, provider.Mutations);
    }

    [Fact]
    public async Task ExplicitUnmarkedServiceCanBeAdoptedAndRepeatHasNoMutations()
    {
        using Provider provider = new();
        provider.CreateService(marked: false);
        JsonObject identity = [];
        RailwayServiceOptions options = new() { ExistingServiceId = "service", OwnershipMode = RailwayOwnershipMode.ExistingOnly };
        await ApplyAsync(provider, options, identity);
        int mutations = provider.Mutations;
        RailwayServiceResult second = await ApplyAsync(provider, options, identity);
        Assert.False(second.Deployed);
        Assert.Equal(mutations, provider.Mutations);
        Assert.Equal("service", second.ServiceId);
    }

    [Fact]
    public async Task PartialApplicationReusesTheRecordedService()
    {
        using Provider provider = new() { FailNextPatch = true };
        JsonObject identity = [];
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ApplyAsync(provider, new(), identity));
        Assert.Equal("service", (string?)identity["serviceId"]);
        await ApplyAsync(provider, new(), identity);
        Assert.Equal(1, provider.Creates);
    }

    [Fact]
    public async Task FailureAfterConfigurationCommitStillDeploysOnRetry()
    {
        using Provider provider = new() { FailNextDeploy = true };
        JsonObject identity = [];
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ApplyAsync(provider, new(), identity));
        Assert.True((bool)identity["pending"]!);
        RailwayServiceResult retry = await ApplyAsync(provider, new(), identity);
        Assert.True(retry.Deployed);
        Assert.False((bool)identity["pending"]!);
        Assert.Equal(1, provider.Creates);
        Assert.Equal(2, provider.DeployRequests);
    }

    [Fact]
    public async Task RejectedConfigurationDeploymentDoesNotBlindlyRetry()
    {
        using Provider provider = new() { InitialMissingResponses = 1 };
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ApplyAsync(provider, new(), []));
        Assert.Equal(1, provider.DeployRequests);
        Assert.Equal(1, provider.Creates);
    }

    [Fact]
    public async Task RejectedFiniteRequestAllowsFreshIntentAfterOperatorDeployment()
    {
        using Provider provider = new() { FailNextDeploy = true, InstanceStatus = "EXITED", Stopped = true };
        JsonObject identity = [];
        RailwayServiceOptions options = new() { RestartPolicy = RailwayRestartPolicy.Never, WaitForCompletion = true };
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ApplyAsync(provider, options, identity));
        Assert.Null(identity["deploymentAttempt"]);
        provider.CreateOperatorDeployment();
        int requests = provider.DeployRequests;
        RailwayServiceResult result = await ApplyAsync(provider, options, identity, UpdatedImage);
        Assert.Equal("deployment-2", result.DeploymentId);
        Assert.Equal(UpdatedImage, provider.DeployedImage);
        Assert.Equal(requests + 1, provider.DeployRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyUnsentAttemptReceivesMarkerAndCurrentBaseline(bool changeImage)
    {
        using Provider provider = new() { FailNextDeploy = true, InstanceStatus = "EXITED", Stopped = true };
        JsonObject identity = [];
        JsonObject? savedAttempt = null;
        RailwayServiceOptions options = new() { RestartPolicy = RailwayRestartPolicy.Never, WaitForCompletion = true };
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ApplyAsync(provider, options, identity, saveIdentity: () =>
        {
            if (identity["deploymentAttempt"] is JsonObject attempt)
            { savedAttempt = attempt.DeepClone().AsObject(); }
            return Task.CompletedTask;
        }));
        Assert.NotNull(savedAttempt);
        savedAttempt["sent"] = false;
        savedAttempt.Remove("requestId");
        savedAttempt.Remove("operation");
        identity["deploymentAttempt"] = savedAttempt;
        provider.CreateOperatorDeployment();
        string image = changeImage ? UpdatedImage : Image;
        RailwayServiceResult result = await ApplyAsync(provider, options, identity, image);
        Assert.Equal("deployment-2", result.DeploymentId);
        Assert.Equal(image, provider.DeployedImage);
        Assert.Matches("^[a-f0-9]{32}$", provider.AcceptedRequestMarkers[^1]);
    }

    [Fact]
    public async Task LostLaterFiniteResponseRecoversItsExactDeploymentWithoutExecutingTwice()
    {
        using Provider provider = new() { Status = "SUCCESS", InstanceStatus = "EXITED", Stopped = true };
        JsonObject identity = [];
        RailwayServiceOptions options = new() { RestartPolicy = RailwayRestartPolicy.Never, WaitForCompletion = true };
        await ApplyAsync(provider, options, identity);
        provider.LoseNextDeployResponse = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => ApplyAsync(provider, options, identity));
        Assert.NotNull(identity["deploymentAttempt"]?["baseline"]);
        Assert.True((bool)identity["pending"]!);
        int requests = provider.DeployRequests;
        RailwayServiceResult recovered = await ApplyAsync(provider, options, identity);
        Assert.Equal("deployment-2", recovered.DeploymentId);
        Assert.Equal(requests, provider.DeployRequests);
        Assert.Null(identity["deploymentAttempt"]);
    }

    [Fact]
    public async Task UnknownAcceptedResponseNeverBlindlyResendsAFiniteRequest()
    {
        using Provider provider = new() { LoseNextDeployResponse = true, HideDeploymentIds = true };
        JsonObject identity = [];
        RailwayServiceOptions options = new() { RestartPolicy = RailwayRestartPolicy.Never, WaitForCompletion = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => ApplyAsync(provider, options, identity));
        int requests = provider.DeployRequests;
        options.DeploymentTimeout = TimeSpan.FromMilliseconds(1);
        await Assert.ThrowsAsync<TimeoutException>(() => ApplyAsync(provider, options, identity));
        Assert.Equal(requests, provider.DeployRequests);
    }

    [Fact]
    public async Task PendingFiniteConfigurationChangesFailBeforeFurtherMutation()
    {
        using Provider provider = new() { LoseNextDeployResponse = true };
        JsonObject identity = [];
        RailwayServiceOptions options = new() { RestartPolicy = RailwayRestartPolicy.Never, WaitForCompletion = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => ApplyAsync(provider, options, identity));
        int mutations = provider.Mutations;
        options.StartCommand = "a different executable";
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(provider, options, identity));
        Assert.Equal(mutations, provider.Mutations);
    }

    [Fact]
    public async Task OutputReadFailureDoesNotExecuteACompletedFiniteProcessAgain()
    {
        using Provider provider = new() { Status = "SUCCESS", InstanceStatus = "EXITED", Stopped = true, FailNextOutputRead = true };
        JsonObject identity = [];
        RailwayServiceOptions options = new() { RestartPolicy = RailwayRestartPolicy.Never, WaitForCompletion = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => ApplyAsync(provider, options, identity));
        int requests = provider.DeployRequests;
        Assert.Equal("deployment", identity["deploymentAttempt"]?["id"]?.GetValue<string>());
        await ApplyAsync(provider, options, identity);
        Assert.Equal(requests, provider.DeployRequests);
    }

    [Fact]
    public async Task KnownTerminalFiniteFailureAllowsANewIntentionalInvocation()
    {
        using Provider provider = new() { Status = "SUCCESS", InstanceStatus = "CRASHED", Stopped = true };
        JsonObject identity = [];
        RailwayServiceOptions options = new() { RestartPolicy = RailwayRestartPolicy.Never, WaitForCompletion = true };
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ApplyAsync(provider, options, identity));
        Assert.False((bool)identity["pending"]!);
        Assert.Null(identity["deploymentAttempt"]);
        provider.InstanceStatus = "EXITED";
        RailwayServiceResult next = await ApplyAsync(provider, options, identity);
        Assert.Equal("deployment-2", next.DeploymentId);
        Assert.Equal(2, provider.DeployRequests);
    }

    [Fact]
    public async Task KnownTerminalOrdinaryFailureAllowsANewIntentionalInvocation()
    {
        using Provider provider = new() { Status = "FAILED" };
        JsonObject identity = [];
        RailwayServiceOptions options = new();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ApplyAsync(provider, options, identity));
        Assert.True((bool)identity["pending"]!);
        Assert.Null(identity["deploymentAttempt"]);
        Assert.Null(identity["applyPhase"]);
        provider.Status = "SUCCESS";
        RailwayServiceResult next = await ApplyAsync(provider, options, identity);
        Assert.Equal("deployment-2", next.DeploymentId);
        Assert.Equal(2, provider.DeployRequests);
        Assert.False((bool)identity["pending"]!);
    }

    [Fact]
    public async Task LegacyUncertainFiniteStateCannotSilentlyStartAnotherExecution()
    {
        using Provider provider = new();
        provider.CreateService(marked: true);
        JsonObject identity = Identity();
        identity["pending"] = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(provider,
            new() { RestartPolicy = RailwayRestartPolicy.Never, WaitForCompletion = true }, identity));
        Assert.Equal(0, provider.Mutations);
    }

    [Fact]
    public async Task InitialDeploymentRejectsConcurrentAmbiguousIdentities()
    {
        using Provider provider = new() { AmbiguousInitialDeployments = true };
        InvalidOperationException error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ApplyAsync(provider, new(), []));
        Assert.Contains("ambiguous", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, provider.DeployRequests);
    }

    [Fact]
    public async Task AmbiguousCreateResponseRecoversOnlyItsRecordedClaim()
    {
        using Provider provider = new() { FailCreateResponse = true };
        JsonObject identity = [];
        RailwayServiceOptions options = new() { OwnershipMode = RailwayOwnershipMode.CreateOnly };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(provider, options, identity));
        Assert.Null(identity["serviceId"]);
        Assert.NotNull(identity["claim"]);
        await PreflightAsync(provider, options, identity);
        await ApplyAsync(provider, options, identity);
        Assert.Equal(1, provider.Creates);
    }

    [Fact]
    public async Task SealedValuesAndRegistryCredentialsNeverEnterDeploymentState()
    {
        using Provider provider = new();
        JsonObject identity = [];
        RailwayServiceOptions options = new();
        options.SealedVariables.Add("APP_PASSWORD");
        Dictionary<string, string> variables = new(StringComparer.Ordinal) { ["APP_PASSWORD"] = "runtime-secret" };
        Dictionary<string, string> hashes = new(StringComparer.Ordinal) { ["APP_PASSWORD"] = "nonsecret-hash" };
        JsonObject credentials = new() { ["username"] = "registry-user", ["password"] = "registry-secret" };
        using HttpClient http = new(provider, disposeHandler: false);
        RailwayServiceReconciler reconciler = new(new RailwayManagementClient(http, "secret-token", RailwayAuthenticationMode.ProjectToken));
        await reconciler.ApplyAsync(Target(), "web", "web", Image, options, variables, identity, () => Task.CompletedTask,
            TestContext.Current.CancellationToken, credentials, "registry-hash", hashes);
        int mutations = provider.Mutations;
        RailwayServiceResult repeat = await reconciler.ApplyAsync(Target(), "web", "web", Image, options, variables, identity, () => Task.CompletedTask,
            TestContext.Current.CancellationToken, credentials, "registry-hash", hashes);
        Assert.False(repeat.Deployed);
        Assert.Equal(mutations, provider.Mutations);
        Assert.Equal("registry-secret", (string?)provider.LastPatch?["deploy"]?["registryCredentials"]?["password"]);
        Assert.True((bool)provider.LastPatch?["variables"]?["APP_PASSWORD"]?["isSealed"]!);
        Assert.DoesNotContain("runtime-secret", identity.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("registry-secret", identity.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitRegionReplacesTheDefaultRatherThanAddingASecondRegion()
    {
        using Provider provider = new();
        JsonObject identity = [];
        RailwayServiceOptions options = new() { Region = "europe-west4-drams3a" };
        await ApplyAsync(provider, options, identity);
        Assert.Equal("ams", Assert.Single(provider.Regions).Key);
        Assert.Equal(1, (int)provider.Regions["ams"]!["numReplicas"]!);
        Assert.Null(provider.LastPatch?["deploy"]?["multiRegionConfig"]);
        int mutations = provider.Mutations;
        Assert.False((await ApplyAsync(provider, options, identity)).Deployed);
        Assert.Equal(mutations, provider.Mutations);
    }

    [Fact]
    public async Task RemovingRegistryCredentialsExplicitlyClearsTheirProviderConfiguration()
    {
        using Provider provider = new();
        JsonObject identity = [];
        using HttpClient http = new(provider, disposeHandler: false);
        RailwayServiceReconciler reconciler = new(new RailwayManagementClient(http, "secret-token", RailwayAuthenticationMode.ProjectToken));
        await reconciler.ApplyAsync(Target(), "web", "web", Image, new(), [], identity, () => Task.CompletedTask,
            TestContext.Current.CancellationToken, new JsonObject { ["username"] = "user", ["password"] = "registry-password" }, "registry-hash");
        Assert.Equal("registry-hash", (string?)identity["registryFingerprint"]);
        RailwayServiceResult removed = await ApplyAsync(provider, new(), identity);
        Assert.True(removed.Deployed);
        Assert.True(provider.LastPatch!["deploy"]!.AsObject().ContainsKey("registryCredentials"));
        Assert.Null(provider.LastPatch["deploy"]!["registryCredentials"]);
        Assert.Null(identity["registryFingerprint"]);
        Assert.False((await ApplyAsync(provider, new(), identity)).Deployed);
    }

    [Fact]
    public async Task RemovedManagedVariablesAreDeletedWithoutTouchingUnrelatedProviderValues()
    {
        using Provider provider = new();
        JsonObject identity = [];
        RailwayServiceOptions options = new();
        options.SealedVariables.Add("OLD_SECRET");
        using HttpClient http = new(provider, disposeHandler: false);
        RailwayServiceReconciler reconciler = new(new RailwayManagementClient(http, "secret-token", RailwayAuthenticationMode.ProjectToken));
        await reconciler.ApplyAsync(Target(), "web", "web", Image, options,
            new() { ["OLD_PLAIN"] = "old-value", ["OLD_SECRET"] = "old-secret" }, identity, () => Task.CompletedTask,
            TestContext.Current.CancellationToken, sealedFingerprints: new Dictionary<string, string> { ["OLD_SECRET"] = "secret-hash" });
        provider.SetUnmanagedVariable("UNRELATED", "keep-this");
        Assert.True(provider.ContainsVariable("OLD_PLAIN"));
        Assert.True(provider.ContainsVariable("OLD_SECRET"));
        await ApplyAsync(provider, new(), identity);
        Assert.False(provider.ContainsVariable("OLD_PLAIN"));
        Assert.False(provider.ContainsVariable("OLD_SECRET"));
        Assert.True(provider.ContainsVariable("UNRELATED"));
        Assert.False(provider.LastPatch!["variables"]!.AsObject().ContainsKey("UNRELATED"));
        Assert.DoesNotContain("old-secret", identity.ToJsonString(), StringComparison.Ordinal);
        Assert.False((await ApplyAsync(provider, new(), identity)).Deployed);
    }

    [Fact]
    public async Task MissingCachedIdentityCannotFallBackToName()
    {
        using Provider provider = new();
        JsonObject identity = Identity();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PreflightAsync(provider, new(), identity));
        Assert.Equal(0, provider.Mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnspecifiedRegionCannotSilentlyRetainMultipleRegionsOrReplicas(bool multipleRegions)
    {
        using Provider provider = new();
        provider.CreateService(marked: true);
        provider.SetExistingRegions(multipleRegions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PreflightAsync(provider, new(), []));
        Assert.Equal(0, provider.Mutations);
    }

    [Fact]
    public async Task LiveReplicaDriftCannotHideBehindMissingDeploymentMetadata()
    {
        using Provider provider = new();
        provider.CreateService(marked: true);
        provider.SetLiveReplicaCount(2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PreflightAsync(provider, new(), []));
        Assert.Equal(0, provider.Mutations);
    }

    [Theory]
    [InlineData("projectId")]
    [InlineData("environmentId")]
    [InlineData("siteKey")]
    [InlineData("serviceName")]
    public async Task CachedScopeDriftCannotMutate(string field)
    {
        using Provider provider = new();
        JsonObject identity = Identity();
        identity[field] = "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => PreflightAsync(provider, new(), identity));
        Assert.Equal(0, provider.Mutations);
    }

    [Fact]
    public async Task LaterResourceOwnershipFailureIsReadOnlyAcrossTheTarget()
    {
        using Provider provider = new();
        await PreflightAsync(provider, new(), []);
        provider.CreateService(marked: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PreflightAsync(provider, new(), []));
        Assert.Equal(0, provider.Mutations);
        Assert.Equal(0, provider.Creates);
    }

    [Fact]
    public async Task VolumeDriftFailsBeforeAnyPatchOrCreation()
    {
        using Provider provider = new();
        provider.CreateService(marked: true);
        provider.Volumes.Add(new JsonObject { ["node"] = new JsonObject { ["volumeId"] = "volume", ["serviceId"] = "service", ["mountPath"] = "/different", ["region"] = "ams" } });
        RailwayServiceOptions options = new();
        options.Volumes.Add(new() { MountPath = "/data" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => PreflightAsync(provider, options, Identity()));
        Assert.Equal(0, provider.Mutations);
    }

    [Fact]
    public async Task PublicExposureDriftFailsBeforeVolumeCreation()
    {
        using Provider provider = new();
        provider.CreateService(marked: true);
        provider.Domains.Add(new JsonObject { ["id"] = "domain", ["domain"] = "site.example", ["targetPort"] = 80 });
        RailwayServiceOptions options = new();
        options.Volumes.Add(new() { MountPath = "/data" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(provider, options, Identity()));
        Assert.Equal(0, provider.Mutations);
    }

    [Fact]
    public async Task ExistingTcpExposureCannotBeSilentlyAdopted()
    {
        using Provider provider = new();
        provider.CreateService(marked: true);
        provider.TcpProxies.Add(new JsonObject { ["id"] = "tcp-proxy" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => PreflightAsync(provider, new(), Identity()));
        Assert.Equal(0, provider.Mutations);
    }

    [Fact]
    public async Task NeverRestartOmitsProviderInvalidZeroRetryConfiguration()
    {
        using Provider provider = new();
        await ApplyAsync(provider, new() { RestartPolicy = RailwayRestartPolicy.Never, RestartPolicyMaxRetries = 0 }, []);
        Assert.Null(provider.LastPatch?["deploy"]?["restartPolicyMaxRetries"]);
    }

    [Theory]
    [InlineData("SUCCESS", "RUNNING")]
    [InlineData("FAILED", "EXITED")]
    [InlineData("CRASHED", "CRASHED")]
    [InlineData("SUCCESS", "STOPPED")]
    [InlineData("DEPLOYING", "EXITED")]
    public async Task FiniteJobCannotPassWithoutSuccessfulProcessTermination(string status, string instance)
    {
        using Provider provider = new() { Status = status, InstanceStatus = instance };
        RailwayServiceOptions options = new() { RestartPolicy = RailwayRestartPolicy.Never, WaitForCompletion = true, DeploymentTimeout = TimeSpan.FromMilliseconds(1) };
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => ApplyAsync(provider, options, []));
        Assert.True(error is InvalidOperationException or TimeoutException);
    }

    [Fact]
    public async Task FiniteJobPassesOnlyStoppedExitedInstance()
    {
        using Provider provider = new() { Status = "SUCCESS", InstanceStatus = "EXITED", Stopped = true };
        RailwayServiceOptions options = new() { RestartPolicy = RailwayRestartPolicy.Never, WaitForCompletion = true };
        RailwayServiceResult result = await ApplyAsync(provider, options, []);
        Assert.Equal("deployment", result.DeploymentId);
    }

    [Fact]
    public async Task SleepingOrdinaryServiceCanBeReconciledWithoutRedeployment()
    {
        using Provider provider = new() { Status = "SLEEPING" };
        JsonObject identity = [];
        RailwayServiceOptions options = new() { SleepApplication = true };
        await ApplyAsync(provider, options, identity);
        int mutations = provider.Mutations;
        RailwayServiceResult repeat = await ApplyAsync(provider, options, identity);
        Assert.False(repeat.Deployed);
        Assert.Equal(mutations, provider.Mutations);
    }

    [Fact]
    public async Task ActivatedCronPublicationDoesNotWaitForTheNextScheduledProcess()
    {
        using Provider provider = new() { Status = "SUCCESS", InstanceStatus = "CREATED", Stopped = false };
        RailwayServiceOptions options = new() { CronSchedule = "0 0 * * *", RestartPolicy = RailwayRestartPolicy.Never };
        RailwayServiceResult result = await ApplyAsync(provider, options, []);
        Assert.Equal("deployment", result.DeploymentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SleepingCannotCompleteAnUnconfiguredOrFiniteService(bool finite)
    {
        using Provider provider = new() { Status = "SLEEPING", InstanceStatus = "EXITED", Stopped = true };
        RailwayServiceOptions options = new()
        {
            SleepApplication = finite,
            WaitForCompletion = finite,
            RestartPolicy = RailwayRestartPolicy.Never,
            DeploymentTimeout = TimeSpan.FromMilliseconds(1),
        };
        await Assert.ThrowsAsync<TimeoutException>(() => ApplyAsync(provider, options, []));
    }

    [Fact]
    public async Task ProviderErrorsNeverExposeCredentials()
    {
        using Provider provider = new() { FailNextPatch = true };
        InvalidOperationException error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ApplyAsync(provider, new(), []));
        Assert.DoesNotContain("provider-secret", error.ToString(), StringComparison.Ordinal);
    }

    private static JsonObject Identity() => new() { ["projectId"] = "project", ["environmentId"] = "environment", ["siteKey"] = "site", ["serviceName"] = "web", ["serviceId"] = "service" };

    private static RailwayResolvedTarget Target() => new("project", "environment", "site", new());

    private static Task PreflightAsync(Provider provider, RailwayServiceOptions options, JsonObject identity)
    {
        HttpClient httpClient = new(provider, disposeHandler: false);
        RailwayServiceReconciler reconciler = new(new RailwayManagementClient(httpClient, "secret-token", RailwayAuthenticationMode.ProjectToken));
        return reconciler.PreflightAsync(Target(), "web", "web", Image, options, identity, TestContext.Current.CancellationToken);
    }

    private static Task<RailwayServiceResult> ApplyAsync(Provider provider, RailwayServiceOptions options, JsonObject identity, string image = Image, Func<Task>? saveIdentity = null, Dictionary<string, string>? variables = null)
    {
        HttpClient httpClient = new(provider, disposeHandler: false);
        RailwayServiceReconciler reconciler = new(new RailwayManagementClient(httpClient, "secret-token", RailwayAuthenticationMode.ProjectToken));
        return reconciler.ApplyAsync(Target(), "web", "web", image, options, variables ?? new(StringComparer.Ordinal), identity, saveIdentity ?? (() => Task.CompletedTask), TestContext.Current.CancellationToken);
    }

    private sealed class Provider : HttpMessageHandler
    {
        internal string TokenProject { get; set; } = "project";
        internal string TokenEnvironment { get; set; } = "environment";
        internal string Site { get; set; } = "site";
        internal int Mutations { get; private set; }
        internal int Creates { get; private set; }
        internal bool FailNextPatch { get; set; }
        internal bool FailNextDeploy { get; set; }
        internal bool FailCreateResponse { get; set; }
        internal int DeployRequests { get; private set; }
        internal int InitialMissingResponses { get; set; }
        internal bool AmbiguousInitialDeployments { get; set; }
        internal bool LoseNextDeployResponse { get; set; }
        internal bool HideDeploymentIds { get; set; }
        internal bool FailNextOutputRead { get; set; }
        internal string? ForcedDeploymentImage { get; set; }
        internal string? ReportedImage { get; set; }
        internal bool OmitReportedImage { get; set; }
        internal bool WrongRequestMarker { get; set; }
        internal bool ReturnCompositePatchReference { get; set; }
        internal string? MissingCorrelationField { get; set; }
        internal int MissingCorrelationResponses { get; set; }
        internal string? WrongCorrelationField { get; set; }
        internal Action? CorrelationRead { get; set; }
        internal List<string> CorrelationReadIds { get; } = [];
        internal string? DeployedImage { get; private set; }
        internal bool LastDeployWasFromSource { get; private set; }
        private readonly JsonArray _deploymentIds = [];
        private readonly Dictionary<string, string?> _deploymentImages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _deploymentMarkers = new(StringComparer.Ordinal);
        private JsonObject _deploySettings = [];
        private JsonNode? _lastCommittedPatch;
        internal List<string> AcceptedRequestMarkers { get; } = [];
        internal JsonNode? LastPatch { get; private set; }
        internal JsonObject Regions { get; private set; } = new() { ["sfo"] = new JsonObject { ["numReplicas"] = 1 } };
        internal string Status { get; set; } = "SUCCESS";
        internal string InstanceStatus { get; set; } = "RUNNING";
        internal bool Stopped { get; set; }
        internal JsonArray Volumes { get; } = [];
        internal JsonArray Domains { get; } = [];
        internal JsonArray TcpProxies { get; } = [];
        private JsonObject? _service;
        private readonly JsonObject _variables = [];
        private readonly HashSet<string> _sealedNames = new(StringComparer.Ordinal);

        internal bool ContainsVariable(string name) => _variables.ContainsKey(name) || _sealedNames.Contains(name);

        internal void SetUnmanagedVariable(string name, string value) => _variables[name] = value;

        internal bool HasVariable(string name) => _variables.ContainsKey(name);

        internal void SetConfiguredImage(string image) => _service!["source"] = new JsonObject { ["image"] = image };

        internal void CreateOperatorDeployment()
        {
            using HttpResponseMessage response = Deploy("environmentPatchCommit", fromSource: true);
        }

        internal void CreateService(bool marked)
        {
            _service = new() { ["serviceId"] = "service", ["serviceName"] = "web", ["latestDeployment"] = null };
            if (marked)
            {
                _variables["PINGUAPPS_SITE_KEY"] = "site";
                _variables["PINGUAPPS_RESOURCE_NAME"] = "web";
            }
        }

        internal void SetExistingRegions(bool multipleRegions)
        {
            JsonObject regions = new() { ["sfo"] = new JsonObject { ["numReplicas"] = multipleRegions ? 1 : 2 } };
            if (multipleRegions)
            { regions["ams"] = new JsonObject { ["numReplicas"] = 1 }; }
            _service!["latestDeployment"] = new JsonObject
            {
                ["id"] = "existing-deployment",
                ["meta"] = new JsonObject { ["serviceManifest"] = new JsonObject { ["deploy"] = new JsonObject { ["multiRegionConfig"] = regions } } },
            };
        }

        internal void SetLiveReplicaCount(int replicas)
        {
            _service!["numReplicas"] = replicas;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            JsonObject payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            string query = (string)payload["query"]!;
            JsonNode args = payload["variables"]!;
            Assert.Equal("secret-token", Assert.Single(request.Headers.GetValues("Project-Access-Token")));
            JsonObject data = [];
            if (query.StartsWith("mutation", StringComparison.Ordinal))
            { Mutations++; }
            if (query.Contains("projectToken", StringComparison.Ordinal))
            {
                data["projectToken"] = new JsonObject { ["projectId"] = TokenProject, ["environmentId"] = TokenEnvironment };
            }
            else if (query.Contains("environment(id:$id){projectId}", StringComparison.Ordinal))
            {
                data["environment"] = new JsonObject { ["projectId"] = "project" };
            }
            else if (query.Contains("serviceInstances", StringComparison.Ordinal))
            {
                data["environment"] = new JsonObject
                {
                    ["serviceInstances"] = new JsonObject { ["edges"] = _service is null ? [] : new JsonArray(new JsonObject { ["node"] = _service.DeepClone() }) },
                    ["volumeInstances"] = new JsonObject { ["edges"] = Volumes.DeepClone() },
                };
            }
            else if (query.Contains("deployments(input:", StringComparison.Ordinal))
            {
                data["deployments"] = new JsonObject { ["edges"] = HideDeploymentIds ? new JsonArray() : _deploymentIds.DeepClone() };
            }
            else if (query.Contains("{variables(", StringComparison.Ordinal))
            {
                JsonObject variables = args["service"] is null ? new() { ["PINGUAPPS_SITE_KEY"] = Site } : (JsonObject)_variables.DeepClone();
                if (!query.Contains("unrendered:true", StringComparison.Ordinal))
                {
                    if (FailNextOutputRead)
                    {
                        FailNextOutputRead = false;
                        throw new HttpRequestException("Simulated output read failure.");
                    }
                    variables["RAILWAY_PRIVATE_DOMAIN"] = "web.railway.internal";
                }
                data["variables"] = variables;
            }
            else if (query.Contains("serviceCreate", StringComparison.Ordinal))
            {
                CreateService(marked: true);
                Creates++;
                _variables["PINGUAPPS_CLAIM"] = args["input"]!["variables"]!["PINGUAPPS_CLAIM"]!.DeepClone();
                if (FailCreateResponse)
                {
                    FailCreateResponse = false;
                    return Response(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = "ambiguous response" }) });
                }
                data["serviceCreate"] = new JsonObject { ["id"] = "service" };
            }
            else if (query.Contains("{domains(", StringComparison.Ordinal))
            {
                data["domains"] = new JsonObject { ["serviceDomains"] = Domains.DeepClone(), ["customDomains"] = new JsonArray() };
                data["tcpProxies"] = TcpProxies.DeepClone();
            }
            else if (query.Contains("environmentPatchCommit", StringComparison.Ordinal))
            {
                if (FailNextPatch)
                {
                    FailNextPatch = false;
                    return Response(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = "provider-secret" }) });
                }

                JsonNode patch = args["patch"]!["services"]!["service"]!;
                bool changed = !JsonNode.DeepEquals(_lastCommittedPatch, patch);
                LastPatch = patch.DeepClone();
                if (patch["source"] is not null)
                { _service!["source"] = patch["source"]!.DeepClone(); }
                if (patch["deploy"] is not null)
                {
                    _deploySettings = patch["deploy"]!.DeepClone().AsObject();
                    _deploySettings["multiRegionConfig"] = Regions.DeepClone();
                    foreach (KeyValuePair<string, JsonNode?> setting in _deploySettings)
                    { _service![setting.Key] = setting.Value?.DeepClone(); }
                }

                if (patch["variables"] is JsonObject variables)
                {
                    foreach (KeyValuePair<string, JsonNode?> variable in variables)
                    {
                        if (variable.Value is null)
                        {
                            _variables.Remove(variable.Key);
                            _sealedNames.Remove(variable.Key);
                        }
                        else if ((bool?)variable.Value["isSealed"] == true)
                        {
                            _variables.Remove(variable.Key);
                            _sealedNames.Add(variable.Key);
                        }
                        else
                        {
                            _sealedNames.Remove(variable.Key);
                            _variables[variable.Key] = variable.Value["value"]!.DeepClone();
                        }
                    }
                }

                if (query.Contains("skipDeploys:false", StringComparison.Ordinal) && changed)
                { return Deploy("environmentPatchCommit", fromSource: true); }
                data["environmentPatchCommit"] = "patch";
            }
            else if (query.Contains("serviceInstanceUpdate(", StringComparison.Ordinal))
            {
                Regions = args["input"]!["multiRegionConfig"]!.DeepClone().AsObject();
                data["serviceInstanceUpdate"] = true;
            }
            else if (query.Contains("serviceInstanceDeploy", StringComparison.Ordinal))
            {
                return Deploy(query.Contains("serviceInstanceDeployV2", StringComparison.Ordinal) ? "serviceInstanceDeployV2" : "serviceInstanceDeploy", query.Contains("latestCommit:true", StringComparison.Ordinal));
            }
            else
            {
                string? reportedImage = ReportedImage ?? _deploymentImages.GetValueOrDefault((string)args["id"]!);
                if (OmitReportedImage)
                { reportedImage = null; }
                data["deployment"] = query.Contains("deployment(id:", StringComparison.Ordinal)
                    ? (JsonNode)new JsonObject { ["projectId"] = "project", ["environmentId"] = "environment", ["serviceId"] = "service", ["status"] = Status, ["meta"] = new JsonObject { ["image"] = reportedImage, ["patchId"] = $"patch-{args["id"]}" }, ["deploymentStopped"] = Stopped, ["instances"] = new JsonArray(new JsonObject { ["id"] = "instance", ["status"] = InstanceStatus }) }
                    : throw new InvalidOperationException($"Unexpected test operation: {query}");
                if (query.Contains("deploymentSnapshot", StringComparison.Ordinal))
                {
                    CorrelationReadIds.Add((string)args["id"]!);
                    string marker = _deploymentMarkers.GetValueOrDefault((string)args["id"]!) ?? string.Empty;
                    if (WrongRequestMarker)
                    { marker = "unrelated-operator-request"; }
                    data["deploymentSnapshot"] = new JsonObject { ["variables"] = new JsonObject { ["PINGUAPPS_DEPLOYMENT_REQUEST"] = marker } };
                    if (MissingCorrelationResponses > 0)
                    {
                        MissingCorrelationResponses--;
                        switch (MissingCorrelationField)
                        {
                            case "deployment":
                                data["deployment"] = null;
                                break;
                            case "project":
                                data["deployment"]!["projectId"] = null;
                                break;
                            case "environment":
                                data["deployment"]!["environmentId"] = null;
                                break;
                            case "service":
                                data["deployment"]!["serviceId"] = null;
                                break;
                            case "meta":
                                data["deployment"]!["meta"] = null;
                                break;
                            case "patch":
                                data["deployment"]!["meta"]!["patchId"] = null;
                                break;
                            case "snapshot":
                                data["deploymentSnapshot"] = null;
                                break;
                            case "variables":
                                data["deploymentSnapshot"]!["variables"] = null;
                                break;
                            case "marker":
                                data["deploymentSnapshot"]!["variables"]!["PINGUAPPS_DEPLOYMENT_REQUEST"] = null;
                                break;
                        }
                    }

                    switch (WrongCorrelationField)
                    {
                        case "project":
                            data["deployment"]!["projectId"] = "unrelated-project";
                            break;
                        case "environment":
                            data["deployment"]!["environmentId"] = "unrelated-environment";
                            break;
                        case "service":
                            data["deployment"]!["serviceId"] = "unrelated-service";
                            break;
                        case "patch":
                            data["deployment"]!["meta"]!["patchId"] = "unrelated-patch";
                            break;
                        case "marker":
                            data["deploymentSnapshot"]!["variables"]!["PINGUAPPS_DEPLOYMENT_REQUEST"] = "unrelated-request";
                            break;
                    }

                    CorrelationRead?.Invoke();
                }
            }

            return Response(new JsonObject { ["data"] = data });
        }

        private HttpResponseMessage Deploy(string operation, bool fromSource)
        {
            DeployRequests++;
            if (InitialMissingResponses > 0)
            {
                InitialMissingResponses--;
                return Response(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = "Deployment not found" }) });
            }
            if (FailNextDeploy)
            {
                FailNextDeploy = false;
                return Response(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = "provider-secret" }) });
            }
            LastDeployWasFromSource = fromSource;
            _lastCommittedPatch = LastPatch?.DeepClone();
            AcceptedRequestMarkers.Add((string?)LastPatch?["variables"]?["PINGUAPPS_DEPLOYMENT_REQUEST"]?["value"] ?? string.Empty);
            string id = _deploymentIds.Count == 0 ? "deployment" : $"deployment-{_deploymentIds.Count + 1}";
            _deploymentIds.Add(new JsonObject { ["node"] = new JsonObject { ["id"] = id } });
            DeployedImage = ForcedDeploymentImage ?? (fromSource ? (string?)_service!["source"]?["image"] : DeployedImage ?? (string?)_service!["source"]?["image"]);
            _deploymentImages[id] = DeployedImage;
            _deploymentMarkers[id] = AcceptedRequestMarkers[^1];
            _service!["latestDeployment"] = new JsonObject { ["id"] = id, ["meta"] = new JsonObject { ["image"] = DeployedImage, ["serviceManifest"] = new JsonObject { ["deploy"] = _deploySettings.DeepClone() } } };
            if (AmbiguousInitialDeployments)
            { _deploymentIds.Add(new JsonObject { ["node"] = new JsonObject { ["id"] = "operator-deployment" } }); }
            if (LoseNextDeployResponse)
            {
                LoseNextDeployResponse = false;
                throw new HttpRequestException("Simulated lost accepted response.");
            }
            if (operation == "environmentPatchCommit")
            {
                string patchReference = ReturnCompositePatchReference ? $"commitChanges/environment/patch-{id}" : $"patch-{id}";
                return Response(new JsonObject { ["data"] = new JsonObject { [operation] = patchReference } });
            }
            if (operation == "serviceInstanceDeployV2")
            { return Response(new JsonObject { ["data"] = new JsonObject { [operation] = id } }); }
            return Response(new JsonObject { ["data"] = new JsonObject { [operation] = true } });
        }

        private static HttpResponseMessage Response(JsonObject body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
    }
}
