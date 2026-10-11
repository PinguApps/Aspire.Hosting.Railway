# Tests

Active xUnit contracts cover local behavior, immutable artifacts, scoped ownership, drift, repeatability, interrupted apply, process readiness, and diagnostics. Provider contracts use an in-memory GraphQL handler. The TypeScript fixture verifies the packed NuGet API. Live consumers remain external; safe evidence is recorded under docs.

`Features/VolumeRegions.feature` covers provider region aliases, configuration before volume creation, created-volume binding checks, deployment fallback rejection, persistent drift refusal, image/source replays, and the published assembly identity. Region defaults are materialized by the fake provider so the original missing explicit-region configuration fails the regression.
