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

builder.AddProject<Projects.Agent42_Backend>("backend")
    .WithReference(orleans)
    .WithReference(cosmos)
    .WithReference(blobs)
    .WithEnvironment("WebSearch__Endpoint", searxng.GetEndpoint("http"))
    // Forward the context.dev scrape key (ADR 0027) from the AppHost's own config/user-secrets so a local
    // full-stack run can exercise the real fetcher. Absent is fine — it degrades to a per-call failure,
    // not a startup throw (it is deliberately NOT on the availability path, unlike SEARXNG_SECRET above).
    .WithEnvironment("ContextScrape__ApiKey", builder.Configuration["ContextScrape:ApiKey"] ?? "")
    // Google Calendar (ADR 0053). Forwarded the same way as the context.dev key: optional, so an
    // empty value is a working local run with the calendar tools reporting "not connected".
    .WithEnvironment("GoogleCalendar__ClientId", builder.Configuration["GoogleCalendar:ClientId"] ?? "")
    .WithEnvironment("GoogleCalendar__ClientSecret", builder.Configuration["GoogleCalendar:ClientSecret"] ?? "")
    .WaitFor(cosmos)
    .WaitFor(blobs)
    .WaitFor(searxng);

builder.Build().Run();
