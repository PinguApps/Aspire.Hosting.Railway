using System.Diagnostics;
using System.Text.Json.Nodes;
using Aspire.Hosting.Railway.Management;

namespace Aspire.Hosting.Railway.Deployment;

internal sealed class RailwayServiceReconciler
{
    private readonly RailwayManagementClient _client;

    internal RailwayServiceReconciler(RailwayManagementClient client)
    {
        _client = client;
    }

    internal async Task<string> PreflightAsync(RailwayResolvedTarget target, string resourceName, string serviceName, string image, RailwayServiceOptions options, JsonObject identity, CancellationToken cancellationToken)
    {
        await _client.ValidateScopeAsync(target, cancellationToken).ConfigureAwait(false);
        ValidateCachedScope(identity, target, serviceName);
        JsonObject environment = await ReadEnvironmentAsync(target, cancellationToken).ConfigureAwait(false);
        JsonObject? instance = FindService(environment, serviceName, (string?)identity["serviceId"] ?? options.ExistingServiceId);
        if (instance is null)
        {
            if (identity["serviceId"] is not null || options.ExistingServiceId is not null || options.OwnershipMode == RailwayOwnershipMode.ExistingOnly)
            {
                throw new InvalidOperationException("The expected existing Railway service is missing. No infrastructure will be changed.");
            }

            return "Create a site-owned immutable container service.";
        }

        string serviceId = (string)instance["serviceId"]!;
        JsonObject variables = await _client.ReadVariablesAsync(target, serviceId, cancellationToken).ConfigureAwait(false);
        ValidateOwnership(variables, target.SiteKey, resourceName, serviceId, options, identity);
        ValidateUnspecifiedRegion(instance, options);
        ValidateVolumeDrift(environment, serviceId, options, identity);
        JsonObject domains = await ReadDomainsAsync(target, serviceId, cancellationToken).ConfigureAwait(false);
        ValidateDomainDrift(domains, options);
        return SettingsMatch(instance, DesiredSettings(image, options))
            ? "Reuse service settings; reconcile runtime bindings and readiness."
            : "Update the existing site's service configuration and retained image.";
    }

