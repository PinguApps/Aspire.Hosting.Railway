# TypeScript AppHost Fixture

This fixture validates the supported TypeScript AppHost authoring path for `PinguApps.Aspire.Hosting.Railway`.

Generated `.aspire/modules/` content is intentionally not checked in. Validation must regenerate it through Aspire CLI commands such as `aspire restore` before running TypeScript checks.

`aspire.config.json` references package version `1.0.0`. `eng/Validate-TypeScriptAppHostPackage.ps1` packs this checkout into an isolated NuGet cache, regenerates the bindings, typechecks this fixture and checks the publish/deploy pipeline graphs. The gate performs no live deployment.
