var builder = DistributedApplication.CreateBuilder(args);

// Cosmos DB backs the agent grain state (emulator when running locally).
var cosmos = builder.AddAzureCosmosDB("cosmos")
    .RunAsEmulator();

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

builder.AddProject<Projects.Agent42_Backend>("backend")
    .WithReference(orleans)
    .WithReference(cosmos)
    .WithReference(blobs)
    .WaitFor(cosmos)
    .WaitFor(blobs);

builder.Build().Run();
