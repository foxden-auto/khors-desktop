using System.Reflection;
using Khors.Core.Diagnostics;
using Khors.Engines.Geo;
using Khors.Engines.Tun;
using Khors.Platform;
using Khors.Service;
using Khors.Engines;
using Khors.Engines.Cores;
using Khors.Service.Cores;
using Khors.Service.Geo;
using Khors.Service.KillSwitch;
using Khors.Service.Tun;
using Khors.Service.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (ServiceSetup.IsSetupCommand(args))
{
    return ServiceSetup.Run(args[0]);
}

var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddServicePlatform();
builder.Services.AddSingleton(new ServiceInfo(version, DateTimeOffset.UtcNow));
builder.Services.AddSingleton<ITunStarter>(sp =>
{
    var adapters = sp.GetService<ITunAdapterCleanup>();
    return new TunEngine(new SecretMasker(), sp.GetService<IChildProcessGuard>(), sp.GetRequiredService<ServicePaths>().GeoDirectory)
    {
        // Битый адаптер KHORS мешает sing-box создать новый — убираем его перед повтором (ROADMAP 4.4).
        RemoveStaleAdapter = adapters is null ? null : () => adapters.RemoveAdapters(KillSwitchController.TunnelInterface),
    };
});
builder.Services.AddSingleton(sp => new KillSwitchController(
    sp.GetService<IKillSwitch>(),
    Path.Combine(sp.GetRequiredService<ServicePaths>().DataDirectory, "killswitch.json"),
    // Пути ядер — при каждой смене фильтров: после обновления ядра (ROADMAP 5.2) разрешение нужно новому файлу.
    () => [.. new[] { CoreKind.SingBox, CoreKind.Xray }.Select(k => CoreLocator.Find(k)).OfType<string>()],
    sp.GetRequiredService<ILogger<KillSwitchController>>()));
builder.Services.AddSingleton(sp => new CoreUpdater(new CoreStore(sp.GetRequiredService<ServicePaths>().CoresDirectory)));
builder.Services.AddSingleton(sp => new ServiceCores(sp.GetRequiredService<CoreUpdater>()));
builder.Services.AddSingleton<TunController>();
builder.Services.AddSingleton(sp => new NetworkReset(sp.GetRequiredService<TunController>(), sp.GetRequiredService<KillSwitchController>(), sp.GetService<ITunAdapterCleanup>()));
builder.Services.AddSingleton(sp => new GeoDatabaseUpdater(sp.GetRequiredService<ServicePaths>().GeoDirectory));
builder.Services.AddSingleton<ServiceGeo>();
builder.Services.AddHostedService<IpcServer>();

using var host = builder.Build();

// Обновлённые ядра службы — из её каталога (создаётся и защищается при сборке ServicePaths).
CoreLocator.Store = host.Services.GetRequiredService<CoreUpdater>().Store;

// Сверка фильтров Kill Switch с настройкой до приёма команд (CLAUDE.md, правило 9).
host.Services.GetRequiredService<KillSwitchController>().Initialize();
await host.RunAsync().ConfigureAwait(false);
return 0;
