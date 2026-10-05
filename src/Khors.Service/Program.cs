using System.Reflection;
using Khors.Core.Diagnostics;
using Khors.Engines.Tun;
using Khors.Platform;
using Khors.Service;
using Khors.Service.Tun;
using Khors.Service.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (ServiceSetup.IsSetupCommand(args))
{
    return ServiceSetup.Run(args[0]);
}

var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddServicePlatform();
builder.Services.AddSingleton(new ServiceInfo(version, DateTimeOffset.UtcNow));
builder.Services.AddSingleton<ITunStarter>(sp => new TunEngine(new SecretMasker(), sp.GetService<IChildProcessGuard>()));
builder.Services.AddSingleton<TunController>();
builder.Services.AddHostedService<IpcServer>();

using var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
return 0;
