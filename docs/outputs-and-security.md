# Outputs and security

`GetRailwayOutputs()` exposes only service ID, private hostname, public URL, and exact deployment ID. Output references participate in resource relationships. TypeScript uses `getRailwayPrivateHostname()` with explicit deployment dependencies.

Project tokens authenticate management requests. Registry credentials authenticate pulls. Sealed variable values configure workloads. These credentials are never interchangeable or shared across environments.

Desired secret fingerprints use an infrastructure-token-keyed HMAC. Package state never stores registry passwords or sealed variable values. Provider errors suppress response bodies that could echo secrets. No package diagnostic logs resolved secret values.

Supply current credential parameters for every deployment. Aspire's own parameter state is independent of this package; protect it and avoid stale cache fallback in CI.