    internal async Task<RailwayServiceResult> ApplyAsync(
        RailwayResolvedTarget target,
        string resourceName,
        string serviceName,
        string image,
        RailwayServiceOptions options,
        Dictionary<string, string> variables,
        JsonObject identity,
        Func<Task> saveIdentity,
        CancellationToken cancellationToken,
        JsonObject? registryCredentials = null,
        string? registryFingerprint = null,
        IReadOnlyDictionary<string, string>? sealedFingerprints = null)
    {
        Stopwatch overall = Stopwatch.StartNew();
        await _client.ValidateScopeAsync(target, cancellationToken).ConfigureAwait(false);
        ValidateCachedScope(identity, target, serviceName);
        JsonObject environment = await ReadEnvironmentAsync(target, cancellationToken).ConfigureAwait(false);
        JsonObject? instance = FindService(environment, serviceName, (string?)identity["serviceId"] ?? options.ExistingServiceId);
        bool created = false;
        variables["PINGUAPPS_SITE_KEY"] = target.SiteKey;
        variables["PINGUAPPS_RESOURCE_NAME"] = resourceName;
        if (options.Port is int port)
        {
            variables["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        string desiredFingerprint = GetDesiredFingerprint(image, options, variables, registryFingerprint);
        if (options.WaitForCompletion && (bool?)identity["pending"] == true
            && identity["deploymentAttempt"] is null && identity["applyPhase"] is null)
        {
            throw new InvalidOperationException("Legacy pending finite deployment state has no recorded request identity. Reconcile the remote execution before requesting another process.");
        }
        if (identity["deploymentAttempt"] is JsonObject priorAttempt && (string?)priorAttempt["desiredFingerprint"] != desiredFingerprint)
        {
            if (options.WaitForCompletion)
            {
                throw new InvalidOperationException("A pending finite Railway deployment has different desired configuration or credentials. Reconcile its recorded execution before requesting another process.");
            }

            identity.Remove("deploymentAttempt");
            await saveIdentity().ConfigureAwait(false);
        }

        string serviceId;
        if (instance is null)
        {
            if (identity["serviceId"] is not null || options.ExistingServiceId is not null)
            {
                throw new InvalidOperationException("The recorded Railway service identity is missing. Refusing to recreate it by name.");
            }

            if (options.OwnershipMode == RailwayOwnershipMode.ExistingOnly)
            {
                throw new InvalidOperationException("ExistingOnly requires an existing Railway service.");
            }

            string claim = (string?)identity["claim"] ?? Guid.NewGuid().ToString("N");
            identity["projectId"] = target.ProjectId;
            identity["environmentId"] = target.EnvironmentId;
            identity["siteKey"] = target.SiteKey;
            identity["serviceName"] = serviceName;
            identity["claim"] = claim;
            await saveIdentity().ConfigureAwait(false);

            JsonObject create = await _client.SendAsync(
                "mutation($input:ServiceCreateInput!){serviceCreate(input:$input){id}}",
                new { input = new { projectId = target.ProjectId, environmentId = target.EnvironmentId, name = serviceName, variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["PINGUAPPS_SITE_KEY"] = target.SiteKey, ["PINGUAPPS_RESOURCE_NAME"] = resourceName, ["PINGUAPPS_CLAIM"] = claim } } }, cancellationToken).ConfigureAwait(false);
            serviceId = (string)create["serviceCreate"]!["id"]!;
            created = true;
        }
        else
        {
            serviceId = (string)instance["serviceId"]!;
            JsonObject existingVariables = await _client.ReadVariablesAsync(target, serviceId, cancellationToken).ConfigureAwait(false);
            ValidateOwnership(existingVariables, target.SiteKey, resourceName, serviceId, options, identity);
        }

        identity["projectId"] = target.ProjectId;
        identity["environmentId"] = target.EnvironmentId;
        identity["siteKey"] = target.SiteKey;
        identity["serviceName"] = serviceName;
        identity["serviceId"] = serviceId;
        await saveIdentity().ConfigureAwait(false);

        environment = await ReadEnvironmentAsync(target, cancellationToken).ConfigureAwait(false);
        instance = FindService(environment, serviceName, serviceId)
            ?? throw new InvalidOperationException("Created Railway service is not visible in the expected environment.");
        JsonObject currentVariables = await _client.ReadVariablesAsync(target, serviceId, cancellationToken).ConfigureAwait(false);
        ValidateUnspecifiedRegion(instance, options);
        ValidateVolumeDrift(environment, serviceId, options, identity);
        JsonObject plannedDomains = await ReadDomainsAsync(target, serviceId, cancellationToken).ConfigureAwait(false);
        ValidateDomainDrift(plannedDomains, options);
        bool pending = (bool?)identity["pending"] == true;
        identity["pending"] = true;
        identity["applyPhase"] = "configuration";
        await saveIdentity().ConfigureAwait(false);
        JsonObject settings = DesiredSettings(image, options);
        bool settingsChanged = !SettingsMatch(instance, settings)
            || (string?)identity["registryFingerprint"] != registryFingerprint;
        string[] removedVariables = identity["managedVariables"] is JsonArray managedVariables
            ? [.. managedVariables.Select(name => (string)name!).Where(name => !variables.ContainsKey(name))] : [];
        bool variablesChanged = removedVariables.Length != 0 || variables.Any(pair => options.SealedVariables.Contains(pair.Key, StringComparer.Ordinal)
            ? (string?)identity["sealedFingerprints"]?[pair.Key] != sealedFingerprints?[pair.Key]
            : (string?)currentVariables[pair.Key] != pair.Value);
        bool volumesChanged = await EnsureVolumesAsync(target, environment, serviceId, options, identity, saveIdentity, cancellationToken).ConfigureAwait(false);
        bool domainsChanged = await EnsureDomainsAsync(target, serviceId, options, cancellationToken).ConfigureAwait(false);
        bool limitsChanged = await EnsureLimitsAsync(target, serviceId, options, cancellationToken).ConfigureAwait(false);
        if (variablesChanged || settingsChanged)
        {
            JsonObject patchService = [];
            if (settingsChanged)
            {
                JsonNode? source = settings["source"];
                settings.Remove("source");
                patchService["source"] = source;
                settings["registryCredentials"] = registryCredentials?.DeepClone();

                if (settings["multiRegionConfig"] is JsonObject desiredRegions)
                {
                    await _client.SendAsync("mutation($input:ServiceInstanceUpdateInput!,$service:String!,$environment:String!){serviceInstanceUpdate(input:$input,serviceId:$service,environmentId:$environment)}",
                        new { input = new { multiRegionConfig = desiredRegions, numReplicas = 1 }, service = serviceId, environment = target.EnvironmentId }, cancellationToken).ConfigureAwait(false);
                    settings.Remove("multiRegionConfig");
                }

                patchService["deploy"] = settings;
            }

            if (variablesChanged)
            {
                JsonObject patchVariables = [];
                foreach (KeyValuePair<string, string> variable in variables)
                {
                    patchVariables[variable.Key] = new JsonObject { ["value"] = variable.Value, ["isSealed"] = options.SealedVariables.Contains(variable.Key, StringComparer.Ordinal) };
                }

                foreach (string name in removedVariables)
                {
                    patchVariables[name] = null;
                }

                patchService["variables"] = patchVariables;
            }

            JsonObject patch = new() { ["services"] = new JsonObject { [serviceId] = patchService } };
            await _client.SendAsync("mutation($environment:String!,$patch:EnvironmentConfig!){environmentPatchCommit(environmentId:$environment,patch:$patch,skipDeploys:true)}",
                new { environment = target.EnvironmentId, patch }, cancellationToken).ConfigureAwait(false);
            identity["registryFingerprint"] = registryFingerprint;
            identity["managedVariables"] = new JsonArray([.. variables.Keys.Select(name => JsonValue.Create(name))]);
            if (sealedFingerprints is not null)
            {
                JsonObject fingerprints = [];
                foreach (KeyValuePair<string, string> pair in sealedFingerprints)
                {
                    fingerprints[pair.Key] = pair.Value;
                }

                identity["sealedFingerprints"] = fingerprints;
            }

            await saveIdentity().ConfigureAwait(false);
        }

        string? deploymentId = (string?)instance["latestDeployment"]?["id"];
        bool deploy = created || settingsChanged || variablesChanged || volumesChanged || domainsChanged || limitsChanged
            || deploymentId is null || options.WaitForCompletion || pending;
        if (deploy)
        {
            deploymentId = await LaunchOrRecoverAsync(target, serviceId, deploymentId is null, desiredFingerprint, options,
                identity, saveIdentity, overall, cancellationToken).ConfigureAwait(false);
        }

        if (options.CronSchedule is null || deploy)
        {
            try
            {
                await WaitForDeploymentAsync(target, serviceId, deploymentId!, options, options.DeploymentTimeout - overall.Elapsed, cancellationToken).ConfigureAwait(false);
            }
            catch (RailwayDeploymentFailedException) when (options.WaitForCompletion)
            {
                identity["pending"] = false;
                identity.Remove("deploymentAttempt");
                identity.Remove("applyPhase");
                await saveIdentity().ConfigureAwait(false);
                throw;
            }
        }

        JsonObject rendered = await _client.SendAsync("query($project:String!,$environment:String!,$service:String){variables(projectId:$project,environmentId:$environment,serviceId:$service)}",
            new { project = target.ProjectId, environment = target.EnvironmentId, service = serviceId }, cancellationToken).ConfigureAwait(false);
        string hostname = (string?)rendered["variables"]?["RAILWAY_PRIVATE_DOMAIN"]
            ?? throw new InvalidOperationException("Railway did not return a private service hostname.");
        JsonObject domains = await ReadDomainsAsync(target, serviceId, cancellationToken).ConfigureAwait(false);
        string? publicDomain = (string?)domains["serviceDomains"]?.AsArray().FirstOrDefault()?["domain"];
        identity["pending"] = false;
        identity.Remove("deploymentAttempt");
        identity.Remove("applyPhase");
        await saveIdentity().ConfigureAwait(false);
        return new RailwayServiceResult(serviceId, hostname, publicDomain is null ? null : $"https://{publicDomain}", deploymentId, deploy);
    }

    private string GetDesiredFingerprint(string image, RailwayServiceOptions options, Dictionary<string, string> variables, string? registryFingerprint)
    {
        JsonObject environment = [];
        foreach (KeyValuePair<string, string> variable in variables.OrderBy(variable => variable.Key, StringComparer.Ordinal))
        {
            environment[variable.Key] = variable.Value;
        }

        JsonObject desired = new()
        {
            ["settings"] = DesiredSettings(image, options),
            ["variables"] = environment,
            ["sealed"] = new JsonArray([.. options.SealedVariables.Order(StringComparer.Ordinal).Select(name => JsonValue.Create(name))]),
            ["registry"] = registryFingerprint,
            ["volumes"] = new JsonArray([.. options.Volumes.Select(volume => volume.MountPath).Order(StringComparer.Ordinal).Select(path => JsonValue.Create(path))]),
            ["domains"] = new JsonArray([.. options.CustomDomains.Select(domain => domain.ToLowerInvariant()).Order(StringComparer.Ordinal).Select(domain => JsonValue.Create(domain))]),
            ["publicDomain"] = options.PublicDomain,
            ["memoryGB"] = options.MemoryGB,
            ["vCPUs"] = options.VCpus,
            ["finite"] = options.WaitForCompletion,
        };
        return _client.Fingerprint(desired.ToJsonString());
    }

    private async Task<string> LaunchOrRecoverAsync(RailwayResolvedTarget target, string serviceId, bool initial, string desiredFingerprint,
        RailwayServiceOptions options, JsonObject identity, Func<Task> saveIdentity, Stopwatch overall, CancellationToken cancellationToken)
    {
        JsonObject attempt;
        if (identity["deploymentAttempt"] is JsonObject recorded)
        {
            attempt = recorded;
        }
        else
        {
            HashSet<string> before = await ReadDeploymentIdsAsync(target, serviceId, cancellationToken).ConfigureAwait(false);
            attempt = new JsonObject
            {
                ["desiredFingerprint"] = desiredFingerprint,
                ["baseline"] = new JsonArray([.. before.Order(StringComparer.Ordinal).Select(id => JsonValue.Create(id))]),
                ["initial"] = initial,
                ["sent"] = false,
            };
            identity["deploymentAttempt"] = attempt;
            await saveIdentity().ConfigureAwait(false);
        }

        HashSet<string> baseline = [.. attempt["baseline"]!.AsArray().Select(id => (string)id!)];
        while (overall.Elapsed < options.DeploymentTimeout)
        {
            string? deploymentId = await DiscoverInitialDeploymentAsync(target, serviceId, baseline, (string?)attempt["id"], cancellationToken).ConfigureAwait(false);
            if (deploymentId is not null)
            {
                attempt["id"] = deploymentId;
                await saveIdentity().ConfigureAwait(false);
                return deploymentId;
            }

            if ((bool)attempt["sent"]!)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                continue;
            }

            attempt["sent"] = true;
            await saveIdentity().ConfigureAwait(false);
            try
            {
                JsonObject deployed = await _client.SendAsync("mutation($service:String!,$environment:String!){serviceInstanceDeployV2(serviceId:$service,environmentId:$environment)}",
                    new { service = serviceId, environment = target.EnvironmentId }, cancellationToken).ConfigureAwait(false);
                attempt["id"] = (string)deployed["serviceInstanceDeployV2"]!;
                await saveIdentity().ConfigureAwait(false);
            }
            catch (RailwayDeploymentNotFoundException)
            {
                attempt["sent"] = false;
                await saveIdentity().ConfigureAwait(false);
                if (!(bool)attempt["initial"]!)
                { throw; }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (RailwayDeploymentRejectedException)
            {
                attempt["sent"] = false;
                await saveIdentity().ConfigureAwait(false);
                throw;
            }
        }

        throw new TimeoutException("Railway did not establish an unambiguous deployment within its deadline. Reconcile any uncertain recorded request before retrying; no second request was issued for an uncertain response.");
    }

    private static void ValidateCachedScope(JsonObject identity, RailwayResolvedTarget target, string serviceName)
    {
        if (identity.Count != 0 && ((string?)identity["projectId"] != target.ProjectId
            || (string?)identity["environmentId"] != target.EnvironmentId || (string?)identity["siteKey"] != target.SiteKey
            || (string?)identity["serviceName"] != serviceName))
        {
            throw new InvalidOperationException("Railway cached identity drifted from its site, project, environment or name. Refusing mutation.");
        }
    }

    private static JsonObject? FindService(JsonObject environment, string serviceName, string? expectedId)
    {
        JsonObject[] instances = [.. environment["serviceInstances"]!["edges"]!.AsArray().Select(edge => edge!["node"]!.AsObject())];
        JsonObject[] matches = [.. instances.Where(instance => (string?)instance["serviceName"] == serviceName)];
        if (matches.Length > 1)
        {
            throw new InvalidOperationException("The Railway service name is ambiguous.");
        }

        JsonObject? found = matches.SingleOrDefault();
        if (expectedId is not null && found is not null && (string?)found["serviceId"] != expectedId)
        {
            throw new InvalidOperationException("The named Railway service has a different identity. Refusing adoption.");
        }

        return found;
    }

    private static void ValidateOwnership(JsonObject variables, string siteKey, string resourceName, string serviceId, RailwayServiceOptions options, JsonObject identity)
    {
        string? owner = (string?)variables["PINGUAPPS_SITE_KEY"];
        string? resource = (string?)variables["PINGUAPPS_RESOURCE_NAME"];
        bool marked = owner == siteKey && resource == resourceName;
        bool explicitAdoption = owner is null && resource is null && options.ExistingServiceId == serviceId;
        if (!marked && !explicitAdoption)
        {
            throw new InvalidOperationException("Existing Railway service ownership is unproven or belongs to a different site/resource.");
        }

        bool createdByThisDeployment = identity["claim"] is not null && (string?)variables["PINGUAPPS_CLAIM"] == (string?)identity["claim"];
        if (options.OwnershipMode == RailwayOwnershipMode.CreateOnly && identity["serviceId"] is null && !createdByThisDeployment)
        {
            throw new InvalidOperationException("CreateOnly cannot adopt an existing Railway service.");
        }
    }

    private async Task<string?> DiscoverInitialDeploymentAsync(RailwayResolvedTarget target, string serviceId, HashSet<string> baseline, string? expectedId, CancellationToken cancellationToken)
    {
        HashSet<string> observed = await ReadDeploymentIdsAsync(target, serviceId, cancellationToken).ConfigureAwait(false);
        string[] added = [.. observed.Except(baseline, StringComparer.Ordinal)];
        if (added.Length > 1 || (added.Length == 1 && expectedId is not null && added[0] != expectedId))
        {
            throw new InvalidOperationException("Initial Railway deployment identity is ambiguous. Refusing to follow an unrelated deployment.");
        }

        return expectedId ?? added.SingleOrDefault();
    }

    private async Task<HashSet<string>> ReadDeploymentIdsAsync(RailwayResolvedTarget target, string serviceId, CancellationToken cancellationToken)
    {
        JsonObject data = await _client.SendAsync("query($input:DeploymentListInput!){deployments(input:$input,first:20){edges{node{id}}}}",
            new { input = new { projectId = target.ProjectId, environmentId = target.EnvironmentId, serviceId, includeDeleted = true } }, cancellationToken).ConfigureAwait(false);
        return [.. data["deployments"]!["edges"]!.AsArray().Select(edge => (string)edge!["node"]!["id"]!)];
    }

    private Task<JsonObject> ReadEnvironmentAsync(RailwayResolvedTarget target, CancellationToken cancellationToken) => ReadEnvironmentCoreAsync(target, cancellationToken);

    private async Task<JsonObject> ReadEnvironmentCoreAsync(RailwayResolvedTarget target, CancellationToken cancellationToken)
    {
        JsonObject data = await _client.SendAsync("query($id:String!){environment(id:$id){serviceInstances{edges{node{serviceId serviceName source{image} startCommand restartPolicyType restartPolicyMaxRetries cronSchedule healthcheckPath healthcheckTimeout region sleepApplication numReplicas latestDeployment{id status meta}}}} volumeInstances{edges{node{volumeId serviceId mountPath region}}}}}",
            new { id = target.EnvironmentId }, cancellationToken).ConfigureAwait(false);
        return data["environment"]!.AsObject();
    }

    private static JsonObject DesiredSettings(string image, RailwayServiceOptions options)
    {
        string restart = "ON_FAILURE";
        if (options.RestartPolicy == RailwayRestartPolicy.Never)
        {
            restart = "NEVER";
        }
        else if (options.RestartPolicy == RailwayRestartPolicy.Always)
        {
            restart = "ALWAYS";
        }

        JsonObject settings = new()
        {
            ["source"] = new JsonObject { ["image"] = image },
            ["startCommand"] = options.StartCommand,
            ["restartPolicyType"] = restart,
            ["cronSchedule"] = options.CronSchedule,
            ["healthcheckPath"] = options.HealthCheckPath,
            ["sleepApplication"] = options.SleepApplication,
            ["numReplicas"] = 1,
        };
        if (options.RestartPolicy != RailwayRestartPolicy.Never)
        {
            settings["restartPolicyMaxRetries"] = options.RestartPolicyMaxRetries;
        }
        if (options.Region is not null)
        {
            string region = ResolveRegion(options.Region);
            settings["multiRegionConfig"] = new JsonObject { [region] = new JsonObject { ["numReplicas"] = 1 } };
        }

        if (options.HealthCheckPath is not null)
        {
            settings["healthcheckTimeout"] = (int)options.DeploymentTimeout.TotalSeconds;
        }

        return settings;
    }

    private static void ValidateUnspecifiedRegion(JsonObject instance, RailwayServiceOptions options)
    {
        if (options.Region is null && ((int?)instance["numReplicas"] > 1
            || (instance["latestDeployment"]?["meta"]?["serviceManifest"]?["deploy"]?["multiRegionConfig"] is JsonObject regions
                && (regions.Count > 1 || regions.Any(region => (int?)region.Value?["numReplicas"] > 1)))))
        {
            throw new InvalidOperationException("An existing multi-region or multi-replica service requires an explicit single Railway region before reconciliation.");
        }
    }

    private static bool SettingsMatch(JsonObject actual, JsonObject desired)
    {
        string? desiredImage = (string?)desired["source"]?["image"];
        if (actual["latestDeployment"]?["meta"]?["image"] is JsonNode deployedImage && (string?)deployedImage != desiredImage)
        {
            return false;
        }

        JsonNode? deployed = actual["latestDeployment"]?["meta"]?["serviceManifest"]?["deploy"];
        return desired.All(pair => JsonNode.DeepEquals(pair.Key == "source" ? actual[pair.Key] : deployed?[pair.Key] ?? actual[pair.Key], pair.Value));
    }

    private static void ValidateVolumeDrift(JsonObject environment, string serviceId, RailwayServiceOptions options, JsonObject identity)
    {
        JsonNode[] attached = [.. environment["volumeInstances"]!["edges"]!.AsArray().Select(edge => edge!["node"]!).Where(volume => (string?)volume["serviceId"] == serviceId)];
        if (attached.Any(volume => !options.Volumes.Any(desired => desired.MountPath == (string?)volume["mountPath"])))
        {
            throw new InvalidOperationException("Existing Railway volume mount drift requires operator reconciliation. No volume is deleted or moved.");
        }

        if (options.Region is not null && attached.Any(volume => (string?)volume["region"] != ResolveRegion(options.Region)))
        {
            throw new InvalidOperationException("Railway volume region drift requires an explicit operator migration; persistent data is never moved automatically.");
        }

        if (identity["volumes"] is JsonObject cachedVolumes && cachedVolumes.Any(pair => !attached.Any(volume =>
            (string?)volume["volumeId"] == (string?)pair.Value && (string?)volume["mountPath"] == pair.Key)))
        {
            throw new InvalidOperationException("A recorded Railway volume is missing or attached elsewhere. Refusing replacement.");
        }
    }

    private async Task<bool> EnsureVolumesAsync(RailwayResolvedTarget target, JsonObject environment, string serviceId, RailwayServiceOptions options, JsonObject identity, Func<Task> saveIdentity, CancellationToken cancellationToken)
    {
        bool changed = false;
        JsonObject volumes = identity["volumes"]?.AsObject() ?? [];
        identity["volumes"] = volumes;
        foreach (RailwayVolumeOptions mount in options.Volumes)
        {
            JsonNode? existing = environment["volumeInstances"]!["edges"]!.AsArray().Select(edge => edge!["node"]!)
                .SingleOrDefault(volume => (string?)volume["serviceId"] == serviceId && (string?)volume["mountPath"] == mount.MountPath);
            string volumeId;
            if (existing is null)
            {
                JsonObject created = await _client.SendAsync("mutation($input:VolumeCreateInput!){volumeCreate(input:$input){id}}",
                    new { input = new { projectId = target.ProjectId, environmentId = target.EnvironmentId, serviceId, mountPath = mount.MountPath, region = options.Region is null ? null : ResolveRegion(options.Region) } }, cancellationToken).ConfigureAwait(false);
                volumeId = (string)created["volumeCreate"]!["id"]!;
                changed = true;
            }
            else
            {
                volumeId = (string)existing["volumeId"]!;
            }

            volumes[mount.MountPath] = volumeId;
            await saveIdentity().ConfigureAwait(false);
        }

        return changed;
    }

    private async Task<JsonObject> ReadDomainsAsync(RailwayResolvedTarget target, string serviceId, CancellationToken cancellationToken)
    {
        JsonObject result = await _client.SendAsync("query($project:String!,$environment:String!,$service:String!){domains(projectId:$project,environmentId:$environment,serviceId:$service){serviceDomains{id domain targetPort} customDomains{id domain targetPort}} tcpProxies(environmentId:$environment,serviceId:$service){id}}",
            new { project = target.ProjectId, environment = target.EnvironmentId, service = serviceId }, cancellationToken).ConfigureAwait(false);
        JsonObject domains = result["domains"]!.AsObject();
        domains["tcpProxies"] = result["tcpProxies"]!.DeepClone();
        return domains;
    }

    private async Task<bool> EnsureDomainsAsync(RailwayResolvedTarget target, string serviceId, RailwayServiceOptions options, CancellationToken cancellationToken)
    {
        JsonObject domains = await ReadDomainsAsync(target, serviceId, cancellationToken).ConfigureAwait(false);
        JsonArray serviceDomains = domains["serviceDomains"]!.AsArray();
        ValidateDomainDrift(domains, options);
        bool changed = false;
        if (options.PublicDomain && serviceDomains.Count == 0)
        {
            await _client.SendAsync("mutation($input:ServiceDomainCreateInput!){serviceDomainCreate(input:$input){id}}",
                new { input = new { environmentId = target.EnvironmentId, serviceId, targetPort = options.Port } }, cancellationToken).ConfigureAwait(false);
            changed = true;
        }

        foreach (string domain in options.CustomDomains)
        {
            if (domains["customDomains"]!.AsArray().Any(existing => string.Equals((string?)existing!["domain"], domain, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            await _client.SendAsync("mutation($input:CustomDomainCreateInput!){customDomainCreate(input:$input){id}}",
                new { input = new { projectId = target.ProjectId, environmentId = target.EnvironmentId, serviceId, domain, targetPort = options.Port } }, cancellationToken).ConfigureAwait(false);
            changed = true;
        }

        return changed;
    }

    private static void ValidateDomainDrift(JsonObject domains, RailwayServiceOptions options)
    {
        JsonArray serviceDomains = domains["serviceDomains"]!.AsArray();
        if (domains["tcpProxies"]!.AsArray().Count != 0)
        {
            throw new InvalidOperationException("An existing Railway TCP proxy would retain unsupported public exposure. Reconcile it explicitly before deployment.");
        }
        if ((!options.PublicDomain && serviceDomains.Count != 0) || domains["customDomains"]!.AsArray().Any(domain => !options.CustomDomains.Contains((string)domain!["domain"]!, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Railway public domain drift requires operator reconciliation; no public exposure is silently retained or deleted.");
        }

        if (serviceDomains.Concat(domains["customDomains"]!.AsArray()).Any(domain => (int?)domain!["targetPort"] != options.Port))
        {
            throw new InvalidOperationException("Railway domain target port drift requires operator reconciliation.");
        }

    }

    private async Task<bool> EnsureLimitsAsync(RailwayResolvedTarget target, string serviceId, RailwayServiceOptions options, CancellationToken cancellationToken)
    {
        if (options.MemoryGB is null && options.VCpus is null)
        {
            return false;
        }

        JsonObject limits = await _client.SendAsync("query($service:String!,$environment:String!){serviceInstanceLimits(serviceId:$service,environmentId:$environment)}",
            new { service = serviceId, environment = target.EnvironmentId }, cancellationToken).ConfigureAwait(false);
        JsonNode current = limits["serviceInstanceLimits"]!;
        if ((options.MemoryGB is null || (double?)current["containers"]?["memoryBytes"] == options.MemoryGB * 1_000_000_000)
            && (options.VCpus is null || (double?)current["containers"]?["cpu"] == options.VCpus))
        {
            return false;
        }

        await _client.SendAsync("mutation($input:ServiceInstanceLimitsUpdateInput!){serviceInstanceLimitsUpdate(input:$input)}",
            new { input = new { environmentId = target.EnvironmentId, serviceId, memoryGB = options.MemoryGB, vCPUs = options.VCpus } }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task WaitForDeploymentAsync(RailwayResolvedTarget target, string serviceId, string deploymentId, RailwayServiceOptions options, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            JsonObject data = await _client.SendAsync("query($id:String!){deployment(id:$id){projectId environmentId serviceId status deploymentStopped instances{id status}}}",
                new { id = deploymentId }, cancellationToken).ConfigureAwait(false);
            JsonNode deployment = data["deployment"]!;
            if ((string?)deployment["projectId"] != target.ProjectId || (string?)deployment["environmentId"] != target.EnvironmentId || (string?)deployment["serviceId"] != serviceId)
            {
                throw new InvalidOperationException("Railway returned a deployment outside the requested scope.");
            }

            string status = (string)deployment["status"]!;
            string[] instances = [.. deployment["instances"]!.AsArray().Select(instance => (string)instance!["status"]!)];
            bool finite = options.WaitForCompletion;
            if (status is "FAILED" or "CRASHED" or "REMOVED" or "REMOVING" or "SKIPPED" or "NEEDS_APPROVAL"
                || (finite && instances.Any(instance => instance is "CRASHED" or "STOPPED" or "REMOVED")))
            {
                throw new RailwayDeploymentFailedException();
            }

            if (finite && status == "SUCCESS" && (bool)deployment["deploymentStopped"]! && instances.Length != 0 && instances.All(instance => instance == "EXITED"))
            {
                return;
            }

            if (!finite && (status == "SUCCESS" || (options.SleepApplication && status == "SLEEPING")))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Railway deployment '{deploymentId}' exceeded its completion deadline.");
    }

    private static string ResolveRegion(string region)
    {
        if (region is "ams" or "europe-west4-drams3a" or "europe-west4")
        {
            return "ams";
        }

        if (region is "sfo" or "us-west2")
        {
            return "sfo";
        }

        if (region is "iad" or "us-east4-eqdc4a")
        {
            return "iad";
        }

        if (region is "sin" or "asia-southeast1-eqsg3a")
        {
            return "sin";
        }

        throw new ArgumentException("Unsupported Railway region identifier.", nameof(region));
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "This internal terminal-state signal deliberately accepts no provider message or credentials.")]
internal sealed class RailwayDeploymentFailedException : InvalidOperationException
{
    internal RailwayDeploymentFailedException() : base("The exact Railway deployment reached a terminal failed deployment or process state.")
    {
    }
}

internal sealed class RailwayServiceResult
{
    internal RailwayServiceResult(string serviceId, string privateHostname, string? publicUrl, string? deploymentId, bool deployed)
    {
        ServiceId = serviceId;
        PrivateHostname = privateHostname;
        PublicUrl = publicUrl;
        DeploymentId = deploymentId;
        Deployed = deployed;
    }

    internal string ServiceId { get; }
    internal string PrivateHostname { get; }
    internal string? PublicUrl { get; }
    internal string? DeploymentId { get; }
    internal bool Deployed { get; }
}
