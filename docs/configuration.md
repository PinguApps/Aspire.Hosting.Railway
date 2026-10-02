# Configuration

`RailwayTargetOptionsDto` defaults to project-token authentication. Explicit `Bearer` mode also requires `ExpectedProjectName` and `ExpectedWorkspaceId` from the allocation record.

`RailwayServiceOptions` and callback-free `RailwayServiceOptionsDto` configure identity/ownership, immutable image, start command, port, public/custom domains, readiness path, timeout, restart/retry policy, cron/finite execution, region, CPU/memory, sleeping, persistent mounts, and sealed variable names. Exactly one replica is published. TypeScript timeouts use seconds; C# uses `TimeSpan`.

Supported internal regions are `ams`, `sfo`, `iad`, `sin`. Public names `europe-west4-drams3a`, `us-west2`, `us-east4-eqdc4a`, and `asia-southeast1-eqsg3a` are also accepted. Persistent volume region changes require operator migration.

Image tags are rejected. A pre-attached `ContainerImageAnnotation` with a digest is supported, including `WithImageSHA256`. Deployment removes build/push annotations; local annotations remain intact.

Custom-domain DNS configuration remains external. Unexpected public exposure causes preflight failure. Sleeping is opt-in; active brokers/workers may prevent it.
