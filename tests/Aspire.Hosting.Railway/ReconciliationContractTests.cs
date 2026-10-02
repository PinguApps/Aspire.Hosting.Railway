using System.Net;
using System.Text.Json.Nodes;
using Aspire.Hosting.Railway.Deployment;
using Aspire.Hosting.Railway.Management;
using Xunit;

namespace Aspire.Hosting.Railway.Tests;

public sealed class ReconciliationContractTests
{
    private const string Image = "ghcr.io/pinguapps/test@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

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
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(provider, new(), identity));
        Assert.Equal("service", (string?)identity["serviceId"]);
        await ApplyAsync(provider, new(), identity);
        Assert.Equal(1, provider.Creates);
    }

    [Fact]
    public async Task FailureAfterConfigurationCommitStillDeploysOnRetry()
    {
        using Provider provider = new() { FailNextDeploy = true };
        JsonObject identity = [];
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(provider, new(), identity));
        Assert.True((bool)identity["pending"]!);
        RailwayServiceResult retry = await ApplyAsync(provider, new(), identity);
        Assert.True(retry.Deployed);
        Assert.False((bool)identity["pending"]!);
        Assert.Equal(1, provider.Creates);
        Assert.Equal(2, provider.DeployRequests);
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
    public async Task MissingCachedIdentityCannotFallBackToName()
    {
        using Provider provider = new();
        JsonObject identity = Identity();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PreflightAsync(provider, new(), identity));
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
    public async Task ProviderErrorsNeverExposeCredentials()
    {
        using Provider provider = new() { FailNextPatch = true };
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(provider, new(), []));
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

    private static Task<RailwayServiceResult> ApplyAsync(Provider provider, RailwayServiceOptions options, JsonObject identity)
    {
        HttpClient httpClient = new(provider, disposeHandler: false);
        RailwayServiceReconciler reconciler = new(new RailwayManagementClient(httpClient, "secret-token", RailwayAuthenticationMode.ProjectToken));
        return reconciler.ApplyAsync(Target(), "web", "web", Image, options, new(StringComparer.Ordinal), identity, () => Task.CompletedTask, TestContext.Current.CancellationToken);
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

        internal void CreateService(bool marked)
        {
            _service = new() { ["serviceId"] = "service", ["serviceName"] = "web", ["latestDeployment"] = null };
            if (marked)
            {
                _variables["PINGUAPPS_SITE_KEY"] = "site";
                _variables["PINGUAPPS_RESOURCE_NAME"] = "web";
            }
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
            else if (query.Contains("{variables(", StringComparison.Ordinal))
            {
                JsonObject variables = args["service"] is null ? new() { ["PINGUAPPS_SITE_KEY"] = Site } : (JsonObject)_variables.DeepClone();
                if (!query.Contains("unrendered:true", StringComparison.Ordinal))
                { variables["RAILWAY_PRIVATE_DOMAIN"] = "web.railway.internal"; }
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
                LastPatch = patch.DeepClone();
                if (patch["source"] is not null)
                { _service!["source"] = patch["source"]!.DeepClone(); }
                if (patch["deploy"] is not null)
                {
                    _service!["latestDeployment"] = new JsonObject { ["id"] = "deployment", ["meta"] = new JsonObject { ["serviceManifest"] = new JsonObject { ["deploy"] = patch["deploy"]!.DeepClone() } } };
                    _service["latestDeployment"]!["meta"]!["serviceManifest"]!["deploy"]!["multiRegionConfig"] = Regions.DeepClone();
                }

                if (patch["variables"] is JsonObject variables)
                {
                    foreach (KeyValuePair<string, JsonNode?> variable in variables)
                    {
                        if ((bool?)variable.Value!["isSealed"] != true)
                        { _variables[variable.Key] = variable.Value!["value"]!.DeepClone(); }
                    }
                }

                data["environmentPatchCommit"] = "patch";
            }
            else if (query.Contains("serviceInstanceUpdate(", StringComparison.Ordinal))
            {
                Regions = args["input"]!["multiRegionConfig"]!.DeepClone().AsObject();
                data["serviceInstanceUpdate"] = true;
            }
            else if (query.Contains("serviceInstanceDeploy", StringComparison.Ordinal))
            {
                DeployRequests++;
                if (FailNextDeploy)
                {
                    FailNextDeploy = false;
                    return Response(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = "provider-secret" }) });
                }
                if (query.Contains("serviceInstanceDeployV2", StringComparison.Ordinal))
                { data["serviceInstanceDeployV2"] = "deployment"; }
                else
                { data["serviceInstanceDeploy"] = true; }
            }
            else
            {
                data["deployment"] = query.Contains("deployment(id:", StringComparison.Ordinal)
                    ? (JsonNode)new JsonObject { ["projectId"] = "project", ["environmentId"] = "environment", ["serviceId"] = "service", ["status"] = Status, ["deploymentStopped"] = Stopped, ["instances"] = new JsonArray(new JsonObject { ["id"] = "instance", ["status"] = InstanceStatus }) }
                    : throw new InvalidOperationException($"Unexpected test operation: {query}");
            }

            return Response(new JsonObject { ["data"] = data });
        }

        private static HttpResponseMessage Response(JsonObject body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
    }
}
