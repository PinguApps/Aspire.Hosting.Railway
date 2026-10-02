using Xunit;

namespace Aspire.Hosting.Railway.Tests;

public sealed class ReviewValidationContractTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("https://example.com")]
    [InlineData("example.com/")]
    [InlineData("example.com.")]
    [InlineData("127.0.0.1")]
    public void CustomDomainsRejectInvalidNamesBeforePublishing(string domain)
    {
        RailwayServiceOptions options = new() { Port = 80 };
        options.CustomDomains.Add(domain);
        Assert.Throws<ArgumentException>(() => RailwayServiceValidation.Validate(options));
    }

    [Fact]
    public void CustomDomainsRejectCaseInsensitiveDuplicates()
    {
        RailwayServiceOptions options = new() { Port = 80 };
        options.CustomDomains.Add("example.com");
        options.CustomDomains.Add("EXAMPLE.com");
        Assert.Throws<ArgumentException>(() => RailwayServiceValidation.Validate(options));
    }

    [Fact]
    public void DirectTargetConstructionCannotBypassTheSecretTokenInvariant()
    {
        IDistributedApplicationBuilder app = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
        Assert.Throws<ArgumentException>(() => new RailwayTargetResource("railway", app.AddParameter("project").Resource,
            app.AddParameter("environment").Resource, app.AddParameter("token").Resource, app.AddParameter("site").Resource, new()));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData(" ")]
    public void RetainedImagesRejectTrailingWhitespace(string suffix)
    {
        Assert.Throws<ArgumentException>(() => RailwayServiceValidation.ValidateImage(
            "example/image@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" + suffix));
    }
}
