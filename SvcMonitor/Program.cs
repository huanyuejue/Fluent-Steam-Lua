using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

Host.CreateDefaultBuilder(args)
    .UseContentRoot(AppContext.BaseDirectory)
    .UseWindowsService(options =>
    {
        options.ServiceName = "SvcMonitor";
    })
    .ConfigureServices(services =>
    {
        services.AddHostedService<SvcMonitor.GameMonitorService>();
    })
    .Build()
    .Run();
