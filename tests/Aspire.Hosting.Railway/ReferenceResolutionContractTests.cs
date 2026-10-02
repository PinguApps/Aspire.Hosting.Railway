using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.Railway.Tests;

public sealed class ReferenceResolutionContractTests
{
    [Fact]
    public void MissingSealedValueNamesTheVariableAndResourceWithoutLeakingOtherValues()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => RailwayDeploymentPipeline.GetSealedFingerprints(
            "web", "secret-token", ["MISSING"], new Dictionary<string, string> { ["OTHER"] = "secret-value" }));
        Assert.Contains("MISSING", error.Message, StringComparison.Ordinal);
        Assert.Contains("web", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ResourceLimitsMustBeFiniteBeforePublishing(double limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RailwayServiceValidation.Validate(new() { MemoryGB = limit }));
        Assert.Throws<ArgumentOutOfRangeException>(() => RailwayServiceValidation.Validate(new() { VCpus = limit }));
    }

    [Theory]
    [InlineData(EndpointProperty.Host, "broker.railway.internal")]
    [InlineData(EndpointProperty.Port, "5672")]
    [InlineData(EndpointProperty.TargetPort, "5672")]
    [InlineData(EndpointProperty.HostAndPort, "broker.railway.internal:5672")]
    [InlineData(EndpointProperty.Url, "tcp://broker.railway.internal:5672")]
    public async Task PublishedEndpointsResolvePrivateContainerAddresses(EndpointProperty property, string expected)
    {
        IDistributedApplicationBuilder app = CreateBuilder();
        IResourceBuilder<ContainerResource> broker = app.AddContainer("broker", "example")
            .WithEndpoint(name: "amqp", scheme: "tcp", targetPort: 5672)
            .PublishToRailway(Target(app), options => options.Image = Image);
        broker.GetRailwayOutputs().Populate("service", "broker.railway.internal", null, "deployment");
        string? result = await RailwayDeploymentPipeline.ResolveRuntimeValueAsync(broker.GetEndpoint("amqp").Property(property),
            new() { Caller = broker.Resource, ExecutionContext = app.ExecutionContext }, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task RedirectedConnectionStringUsesPublishedValuesAndUriEncoding()
    {
        IDistributedApplicationBuilder app = CreateBuilder();
        ParameterResource password = app.AddParameter("password", "p/@?", secret: true).Resource;
        ConnectionResource original = new("original", ReferenceExpression.Create($"unused-local-address"));
        ConnectionResource redirect = new("published", ReferenceExpression.Create($"amqp://user:{password:uri}@broker.railway.internal/%2Fclient"));
        original.Annotations.Add(new ConnectionStringRedirectAnnotation(redirect));
        string? result = await RailwayDeploymentPipeline.ResolveRuntimeValueAsync(new ConnectionStringReference(original, optional: false),
            new() { Caller = original, ExecutionContext = app.ExecutionContext }, TestContext.Current.CancellationToken);
        Assert.Equal("amqp://user:p%2F%40%3F@broker.railway.internal/%2Fclient", result);
    }

    [Fact]
    public async Task RedirectCyclesFailWithoutWaitingForLocalResources()
    {
        IDistributedApplicationBuilder app = CreateBuilder();
        ConnectionResource first = new("first", ReferenceExpression.Create($"first"));
        ConnectionResource second = new("second", ReferenceExpression.Create($"second"));
        first.Annotations.Add(new ConnectionStringRedirectAnnotation(second));
        second.Annotations.Add(new ConnectionStringRedirectAnnotation(first));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RailwayDeploymentPipeline.ResolveRuntimeValueAsync(new ConnectionStringReference(first, optional: false),
            new() { Caller = first, ExecutionContext = app.ExecutionContext }, TestContext.Current.CancellationToken));
    }

    private const string Image = "example@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static IDistributedApplicationBuilder CreateBuilder() => DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = ["--publisher", "manifest"], DisableDashboard = true });
    private static IResourceBuilder<RailwayTargetResource> Target(IDistributedApplicationBuilder app) => app.AddRailwayTarget("railway",
        app.AddParameter("project"), app.AddParameter("environment"), app.AddParameter("token", secret: true), app.AddParameter("site"));

    private sealed class ConnectionResource : Resource, IResourceWithConnectionString
    {
        internal ConnectionResource(string name, ReferenceExpression expression) : base(name)
        {
            ConnectionStringExpression = expression;
        }

        public ReferenceExpression ConnectionStringExpression { get; }
    }
}
