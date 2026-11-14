using CopilotTeamsBridge.Worker;
using CopilotTeamsBridge.Worker.Discovery;
using Microsoft.Extensions.Hosting;

if (ChatDiscoveryCommand.IsRequested(args))
    return await ChatDiscoveryCommand.RunAsync(args, Console.Out, Console.Error, CancellationToken.None);

var builder = TeamsBridgeServiceRegistration.CreateBuilder(args);
await builder.Build().RunAsync();
return 0;
