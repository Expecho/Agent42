var builder = DistributedApplication.CreateBuilder(args);

// Cosmos DB backs the agent grain state (emulator when running locally).
var cosmos = builder.AddAzureCosmosDB("cosmos")
    .RunAsEmulator();

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
