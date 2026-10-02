using Aspire.Hosting.Railway.Management;
using Xunit;

namespace Aspire.Hosting.Railway.Tests;

public sealed class LiveScopeTests
{
    [Fact]
    [Trait("Category", "live-railway")]
    public async Task ConfiguredProjectTokenMatchesTheAllocatedEnvironmentAndSite()
    {
        string? project = Environment.GetEnvironmentVariable("RAILWAY_PROJECT_ID");
        string? environment = Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT_ID");
        string? token = Environment.GetEnvironmentVariable("RAILWAY_API_TOKEN");
        string? site = Environment.GetEnvironmentVariable("RAILWAY_SITE_KEY");
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(environment) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(site))
        {
            Assert.Skip("Live scope validation requires the four allocated Railway inputs.");
            return;
        }

        using HttpClient http = new();
        RailwayManagementClient client = new(http, token, RailwayAuthenticationMode.ProjectToken);
        await client.ValidateScopeAsync(new(project, environment, site, new()), TestContext.Current.CancellationToken);
    }
}
