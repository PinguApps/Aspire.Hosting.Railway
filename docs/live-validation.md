# Live validation

Validated on 2 October 2026 with .NET 10.0.401, Aspire CLI/SDK 13.6.0, and a packed `PinguApps.Aspire.Hosting.Railway` 1.0.0 package. The consumer was outside this repository at `V:/pin646-core-consumer`, with a fresh isolated NuGet cache for each package revision. No package was published.

The dedicated [PIN-646 Core Integration project](https://railway.com/project/c702abd1-b897-4c2d-a7d9-3468fad4271a) remains running for inspection. Production environment: `a8bcecee-5ff7-46db-a9a6-575b566a0876`. An environment-scoped project token and shared site marker were used; control-plane credentials and parameter files are excluded from this report and repository.

| Resource | Railway service ID | Exact successful deployment ID |
|---|---|---|
| Public web | `aad2ba48-6217-47a2-84ad-7c0eecce4668` | `3f073127-6449-4760-82b8-a96b10e4850b` |
| Private worker | `b0ccf384-87a0-4615-9dd3-3b353c7b5d0f` | `0af7514b-7cf6-411d-8399-9b9318023c99` |
| Amsterdam volume | `807d344d-3a4d-4c21-80b0-52890d01467d` | `935500cb-ef86-4cf3-8c75-50014dee153b` |
| Private registry pull | `3f1d2e79-6913-450a-828e-1c5ddd07dff9` | `49d9d0dc-8cd2-401a-8b12-0c4d66ad0fac` |
| Retained project image | `2048165b-c6cb-4ae1-9632-d2ef0f475a54` | `8eef163a-5bc0-47f1-ab9a-fd920d95ccab` |
| Pristine initialization | `806e601e-920b-4fbf-b952-f319439fc927` | `5fe6430d-4311-4721-ba14-310ac5ddd53b` |

The [public web endpoint](https://web-production-62a0b5.up.railway.app) returned HTTP 200. The final consumer deploy completed all 20 steps. Additional initialization/recovery probes remain in the same dedicated project; all eleven final services reported `SUCCESS`, one region, and one replica.

## Coverage and limits

| Options or behavior | Evidence |
|---|---|
| Project/environment/site/token inputs; project-token default | Live scoped deployment. Different current project, environment, site marker, or invalid current token failed at the read-only plan before any apply step. Explicit same-name current parameter values overrode cached prior values. |
| Container and project resources; `Image`, pre-attached SHA256 | Live retained container and `AddProject(...).PublishToRailway(...).PublishAsDockerFile()` deployment. Contract tests cover both registration orders and prefixed/unprefixed SHA256. No workload build/push steps remain. |
| Public domain, port, health check; private worker | Live generated HTTPS endpoint on port 80 with `/` readiness. Private services had no generated public domain. Published endpoint expressions resolve the private hostname and target port. |
| Region, volumes, one replica | Live SFO and Amsterdam services with persistent `/data` mounts. Amsterdam deployment metadata contained only `ams: { numReplicas: 1 }`; volume remained in `ams`. |
| `MemoryGB`, `VCpus` | Live web limits of 0.25 GB and 0.25 vCPU; unchanged readback did not cause redeployment. Invalid/non-finite limits are rejected in contract tests. |
| Runtime variables and `SealedVariables` | Live `TEST_CONFIG=core-integration`. Sealed test variable absent from API readback; provider control-plane token variable absent. Desired secret hash reconciliation/redaction covered by contract tests. |
| Registry username/password | Live pull of an existing private ACR image using secret Aspire parameters. Registry credentials remained control-plane configuration. No Azure resources were created or changed. A private GHCR artifact/credential was unavailable; GHCR-specific pull was not live-tested. Railway plan eligibility is an external prerequisite. |
| Unchanged reconciliation | Repeated live deployment preserved all eleven exact deployment IDs, service identities, and volume identities. |
| Whole-target drift refusal | Live later-resource mount mismatch rejected the entire plan before apply; a proposed earlier web variable change was not written. Cached scope/identity, ownership, domain and public TCP drift refusal have focused contract tests. |
| Ownership modes, explicit adoption, `ExistingServiceId`, service name | Contract tests cover each policy, explicit proven adoption, cached identity mismatch and missing services. Live tests used the default create-or-adopt policy with package-owned services. Unmarked production infrastructure was not adopted. |
| Partial apply and ambiguous responses | Injected transport contract tests prove service reuse after creation/patch/deploy failures and recovery of the exact creation claim. Multiple new initial deployment IDs are rejected. Live pristine initialization proves bounded retry of the provider's exact `Deployment not found` transient. |
| `StartCommand`, restart policy/retries, finite completion, cron and dependencies | Core contract tests plus the companion Jobs package's separate packed live consumer: successful stopped `EXITED` process, exit-7 `CRASHED` instance despite outer `SUCCESS`, dependent web gating, timeout and real scheduled execution. See that repository's live report for its exact evidence. |
| RabbitMQ connection expressions and private dependencies | Core reference-resolution tests plus companion RabbitMQ live AMQP/private-host/special-character credential tests. Its report records broker/proxy and durable-queue verification. |
| `CustomDomains` | Configuration/validation and drift contracts only. No external DNS zone was configured; DNS and TLS readiness remain operator prerequisites. |
| `SleepApplication` | Configuration and accepting an explicitly sleeping ordinary service on repeat deployment are contract-tested; finite jobs cannot pass in SLEEPING. Actual inactivity/sleep/wake timing was not observed. |
| Explicit Bearer account mode | Available only with expected project/workspace identity. The mandatory live consumer used project-token mode; account-mode deployment was not live-tested. |
| Credential rotation | Live invalid current token overrides cached valid token and is refused. A replacement valid scoped token was not generated solely for this test. Registry/sealed fingerprint changes are contract-tested. |
| TypeScript DTOs and exports | Packed-NuGet fixture restored generated SDK code, typechecked, and generated publish/deploy graphs with web-to-worker ordering. TypeScript did not separately deploy live infrastructure. |

Normal validation: 61 non-live tests passed, with a warning-free Release build. The optional live scope test also passed against the allocated project/environment/site. The packed TypeScript gate passed using the same source. Secrets, raw deployment state and provider error payloads are not committed.

## Repository setup

- Allow the organization Blacksmith runner used by the copied workflows.
- Configure `NUGET_USER` and a NuGet trusted publishing policy for this repository's `publish.yml` workflow. Initial package publication remains a maintainer action.
- Optional read-only live scope CI needs `RAILWAY_API_TOKEN`, `RAILWAY_PROJECT_ID`, `RAILWAY_ENVIRONMENT_ID`, and `RAILWAY_SITE_KEY`. The project must already contain the matching shared `PINGUAPPS_SITE_KEY` marker. This CI check verifies scope; it does not recreate the full live consumer.
- AppHost deployments need an environment-scoped project token and explicit current allocation parameters. Private registry credentials and runtime secrets are separate inputs. No deployment credentials are required for ordinary unit tests or the packed TypeScript gate.


