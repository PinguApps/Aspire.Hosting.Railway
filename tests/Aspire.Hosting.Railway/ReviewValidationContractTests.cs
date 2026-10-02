using Xunit;

namespace Aspire.Hosting.Railway.Tests;

public sealed class ReviewValidationContractTests
{
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
