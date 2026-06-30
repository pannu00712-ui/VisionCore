using Serilog;
using Serilog.Events;
using VisionCore.Core;
using VisionCore.Service;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VisionCore", "logs", "service-bootstrap-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 3)
    .CreateBootstrapLogger();

try
{
    Log.Information("VisionCore Service starting up (PID {Pid}).", Environment.ProcessId);

    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = ServiceConstants.ServiceName;
    });

    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.AddVisionCoreServices();
    builder.Services.AddHostedService<VisionCoreWorker>();

    var host = builder.Build();
    await host.RunAsync();

    Log.Information("VisionCore Service stopped cleanly.");
    return 0;
}
catch (Exception ex) when (ex is not OperationCanceledException
                           && ex.GetType().Name != "StopTheHostException")
{
    Log.Fatal(ex, "VisionCore Service terminated unexpectedly.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
