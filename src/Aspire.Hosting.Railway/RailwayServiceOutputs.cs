using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Railway;

/// <summary>Contains application-safe Railway service deployment outputs.</summary>
[AspireExportIgnore(Reason = "TypeScript receives individual output expressions.")]
public sealed class RailwayServiceOutputs
{
    private readonly IResource _resource;
    private string? _serviceId;
    private string? _privateHostname;
    private string? _publicUrl;
    private string? _deploymentId;
    private string? _image;
    private string? _buildFingerprint;
    private string? _imageDigest;

    internal RailwayServiceOutputs(IResource resource)
    {
        _resource = resource;
    }

    /// <summary>Gets the remote service identity.</summary>
    public RailwayOutputReference ServiceId => new(_resource, "serviceId", () => _serviceId);
    /// <summary>Gets the private environment hostname.</summary>
    public RailwayOutputReference PrivateHostname => new(_resource, "privateHostname", () => _privateHostname);
    /// <summary>Gets the generated public HTTPS URL.</summary>
    public RailwayOutputReference PublicUrl => new(_resource, "publicUrl", () => _publicUrl);
    /// <summary>Gets the exact deployment observed by the publisher.</summary>
    public RailwayOutputReference DeploymentId => new(_resource, "deploymentId", () => _deploymentId);
    /// <summary>Gets the image reported for the exact completed deployment, when Railway exposes it.</summary>
    public RailwayOutputReference Image => new(_resource, "image", () => _image);
    /// <summary>Gets the local source snapshot fingerprint associated with the completed Railway-owned build.</summary>
    public RailwayOutputReference BuildFingerprint => new(_resource, "buildFingerprint", () => _buildFingerprint);
    /// <summary>Gets the SHA256 digest of the completed image when reported; source builds can omit it.</summary>
    public RailwayOutputReference ImageDigest => new(_resource, "imageDigest", () => _imageDigest);

    internal void Populate(string serviceId, string hostname, string? publicUrl, string? deploymentId, string? image = null, string? buildFingerprint = null, string? imageDigest = null)
    {
        _serviceId = serviceId;
        _privateHostname = hostname;
        _publicUrl = publicUrl;
        _deploymentId = deploymentId;
        _image = image;
        _buildFingerprint = buildFingerprint;
        _imageDigest = imageDigest;
    }
}

/// <summary>An application-safe deferred output value.</summary>
[AspireExportIgnore(Reason = "TypeScript receives ReferenceExpression values.")]
public sealed class RailwayOutputReference : IValueProvider, IManifestExpressionProvider, IValueWithReferences
{
    private readonly Func<string?> _value;
    private readonly IResource _resource;

    internal RailwayOutputReference(IResource resource, string name, Func<string?> value)
    {
        _resource = resource;
        _value = value;
        ValueExpression = $"{{{resource.Name}.railway.{name}}}";
    }

    /// <summary>Gets the application-safe manifest expression.</summary>
    public string ValueExpression { get; }
    /// <summary>Gets the resource supplying this output.</summary>
    public IEnumerable<object> References => [_resource];
    /// <summary>Resolves an already deployed resource output.</summary>
    public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<string?>(_value() ?? throw new InvalidOperationException("Railway output is unavailable before deployment."));
    }
}
