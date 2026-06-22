using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VisionCore.Core;

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "VisionCore Service";
    })
    .ConfigureServices(services =>
    {
        services.AddVisionCoreServices();
    })
    .Build();

await host.RunAsync();