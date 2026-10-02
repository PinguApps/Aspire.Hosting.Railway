# TypeScript AppHost

Copy the [sample](../samples/TypeScriptAppHost), then run `aspire restore`, `npm ci`, and `npm run typecheck`. Explicitly declare the core package alongside companions so shared target exports are generated.

Declare local workload resources outside the publishing guard. Add target parameters and call `publishToRailway` inside `if (await builder.executionContext().isPublishMode())`, as the [README](../README.md#typescript-apphost) shows. This avoids requiring deployment credentials during local runs.

Read current private inputs and pass them as `builder.addParameter(name, { value: currentValue, secret: true })` for credentials; non-secret IDs/site keys omit `secret`. Updating configuration alone may reuse old cached parameters. Keep current values outside source control; never set `publishValueAsDefault` for secrets.

Run `aspire deploy --non-interactive` against the pre-created scoped allocation. The [packed fixture](../tests/Aspire.Hosting.Railway/Fixtures/TypeScriptAppHost) exercises generated NuGet exports rather than mocked TypeScript declarations.
