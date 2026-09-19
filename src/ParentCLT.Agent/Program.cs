using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ParentCLT.Agent.Services;

if (args.Length > 0 && args[0].Equals("restore-dns", StringComparison.OrdinalIgnoreCase))
{
    using var lf = LoggerFactory.Create(b => b.AddConsole());
    var store = new ConfigStore();
    new DnsManager(lf.CreateLogger<DnsManager>(), store).RestoreOriginalDns();
    return;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddWindowsService(o => o.ServiceName = "ParentCLT Agent");
builder.Services.AddHostedService<PolicyWorker>();

var host = builder.Build();
try
{
    await host.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ParentCLT Agent fatal: {ex}");
    throw;
}