# Deployment behavior

The plan runs after `process-parameters` and before `deploy-prereq`, validating scope, ownership, cache, volumes, and domains across the target without writes. Each service repeats live checks before mutation.

Settings and runtime variables use a configuration patch with `skipDeploys`, followed by one explicit deployment whose returned ID is tracked. Repeat deployment compares desired live settings, variables, secret fingerprints, volume/domain identities, and resource limits. Unchanged ordinary services reuse their successful deployment; finite release jobs explicitly execute again.

Railway can initially return `Deployment not found` while a newly configured image source becomes available. Only this exact transient error is retried, within the service's overall deployment timeout. Each retry checks for newly created deployment IDs first; multiple candidates fail rather than following an arbitrary latest deployment. The same timeout budget covers initialization, readiness, and finite completion. Other provider errors fail immediately. No bootstrap workload is launched before the full desired configuration is applied.

Pending state is saved before configuration writes. Interrupted application retries the recorded service. Destructive external changes cause failure rather than name-based replacement.

Ordinary services must reach `SUCCESS` for the exact launched deployment ID; an explicitly enabled sleeping ordinary service may also report `SLEEPING`. Finite execution always requires `SUCCESS`, `deploymentStopped`, and every instance `EXITED`. Failed/crashed deployments are rejected even if instances have exited. A timeout blocks dependent services; it does not imply that the remote process was stopped. Cron publication requires the exact deployment's `SUCCESS` activation status and `Never` restart, but does not wait for its next scheduled process or provide a finite release prerequisite.

Existing PostgreSQL, Upstash, and Bunny publishers participate through `push-prereq`. They remain responsible for provider-specific ownership/output generation. Adapting those packages to the full site allocation contract belongs to the later template integration phase.

Retained images do not reverse database migrations. Rollback eligibility, migration compatibility, worker handover, and release recovery remain release policies composed through completion dependencies.

The core publishes projects/containers, including web apps and workers. Jobs, RabbitMQ, and Dashboard companions add workload-specific configuration. OpenObserve deployment remains outside these packages. See [live validation](live-validation.md) for verified behavior and limitations.
