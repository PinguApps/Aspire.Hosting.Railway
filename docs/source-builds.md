# Railway-owned source builds

Keep `aspire deploy` as the entry point. By default, `PublishToRailway` still deploys an immutable image digest. Select `Build` to upload a source context and let Railway build and retain the container:

```csharp
web.PublishToRailway(target, options =>
{
    options.Build = new RailwayBuildOptions
    {
        ContextPath = "../..", // relative to the AppHost directory
        DockerfilePath = "src/Web/Dockerfile", // relative to the context
        BuildArguments = [new RailwayBuildArgument { Name = "RELEASE_LABEL", Value = releaseId }],
    };
    options.Port = 8080;
    options.PublicDomain = true;
    options.HealthCheckPath = "/health";
});
```

This works for both `ProjectResource` and `ContainerResource`. Supply a working Dockerfile and self-contained context explicitly; the package does not generate a Dockerfile or perform a local container build. Local Aspire behavior remains unchanged. Use a narrow application context rather than a directory containing unrelated files or credentials.

The deployment machine needs Railway CLI 5.63.1 or later. Set `RailwayTargetOptionsDto.CliPath` if its executable is not on PATH. The package validates this prerequisite during the read-only target plan. Infrastructure authentication is unchanged: use a scoped Railway project token, or the explicitly constrained Bearer mode. No GitHub token is required for the Railway-built image. A private Dockerfile base image or private build-time dependency still needs its own access arrangement.

`Build` is mutually exclusive with an explicit `Image` and private-registry parameters. Non-secret `BuildArguments` become Railway variables and therefore can also be visible at runtime. Declare the corresponding Dockerfile `ARG`s. They cannot override runtime variables, use publisher-managed `PORT` or `ASPNETCORE_HTTP_PORTS`, or use names beginning `RAILWAY_` or `PINGUAPPS_`. Never put secrets in build arguments, Dockerfiles, or committed source.

Source contexts cannot contain `railway.json` or `railway.toml`, including below subdirectories. Railway [config-as-code takes precedence over service settings](https://docs.railway.com/config-as-code/reference) and could override the declared builder, restart policy, cron schedule, or runtime settings. The package rejects these files before upload or provider mutation; configure deployment behavior through the Aspire options instead. Existing services with a custom Railway Config File path are also rejected in source mode: clear that setting in Railway before migration. Image mode retains its existing behavior.

The package snapshots only the explicit context before mutation and rejects symlinks. It excludes `.git`, `.aspire`, `.railway`, `.env`, `.env.*`, `secrets.json`, `bin`, `obj`, and `node_modules` at every directory level. It also refuses files containing the current control-plane credential. The temporary snapshot combines the effective Docker ignore file followed by `.railwayignore`, then explicitly retains only the Docker build control files. A nested selected Dockerfile is copied to the reserved root transport name `.pinguapps-railway.Dockerfile`, together with its Dockerfile-specific `.dockerignore` when present; conflicting root names are rejected. This preserves context-relative `COPY`/`ADD` paths and Dockerfile-specific ignore precedence without unignoring parent directories or their siblings. The public selected Dockerfile path stays unchanged. Security exclusions remain authoritative. Railway CLI additionally honors `.gitignore`; ignore patterns use the CLI's semantics rather than a complete Docker ignore parser. Ensure required application inputs are not excluded. Disposable snapshot files are removed when deployment completes, fails, or is cancelled. Upload contexts must contain no other credentials; filename exclusions cannot identify every secret.

The source content identity, options, runtime bindings, and scoped deployment request are recorded before upload. Desired configuration clears any external image source and registry credentials, selects the Dockerfile builder, and is committed with deployments disabled. Then the package runs `railway up --detach --json` internally with explicit project, environment, and service IDs. CLI tokens are supplied through its process environment, never its arguments or workload configuration; CLI diagnostics are withheld because they can contain credentials.

Queued acceptance is insufficient. The package waits for the exact returned deployment to prove its scope, snapshot request marker, matching CLI upload message, and requested Dockerfile builder/path. It then verifies readiness or successful finite-process exit and captures the built image digest when Railway supplies it. Timeout/cancellation retains the sent request for recovery and does not silently launch it again. Unchanged ordinary publication reuses the verified deployment; a new explicit finite-job invocation intentionally executes again, preserving the existing job behavior.

Railway may initially report default build settings for a new upload before its Dockerfile configuration is materialized. The package polls that same exact deployment within the original deadline until its requested builder/path appears; it never accepts the temporary defaults. Scope, snapshot marker, and upload-message mismatches fail immediately.

`GetRailwayOutputs().DeploymentId` and `.BuildFingerprint` report the exact completed deployment and local source snapshot identity. `.ImageDigest` captures a provider-reported SHA256 digest when available; Railway can omit it for cached/finite builds, so do not require this optional output as a source-build runtime binding. TypeScript has `getRailwayDeploymentId()`, `getRailwayBuildFingerprint()`, and `getRailwayImageDigest()` expressions. `.Image` / `getRailwayImage()` remains available when Railway reports an external retained image URI; it is absent for source builds. A built digest does not supply a portable registry URI or credentials. Railway retains that service's build for restarts and redeployment; this package does not promise cross-service image reuse or a permanent rollback archive. Separate services/jobs using the same context build independently. Preserve release compatibility and Railway retention requirements when designing rollback.

```typescript
web = await web.publishToRailway(target, {
  build: {
    contextPath: "../..", dockerfilePath: "src/Web/Dockerfile",
    buildArguments: [{ name: "RELEASE_LABEL", value: "release-123" }]
  },
  port: 8080, publicDomain: true, healthCheckPath: "/health"
});
```
