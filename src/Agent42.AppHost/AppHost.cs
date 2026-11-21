var builder = DistributedApplication.CreateBuilder(args);

// Cosmos DB backs the agent grain state (emulator when running locally).
var cosmos = builder.AddAzureCosmosDB("cosmos")
    .RunAsEmulator(e => e.WithLifetime(ContainerLifetime.Persistent));

// Seed the emulator with the same database and containers that infra/modules/cosmos.bicep
// pre-creates in prod. Both Orleans providers run with IsResourceCreationEnabled = false
// (prod's account has disableLocalAuth, so their AAD auto-create path 403s), so without
// these declarations the empty emulator has no containers and the durable reminder service
// fails at silo startup. Partition key /PartitionKey matches what Orleans.Persistence.Cosmos
// and Orleans.Reminders.Cosmos expect. The doc-chunks vector container is deliberately omitted:
// the emulator can't do vector search, so local runs use the in-memory document index instead.
var cosmosDb = cosmos.AddCosmosDatabase("agent42");
cosmosDb.AddContainer("agent-state", "/PartitionKey");
cosmosDb.AddContainer("reminders", "/PartitionKey");

// Blob storage backs image attachments (Azurite emulator when running locally). Note: a local
// full-stack run against the real model still can't deliver images to it — Azure OpenAI can't
// fetch a localhost SAS URL — but upload, display, and history all work end to end.
var blobs = builder.AddAzureStorage("storage")
    .RunAsEmulator()
    .AddBlobs("blobs");

// Orleans cluster; single-silo development clustering for local runs.
var orleans = builder.AddOrleans("orleans")
    .WithDevelopmentClustering();

// Self-hosted SearXNG backs the web_search tool (ADR 0011). In production it is a sidecar container
// in the same Container App, reached over localhost; here it is its own container, so the backend gets
// its address through configuration rather than assuming localhost. Port 8888, not SearXNG's default
// 8080, which the backend's own ingress already owns in prod — kept identical here so local and prod
// agree. The image is the same thin Dockerfile that is built and pushed by the deploy workflow.
var searxng = builder.AddDockerfile("searxng", "../../infra/searxng")
    .WithHttpEndpoint(targetPort: 8888, name: "http")
    .WithEnvironment("SEARXNG_PORT", "8888")
    // The container's entrypoint reads the port from SEARXNG_PORT (server.port in settings.yml is
    // inert for the bind), and refuses to start without a secret.
    .WithEnvironment("SEARXNG_SECRET", "local-dev-only-not-a-real-secret");

// The /searxng/ subpath the backend proxies the UI under (ADR 0059), so the served HTML prefixes its
// own asset URLs. Unlike production — where the sidecar shares the backend's network namespace and
// the base URL can name the public domain — here SearXNG is its own container on an Aspire-assigned
// port, so the base URL must name THAT endpoint: it is the authority the proxy dials, and the proxy
// only reduces a redirect Location to a root-relative path when its authority is one it recognises.
searxng.WithEnvironment("SEARXNG_BASE_URL", ReferenceExpression.Create($"{searxng.GetEndpoint("http")}/searxng/"));

builder.AddDotnetProject("backend", "../Agent42.Backend/Agent42.Backend.csproj")
    .WithReference(orleans)
    .WithReference(cosmos)
    .WithReference(blobs)
    .WithEnvironment("WebSearch__Endpoint", searxng.GetEndpoint("http"))
    // The owner tier (ADR 0062/0121). REQUIRED here, and this is the environment that is easy to forget:
    // this path runs the real Agent42.Backend project, so it loads the real
    // src/Agent42.Backend/appsettings.json, whose Access:Owners are the production ids. Without this
    // override the gate would 404 /dashboard and /logs for the "local-dev" identity every local
    // full-stack request carries, which looks exactly like the feature being broken. Env vars override
    // array items by index, so entry 0 becomes local-dev and entry 1 is blanked (blank is ignored).
    .WithEnvironment("Access__Owners__0", "local-dev")
    .WithEnvironment("Access__Owners__1", "")
    .WaitFor(cosmos)
    .WaitFor(blobs)
    .WaitFor(searxng);

builder.Build().Run();
