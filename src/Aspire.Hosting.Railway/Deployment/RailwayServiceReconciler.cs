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
        RailwayServiceValidation.Validate(options);
        if (options.Build is null)
        {
            RailwayServiceValidation.ValidateImage(image);
        }
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

            return options.Build is null ? "Create a site-owned immutable container service." : "Create a site-owned service and upload its Dockerfile build context.";
        }

        ValidateSourceConfigFile(instance, options);
        string serviceId = (string)instance["serviceId"]!;
        JsonObject variables = await _client.ReadVariablesAsync(target, serviceId, cancellationToken).ConfigureAwait(false);
        ValidateOwnership(variables, target.SiteKey, resourceName, serviceId, options, identity);
        ValidateUnspecifiedRegion(instance, options);
        ValidateVolumeDrift(environment, serviceId, options, identity);
        JsonObject domains = await ReadDomainsAsync(target, serviceId, cancellationToken).ConfigureAwait(false);
        ValidateDomainDrift(domains, options);
        if (SettingsMatch(instance, DesiredSettings(image, options)))
        {
            return options.Build is null ? "Reuse service settings; reconcile runtime bindings and readiness."
                : "Reuse service settings; reconcile readiness and rebuild if source content, build inputs, or deployment state changed.";
        }
        return options.Build is null ? "Update the existing site's service configuration and retained image."
            : "Update the existing site's service configuration and upload its Dockerfile build context.";
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
        IReadOnlyDictionary<string, string>? sealedFingerprints = null,
        Func<string, string, CancellationToken, Task<string>>? upload = null)
    {
        Stopwatch overall = Stopwatch.StartNew();
        RailwayServiceValidation.Validate(options);
        if (options.Build is null)
        {
            RailwayServiceValidation.ValidateImage(image);
        }
        else if (upload is null)
        {
            throw new InvalidOperationException("Railway source builds require a source-upload transport.");
        }
        await _client.ValidateScopeAsync(target, cancellationToken).ConfigureAwait(false);
        ValidateCachedScope(identity, target, serviceName);
        JsonObject environment = await ReadEnvironmentAsync(target, cancellationToken).ConfigureAwait(false);
        JsonObject? instance = FindService(environment, serviceName, (string?)identity["serviceId"] ?? options.ExistingServiceId);
        ValidateSourceConfigFile(instance, options);
        bool created = false;
        if (variables.ContainsKey("PINGUAPPS_DEPLOYMENT_REQUEST"))
        { throw new ArgumentException("PINGUAPPS_DEPLOYMENT_REQUEST is reserved for Railway deployment identity.", nameof(variables)); }
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
            if (options.WaitForCompletion && (bool?)priorAttempt["sent"] != false)
            {
                throw new InvalidOperationException("A pending finite Railway deployment has different desired configuration or credentials. Reconcile its recorded execution before requesting another process.");
            }

            if ((bool?)priorAttempt["sent"] == true && priorAttempt["managedVariables"] is JsonArray attemptedNames)
            {
                IEnumerable<string> previousNames = identity["managedVariables"] is JsonArray names
                    ? names.Select(name => (string)name!) : [];
                identity["managedVariables"] = new JsonArray([.. previousNames.Concat(attemptedNames.Select(name => (string)name!))
                    .Distinct(StringComparer.Ordinal).Select(name => JsonValue.Create(name))]);
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
        ValidateSourceConfigFile(instance, options);
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
        bool regionChanged = false;
        if (settings["multiRegionConfig"] is JsonObject desiredRegions)
        {
            string providerRegion = ResolveProviderRegion(options.Region!);
            JsonNode? currentRegions = instance["latestDeployment"]?["meta"]?["serviceManifest"]?["deploy"]?["multiRegionConfig"];
            if (!JsonNode.DeepEquals(currentRegions, desiredRegions) || (int?)instance["numReplicas"] != 1
                || (string?)identity["configuredRegion"] != providerRegion)
            {
                await _client.SendAsync("mutation($input:ServiceInstanceUpdateInput!,$service:String!,$environment:String!){serviceInstanceUpdate(input:$input,serviceId:$service,environmentId:$environment)}",
                    new { input = new { region = providerRegion, multiRegionConfig = desiredRegions, numReplicas = 1 }, service = serviceId, environment = target.EnvironmentId }, cancellationToken).ConfigureAwait(false);
                identity["configuredRegion"] = providerRegion;
                await saveIdentity().ConfigureAwait(false);
                regionChanged = true;
            }
        }
        bool volumesChanged = await EnsureVolumesAsync(target, environment, serviceId, options, identity, saveIdentity, cancellationToken).ConfigureAwait(false);
        bool domainsChanged = await EnsureDomainsAsync(target, serviceId, options, cancellationToken).ConfigureAwait(false);
        bool limitsChanged = await EnsureLimitsAsync(target, serviceId, options, cancellationToken).ConfigureAwait(false);
        string? deploymentId = (string?)instance["latestDeployment"]?["id"];
        bool deploy = created || settingsChanged || variablesChanged || regionChanged || volumesChanged || domainsChanged || limitsChanged
            || deploymentId is null || options.WaitForCompletion || pending
            || (options.Build is not null && ((string?)identity["desiredFingerprint"] != desiredFingerprint
                || (string?)identity["completedDeploymentId"] != deploymentId));
        JsonObject? deploymentPatch = null;
        if (deploy)
        {
            JsonObject patchService = [];
            JsonNode? source = settings["source"];
            settings.Remove("source");
            patchService["source"] = source;
            if (options.Build is not null)
            {
                patchService["build"] = new JsonObject { ["builder"] = "DOCKERFILE", ["dockerfilePath"] = RailwaySourceUpload.GetTransportDockerfilePath(options.Build) };
            }
            settings["registryCredentials"] = registryCredentials?.DeepClone();

            settings.Remove("multiRegionConfig");

            patchService["deploy"] = settings;

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

            deploymentPatch = new() { ["services"] = new JsonObject { [serviceId] = patchService } };
        }

        if (deploy)
        {
            deploymentId = await LaunchOrRecoverAsync(target, serviceId, desiredFingerprint, options,
                identity, saveIdentity, overall, deploymentPatch, upload, cancellationToken).ConfigureAwait(false);
        }

        if (deploymentPatch is not null)
        {
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

        try
        {
            string? deployedImage = await WaitForDeploymentAsync(target, serviceId, deploymentId!, image, options, options.DeploymentTimeout - overall.Elapsed, cancellationToken).ConfigureAwait(false);
            if (options.Volumes.Count != 0)
            {
                ValidateVolumeDrift(await ReadEnvironmentAsync(target, cancellationToken).ConfigureAwait(false), serviceId, options, identity);
            }
            identity["image"] = options.Build is null ? deployedImage : null;
            identity["imageDigest"] = options.Build is null ? deployedImage?[(deployedImage.IndexOf('@', StringComparison.Ordinal) + 1)..] : deployedImage;
            identity["buildFingerprint"] = options.Build is null ? null : image;
        }
        catch (RailwayDeploymentFailedException)
        {
            identity["pending"] = !options.WaitForCompletion;
            identity.Remove("deploymentAttempt");
            identity.Remove("applyPhase");
            await saveIdentity().ConfigureAwait(false);
            throw;
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
        identity["desiredFingerprint"] = desiredFingerprint;
        identity["completedDeploymentId"] = deploymentId;
        await saveIdentity().ConfigureAwait(false);
        return new RailwayServiceResult(serviceId, hostname, publicDomain is null ? null : $"https://{publicDomain}", deploymentId, deploy, (string?)identity["image"], (string?)identity["buildFingerprint"], (string?)identity["imageDigest"]);
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
        if (options.Build is not null)
        {
            desired["sourceContent"] = image;
            desired["dockerfile"] = options.Build.DockerfilePath.Replace('\\', '/');
        }
        return _client.Fingerprint(desired.ToJsonString());
    }

    private async Task<string> LaunchOrRecoverAsync(RailwayResolvedTarget target, string serviceId, string desiredFingerprint,
        RailwayServiceOptions options, JsonObject identity, Func<Task> saveIdentity, Stopwatch overall, JsonObject? patch,
        Func<string, string, CancellationToken, Task<string>>? upload, CancellationToken cancellationToken)
    {
        JsonObject attempt;
        if (identity["deploymentAttempt"] is JsonObject recorded)
        {
            attempt = recorded;
            if (!(bool)attempt["sent"]!)
            {
                HashSet<string> before = await ReadDeploymentIdsAsync(target, serviceId, cancellationToken).ConfigureAwait(false);
                attempt["baseline"] = new JsonArray([.. before.Order(StringComparer.Ordinal).Select(id => JsonValue.Create(id))]);
                attempt["requestId"] ??= JsonValue.Create(Guid.NewGuid().ToString("N"));
                await saveIdentity().ConfigureAwait(false);
            }
        }
        else
        {
            HashSet<string> before = await ReadDeploymentIdsAsync(target, serviceId, cancellationToken).ConfigureAwait(false);
            attempt = new JsonObject
            {
                ["desiredFingerprint"] = desiredFingerprint,
                ["baseline"] = new JsonArray([.. before.Order(StringComparer.Ordinal).Select(id => JsonValue.Create(id))]),
                ["sent"] = false,
                ["operation"] = "patch",
                ["requestId"] = Guid.NewGuid().ToString("N"),
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
                if (!(bool)attempt["sent"]!)
                { throw new InvalidOperationException("An unexpected Railway deployment appeared before the recorded request. Reconcile concurrent changes before retrying."); }
                await ValidateDeploymentRequestAsync(target, serviceId, deploymentId, attempt, overall, options, cancellationToken).ConfigureAwait(false);
                attempt["id"] = deploymentId;
                await saveIdentity().ConfigureAwait(false);
                return deploymentId;
            }

            if ((bool)attempt["sent"]!)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (patch is null || (string?)attempt["requestId"] is not string requestId)
            { throw new InvalidOperationException("The recorded configuration deployment requires its desired patch and request marker before it can be retried."); }
            JsonObject servicePatch = patch["services"]![serviceId]!.AsObject();
            JsonObject patchVariables = servicePatch["variables"]!.AsObject();
            attempt["managedVariables"] = new JsonArray([.. patchVariables.Where(variable => variable.Value is not null).Select(variable => JsonValue.Create(variable.Key))]);
            patchVariables["PINGUAPPS_DEPLOYMENT_REQUEST"] = new JsonObject { ["value"] = requestId, ["isSealed"] = false };
            try
            {
                if (upload is not null)
                {
                    await _client.SendAsync("mutation($environment:String!,$patch:EnvironmentConfig!){environmentPatchCommit(environmentId:$environment,patch:$patch,skipDeploys:true)}",
                        new { environment = target.EnvironmentId, patch }, cancellationToken).ConfigureAwait(false);
                    attempt["sent"] = true;
                    await saveIdentity().ConfigureAwait(false);
                    attempt["id"] = await upload(serviceId, requestId, cancellationToken).ConfigureAwait(false);
                    await saveIdentity().ConfigureAwait(false);
                }
                else
                {
                    attempt["sent"] = true;
                    await saveIdentity().ConfigureAwait(false);
                    JsonObject committed = await _client.SendAsync("mutation($environment:String!,$patch:EnvironmentConfig!){environmentPatchCommit(environmentId:$environment,patch:$patch,skipDeploys:false)}",
                        new { environment = target.EnvironmentId, patch }, cancellationToken).ConfigureAwait(false);
                    attempt["patchId"] = (string?)committed["environmentPatchCommit"];
                    await saveIdentity().ConfigureAwait(false);
                }
            }
            catch (RailwayDeploymentRejectedException)
            {
                identity.Remove("deploymentAttempt");
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

    private async Task ValidateDeploymentRequestAsync(RailwayResolvedTarget target, string serviceId, string deploymentId, JsonObject attempt, Stopwatch overall, RailwayServiceOptions options, CancellationToken cancellationToken)
    {
        TimeSpan timeout = options.DeploymentTimeout;
        if ((string?)attempt["requestId"] is not string requestId)
        {
            if ((string?)attempt["id"] != deploymentId)
            { throw new InvalidOperationException("A legacy sent request has no exact recorded deployment identity or marker. Operator reconciliation is required before accepting an observed execution."); }
            return;
        }
        string? expectedPatchId = (string?)attempt["patchId"];
        while (overall.Elapsed < timeout)
        {
            TimeSpan budget = timeout - overall.Elapsed;
            if (budget <= TimeSpan.Zero)
            {
                break;
            }

            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(budget);
            JsonObject data;
            try
            {
                data = await _client.SendAsync("query($id:String!){deployment(id:$id){projectId environmentId serviceId meta} deploymentSnapshot(deploymentId:$id){variables}}",
                    new { id = deploymentId }, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (overall.Elapsed >= timeout)
            {
                break;
            }

            JsonNode? deployment = data["deployment"];
            string? project = (string?)deployment?["projectId"];
            string? environment = (string?)deployment?["environmentId"];
            string? service = (string?)deployment?["serviceId"];
            string? marker = (string?)data["deploymentSnapshot"]?["variables"]?["PINGUAPPS_DEPLOYMENT_REQUEST"];
            string? actualPatchId = (string?)deployment?["meta"]?["patchId"];
            string? cliMessage = (string?)deployment?["meta"]?["cliMessage"];
            string? builder = (string?)deployment?["meta"]?["serviceManifest"]?["build"]?["builder"];
            string? dockerfile = (string?)deployment?["meta"]?["serviceManifest"]?["build"]?["dockerfilePath"];
            if ((project is not null && project != target.ProjectId) || (environment is not null && environment != target.EnvironmentId)
                || (service is not null && service != serviceId) || (marker is not null && marker != requestId)
                || (expectedPatchId is not null && actualPatchId is not null
                    && expectedPatchId != actualPatchId && expectedPatchId != $"commitChanges/{target.EnvironmentId}/{actualPatchId}"))
            {
                throw new InvalidOperationException("The exact Railway deployment does not prove its association with the recorded configuration request. Reconcile concurrent changes before retrying.");
            }
            if (options.Build is not null && cliMessage is not null && cliMessage != requestId)
            {
                throw new InvalidOperationException("The exact Railway source deployment has a different upload request message. Reconcile its recorded request before retrying.");
            }
            if (project is not null && environment is not null && service is not null && marker is not null
                && (expectedPatchId is null || actualPatchId is not null)
                && (options.Build is null || (cliMessage is not null && builder == "DOCKERFILE"
                    && dockerfile == RailwaySourceUpload.GetTransportDockerfilePath(options.Build))))
            {
                return;
            }

            TimeSpan remaining = timeout - overall.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(500, remaining.TotalMilliseconds)), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Railway did not make the exact deployment's request association available within its deadline. No second configuration request was issued; reconcile or resume the recorded attempt.");
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
        JsonObject data = await _client.SendAsync("query($id:String!){environment(id:$id){serviceInstances{edges{node{serviceId serviceName railwayConfigFile source{image repo} startCommand restartPolicyType restartPolicyMaxRetries cronSchedule healthcheckPath healthcheckTimeout region sleepApplication numReplicas latestDeployment{id status meta}}}} volumeInstances{edges{node{volumeId serviceId mountPath region}}}}}",
            new { id = target.EnvironmentId }, cancellationToken).ConfigureAwait(false);
        return data["environment"]!.AsObject();
    }

    private static void ValidateSourceConfigFile(JsonObject? instance, RailwayServiceOptions options)
    {
        if (options.Build is not null && !string.IsNullOrEmpty((string?)instance?["railwayConfigFile"]))
        {
            throw new InvalidOperationException("Railway source builds cannot adopt a service with a custom config-as-code path. Clear its Railway Config File setting before publishing so declared deployment options remain authoritative.");
        }
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
            ["source"] = options.Build is null ? new JsonObject { ["image"] = image } : new JsonObject { ["image"] = null, ["repo"] = null },
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
        if (desiredImage is not null && actual["latestDeployment"]?["meta"]?["image"] is JsonNode deployedImage && (string?)deployedImage != desiredImage)
        {
            return false;
        }

        JsonNode? deployed = actual["latestDeployment"]?["meta"]?["serviceManifest"]?["deploy"];
        return desired.All(pair => pair.Key == "source"
            ? (string?)actual["source"]?["image"] == desiredImage && actual["source"]?["repo"] is null
            : JsonNode.DeepEquals(deployed?[pair.Key] ?? actual[pair.Key], pair.Value));
    }

    private static void ValidateVolumeDrift(JsonObject environment, string serviceId, RailwayServiceOptions options, JsonObject identity)
    {
        JsonNode[] attached = [.. environment["volumeInstances"]!["edges"]!.AsArray().Select(edge => edge!["node"]!).Where(volume => (string?)volume["serviceId"] == serviceId)];
        if (attached.Any(volume => !options.Volumes.Any(desired => desired.MountPath == (string?)volume["mountPath"])))
        {
            throw new InvalidOperationException("Existing Railway volume mount drift requires operator reconciliation. No volume is deleted or moved.");
        }

        if (options.Region is not null && attached.Any(volume => !RegionMatches((string?)volume["region"], options.Region)))
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
            if (existing is null)
            {
                await ValidateCreatedVolumeAsync(target, serviceId, volumeId, mount.MountPath, options, cancellationToken).ConfigureAwait(false);
            }
        }

        return changed;
    }

    private async Task ValidateCreatedVolumeAsync(RailwayResolvedTarget target, string serviceId, string volumeId, string mountPath, RailwayServiceOptions options, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < options.DeploymentTimeout)
        {
            JsonObject environment = await ReadEnvironmentAsync(target, cancellationToken).ConfigureAwait(false);
            JsonNode? volume = environment["volumeInstances"]!["edges"]!.AsArray().Select(edge => edge!["node"]!)
                .SingleOrDefault(instance => (string?)instance["volumeId"] == volumeId);
            if (volume is not null)
            {
                if ((string?)volume["serviceId"] != serviceId || (string?)volume["mountPath"] != mountPath
                    || (options.Region is not null && !RegionMatches((string?)volume["region"], options.Region)))
                {
                    throw new InvalidOperationException("The created Railway volume does not prove its requested service, mount and region. Reconcile it before deployment; persistent data is never moved automatically.");
                }
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("The created Railway volume was not visible before its deployment deadline.");
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

    private async Task<string?> WaitForDeploymentAsync(RailwayResolvedTarget target, string serviceId, string deploymentId, string image, RailwayServiceOptions options, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            JsonObject data = await _client.SendAsync("query($id:String!){deployment(id:$id){projectId environmentId serviceId status meta deploymentStopped instances{id status}}}",
                new { id = deploymentId }, cancellationToken).ConfigureAwait(false);
            JsonNode deployment = data["deployment"]!;
            if ((string?)deployment["projectId"] != target.ProjectId || (string?)deployment["environmentId"] != target.EnvironmentId || (string?)deployment["serviceId"] != serviceId)
            {
                throw new InvalidOperationException("Railway returned a deployment outside the requested scope.");
            }

            string status = (string)deployment["status"]!;
            if (options.Region is not null && status is "SUCCESS" or "SLEEPING")
            {
                JsonNode? actualRegions = deployment["meta"]?["serviceManifest"]?["deploy"]?["multiRegionConfig"];
                JsonObject expectedRegions = new() { [ResolveRegion(options.Region)] = new JsonObject { ["numReplicas"] = 1 } };
                if (!JsonNode.DeepEquals(actualRegions, expectedRegions))
                {
                    throw new InvalidOperationException("The exact Railway deployment does not prove the requested single region and replica count. Reconcile its configuration before retrying.");
                }
            }
            string? deployedImage = (string?)deployment["meta"]?["image"];
            if (options.Build is not null)
            {
                deployedImage = (string?)deployment["meta"]?["imageDigest"];
                if (deployedImage is not null)
                {
                    RailwayServiceValidation.ValidateBuiltDigest(deployedImage);
                }
            }
            if (options.Build is null && ((deployedImage is not null && deployedImage != image)
                || ((status is "SUCCESS" or "SLEEPING") && deployedImage is null)))
            {
                throw new InvalidOperationException("The exact Railway deployment does not prove the requested retained image. Reconcile its recorded execution before requesting another process.");
            }

            string[] instances = [.. deployment["instances"]!.AsArray().Select(instance => (string)instance!["status"]!)];
            bool finite = options.WaitForCompletion;
            if (status is "FAILED" or "CRASHED" or "REMOVED" or "REMOVING" or "SKIPPED" or "NEEDS_APPROVAL"
                || (finite && instances.Any(instance => instance is "CRASHED" or "STOPPED" or "REMOVED")))
            {
                throw new RailwayDeploymentFailedException();
            }

            if (finite && status == "SUCCESS" && (bool)deployment["deploymentStopped"]! && instances.Length != 0 && instances.All(instance => instance == "EXITED"))
            {
                return deployedImage;
            }

            if (!finite && (status == "SUCCESS" || (options.SleepApplication && status == "SLEEPING")))
            {
                return deployedImage;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Railway deployment '{deploymentId}' exceeded its completion deadline.");
    }

    internal static string ResolveRegion(string region)
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

    internal static string ResolveProviderRegion(string region)
    {
        string airport = ResolveRegion(region);
        if (airport == "ams")
        {
            return "europe-west4-drams3a";
        }
        if (airport == "sfo")
        {
            return "us-west2";
        }
        if (airport == "iad")
        {
            return "us-east4-eqdc4a";
        }
        if (airport == "sin")
        {
            return "asia-southeast1-eqsg3a";
        }
        throw new ArgumentException("Unsupported Railway region identifier.", nameof(region));
    }

    private static bool RegionMatches(string? actual, string expected) => actual is not null
        && (actual == ResolveRegion(expected) || actual == ResolveProviderRegion(expected)
            || (ResolveRegion(expected) == "ams" && actual == "europe-west4"));
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
    internal RailwayServiceResult(string serviceId, string privateHostname, string? publicUrl, string? deploymentId, bool deployed, string? image = null, string? buildFingerprint = null, string? imageDigest = null)
    {
        ServiceId = serviceId;
        PrivateHostname = privateHostname;
        PublicUrl = publicUrl;
        DeploymentId = deploymentId;
        Deployed = deployed;
        Image = image;
        BuildFingerprint = buildFingerprint;
        ImageDigest = imageDigest;
    }

    internal string ServiceId { get; }
    internal string PrivateHostname { get; }
    internal string? PublicUrl { get; }
    internal string? DeploymentId { get; }
    internal bool Deployed { get; }
    internal string? Image { get; }
    internal string? BuildFingerprint { get; }
    internal string? ImageDigest { get; }
}
