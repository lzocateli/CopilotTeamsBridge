using CopilotTeamsBridge.Worker;
using Microsoft.Extensions.Hosting;

var builder = TeamsBridgeServiceRegistration.CreateBuilder(args);
await builder.Build().RunAsync();
