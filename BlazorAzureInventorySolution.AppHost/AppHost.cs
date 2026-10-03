var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.BlazorAzureInventory>("blazorazureinventory");

builder.Build().Run();
