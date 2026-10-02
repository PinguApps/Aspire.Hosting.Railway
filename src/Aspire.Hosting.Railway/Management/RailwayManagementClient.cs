using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Railway.Management;

internal sealed class RailwayManagementClient
{
    private readonly HttpClient _httpClient;
    private readonly string _token;
    private readonly RailwayAuthenticationMode _authenticationMode;

    internal RailwayManagementClient(HttpClient httpClient, string token, RailwayAuthenticationMode authenticationMode)
    {
        _httpClient = httpClient;
        _token = token;
        _authenticationMode = authenticationMode;
    }

    internal async Task<JsonObject> SendAsync(string query, object variables, CancellationToken cancellationToken)
    {
        string operation = GetOperationName(query);
        using HttpRequestMessage request = new(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
        {
            Content = JsonContent.Create(new { query, variables }),
        };
        if (_authenticationMode == RailwayAuthenticationMode.ProjectToken)
        {
            request.Headers.Add("Project-Access-Token", _token);
        }
        else
        {
            request.Headers.Authorization = new("Bearer", _token);
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Railway operation '{operation}' failed with HTTP {(int)response.StatusCode}. Response details are suppressed to protect credentials.");
        }

        JsonObject body = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Railway returned an empty control-plane response.");
        if (body["errors"] is JsonArray { Count: > 0 } errors)
        {
            if (operation == "serviceInstanceDeployV2" && errors.All(error => (string?)error?["message"] == "Deployment not found"))
            {
                throw new RailwayDeploymentNotFoundException();
            }

            throw new InvalidOperationException($"Railway rejected operation '{operation}'. Provider details are suppressed to protect workload secrets.");
        }

        return body["data"]?.AsObject() ?? throw new InvalidOperationException("Railway returned no operation data.");
    }

    private static string GetOperationName(string query)
    {
        string[] operations = ["environmentPatchCommit", "serviceCreate", "serviceInstanceDeployV2", "serviceInstanceDeploy", "serviceInstanceUpdate", "volumeCreate", "serviceDomainCreate", "customDomainCreate", "serviceInstanceLimitsUpdate", "serviceInstanceLimits", "projectToken", "variables", "domains", "deployment", "environment", "project"];
        return operations.FirstOrDefault(operation => query.Contains(operation, StringComparison.Ordinal)) ?? "unknown";
    }

    internal async Task ValidateScopeAsync(RailwayResolvedTarget target, CancellationToken cancellationToken)
    {
        if (_authenticationMode == RailwayAuthenticationMode.ProjectToken)
        {
            JsonObject data = await SendAsync("query { projectToken { projectId environmentId } }", new { }, cancellationToken).ConfigureAwait(false);
            JsonNode token = data["projectToken"]!;
            if ((string?)token["projectId"] != target.ProjectId || (string?)token["environmentId"] != target.EnvironmentId)
            {
                throw new InvalidOperationException("The Railway project token does not match the recorded project and environment.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(target.Options.ExpectedProjectName) || string.IsNullOrWhiteSpace(target.Options.ExpectedWorkspaceId))
            {
                throw new InvalidOperationException("Account authentication requires the recorded project name and workspace identity.");
            }

            JsonObject data = await SendAsync("query($id:String!){project(id:$id){name workspaceId}}", new { id = target.ProjectId }, cancellationToken).ConfigureAwait(false);
            if ((string?)data["project"]?["name"] != target.Options.ExpectedProjectName
                || (string?)data["project"]?["workspaceId"] != target.Options.ExpectedWorkspaceId)
            {
                throw new InvalidOperationException("The Railway project does not match its site allocation record.");
            }
        }

        JsonObject environment = await SendAsync("query($id:String!){environment(id:$id){projectId}}", new { id = target.EnvironmentId }, cancellationToken).ConfigureAwait(false);
        if ((string?)environment["environment"]?["projectId"] != target.ProjectId)
        {
            throw new InvalidOperationException("The Railway environment belongs to a different project.");
        }

        JsonObject shared = await ReadVariablesAsync(target, null, cancellationToken).ConfigureAwait(false);
        if ((string?)shared["PINGUAPPS_SITE_KEY"] != target.SiteKey)
        {
            throw new InvalidOperationException("The Railway environment site marker does not match the recorded site identity.");
        }
    }

    internal async Task<JsonObject> ReadVariablesAsync(RailwayResolvedTarget target, string? serviceId, CancellationToken cancellationToken)
    {
        JsonObject data = await SendAsync(
            "query($project:String!,$environment:String!,$service:String){variables(projectId:$project,environmentId:$environment,serviceId:$service,unrendered:true)}",
            new { project = target.ProjectId, environment = target.EnvironmentId, service = serviceId }, cancellationToken).ConfigureAwait(false);
        return data["variables"]!.AsObject();
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "This internal transport condition deliberately accepts no provider message or credentials.")]
internal sealed class RailwayDeploymentNotFoundException : InvalidOperationException
{
    internal RailwayDeploymentNotFoundException() : base("Railway has not yet established its initial deployment.")
    {
    }
}
