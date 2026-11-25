using FlowMind.Infrastructure;
using FlowMind.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Runs as a Windows Service in production (sc create / New-Service), separate from the IIS app pool.
builder.Services.AddWindowsService(o => o.ServiceName = "FlowMind.Worker");
builder.Services.AddFlowMindInfrastructure(builder.Configuration);
builder.Services.AddHostedService<JobPollingWorker>();

builder.Build().Run();
