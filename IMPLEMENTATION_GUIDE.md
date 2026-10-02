# Implementation guide

Keep standard Aspire project/container resources as the resources of record. The target holds infrastructure parameters; service annotations declare provider settings. `Management/` owns sanitized GraphQL transport. `Deployment/` owns ownership, drift, reconciliation, and readiness. The pipeline resolves Aspire expressions and state. Guest APIs use explicit exports and DTOs.

Behavior changes require active contracts, the packed TypeScript gate, and an external packed consumer in a dedicated environment when provider integration changes. Never commit credentials, deployment state, or live consumer output.
