var builder = DistributedApplication.CreateBuilder(args);

// Cosmos DB backs the agent grain state (emulator when running locally).
var cosmos = builder.AddAzureCosmosDB("cosmos")
    .RunAsEmulator();

// Orleans cluster; single-silo development clustering for local runs.
var orleans = builder.AddOrleans("orleans")
    .WithDevelopmentClustering();

builder.AddProject<Projects.Agent42_Backend>("backend")
    .WithReference(orleans)
    .WithReference(cosmos)
    .WaitFor(cosmos);

builder.Build().Run();
