# Outputs and security

`GetRailwayOutputs()` exposes service ID, private hostname, public URL, and exact deployment ID. Output references participate in resource relationships. TypeScript uses `getRailwayPrivateHostname()` with explicit deployment dependencies.

Project tokens authenticate management requests. Registry credentials authenticate pulls. Sealed variables configure workloads. Keep these credentials distinct and separate across environments. Private pulls require an eligible Railway plan and registry credential; the [live report](live-validation.md) records what was actually verified.

Desired secret fingerprints use an infrastructure-token-keyed HMAC. Package state never stores registry passwords or sealed variable values. Provider errors suppress response bodies that could echo secrets. Package diagnostics do not log resolved secret values. Infrastructure credentials supplied as workload configuration cause refusal.

Aspire maintains deployment state independently, including parameter values. On Windows the default location is `%USERPROFILE%\.aspire\deployments\<AppHostSha>\<environment>.json`; AppHost paths and deployment environments have separate state. Treat these files as secrets; exclude them from source control, public build artifacts, and shared caches. See [Aspire deployment state caching](https://aspire.dev/deployment/deployment-state-caching/).

The package's `PinguApps.Railway.<resourceName>` sections record owned identities, desired fingerprints, and pending applications. Preserve them during normal deployments and credential rotation. Supply current inputs explicitly using the [C# guide](getting-started-dotnet.md); clearing deployment state also discards identity/recovery records and is not a credential-rotation strategy.

Removing local state does not delete Railway resources. Missing/replaced cached identities and unexpected exposure fail safely, requiring operator reconciliation. Never publish raw state or parameter files as test evidence.
