using Khors.Service;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPlatform();

using var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
