import { createBuilder, railwayOwnershipMode, railwayRestartPolicy, railwayAuthenticationMode } from "./.aspire/modules/aspire.mjs";
const builder = await createBuilder();
let web = await builder.addContainer("web", "traefik/whoami");
let worker = await builder.addContainer("worker", "traefik/whoami");
let source = await builder.addContainer("source", "traefik/whoami");
if (await builder.executionContext().isPublishMode()) {
  const project = await builder.addParameter("railway-project-id", { value: process.env.Parameters__railway_project_id });
  const environment = await builder.addParameter("railway-environment-id", { value: process.env.Parameters__railway_environment_id });
  const token = await builder.addParameter("railway-api-token", { value: process.env.Parameters__railway_api_token, secret: true });
  const site = await builder.addParameter("site-key", { value: process.env.Parameters__site_key });
  const target = await builder.addRailwayTarget("railway", project, environment, token, site, { authenticationMode: railwayAuthenticationMode.projectToken });
  web = await web.publishToRailway(target, { image: "traefik/whoami@sha256:c4717a8d1f0134a7444e24f881160e033991f23027c6c5a9a3f8fd22e70d1d44", ownershipMode: railwayOwnershipMode.createOrAdopt, restartPolicy: railwayRestartPolicy.onFailure, port: 80, publicDomain: true, healthCheckPath: "/", volumes: [{ mountPath: "/data" }], sealedVariables: [] });
  const hostname = await web.getRailwayPrivateHostname();
  worker = await worker.publishToRailway(target, { image: "traefik/whoami@sha256:c4717a8d1f0134a7444e24f881160e033991f23027c6c5a9a3f8fd22e70d1d44" });
  worker = await worker.withEnvironment("WEB_HOST", hostname);
  worker = await worker.withRailwayDeploymentDependency(web);
  source = await source.publishToRailway(target, { build: { contextPath: ".", dockerfilePath: "Dockerfile", buildArguments: [{ name: "RELEASE_LABEL", value: "fixture" }] }, port: 80 });
  const sourceDeployment = await source.getRailwayDeploymentId();
  const sourceFingerprint = await source.getRailwayBuildFingerprint();
  const sourceDigest = await source.getRailwayImageDigest();
  const webImage = await web.getRailwayImage();
  worker = await worker.withEnvironment("SOURCE_DEPLOYMENT", sourceDeployment);
  worker = await worker.withEnvironment("SOURCE_FINGERPRINT", sourceFingerprint);
  worker = await worker.withEnvironment("SOURCE_DIGEST", sourceDigest);
  worker = await worker.withEnvironment("WEB_IMAGE", webImage);
  worker = await worker.withRailwayDeploymentDependency(source);
}
const app = await builder.build();
await app.run();
