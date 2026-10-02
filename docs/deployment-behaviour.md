# Deployment behavior

The plan runs after `process-parameters` and before `deploy-prereq`, validating scope, ownership, cache, volumes, and domains across the target without writes. Each service repeats live checks before mutation.

Settings and runtime variables use a configuration patch with `skipDeploys`, followed by one explicit deployment whose returned ID is tracked. Repeat deployment compares desired live settings, variables, secret fingerprints, volume/domain identities, and resource limits. Unchanged ordinary services reuse their successful deployment; finite release jobs explicitly execute again.

Pending state is saved before configuration writes. Interrupted application retries the recorded service. Destructive external changes cause failure rather than name-based replacement.

Existing PostgreSQL, Upstash, and Bunny publishers participate through `push-prereq`. They remain responsible for provider-specific ownership/output generation. Adapting those packages to the full site allocation contract belongs to the later template integration phase.

Retained images do not reverse database migrations. Rollback eligibility, migration compatibility, worker handover, and release recovery remain release policies composed through completion dependencies.
