using Khors.Core.Storage;
using Khors.Ipc;
using Khors.Service.Tun;

namespace Khors.Service.Ipc;

/// <summary>Сведения о запущенной службе.</summary>
public sealed record ServiceInfo(string Version, DateTimeOffset StartedAt);

/// <summary>
/// Обработка команд одного соединения. Принимаются только команды протокола (CLAUDE.md, правило 8)
/// и только после рукопожатия с той же версией протокола; параметры проверяются (профиль — разбором модели,
/// уровень лога — по списку). Соединение закрылось — включённый им TUN выключается.
/// </summary>
public sealed class ServiceRequestHandler(ServiceInfo info, TunController tun, Action<IpcPayload> sendEvent) : IAsyncDisposable
{
    private static readonly HashSet<string> s_logLevels = ["debug", "info", "warning", "error", "none"];

    private bool _greeted;

    public async Task<IpcPayload> HandleAsync(IpcPayload request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        switch (request)
        {
            case HelloRequest hello when hello.ProtocolVersion != IpcProtocol.Version:
                return new ErrorResponse(IpcErrorCode.ProtocolMismatch, IpcProtocol.Version);
            case HelloRequest:
                _greeted = true;
                return new HelloResponse(IpcProtocol.Version, info.Version);
            case not HelloRequest when !_greeted:
                return new ErrorResponse(IpcErrorCode.HelloRequired);
            case GetStatusRequest:
                return new ServiceStatusResponse(info.Version, info.StartedAt);
            case StartTunRequest start:
                if (!s_logLevels.Contains(start.LogLevel) || StorageJson.ParseProfile(start.Profile) is not { } profile)
                {
                    return new ErrorResponse(IpcErrorCode.BadRequest);
                }

                return await tun.StartAsync(this, profile, start.LogLevel, sendEvent, cancellationToken).ConfigureAwait(false);
            case GetTunTrafficRequest:
                return await tun.ReadTrafficAsync(this, cancellationToken).ConfigureAwait(false);
            case StopTunRequest:
                await tun.StopAsync(this).ConfigureAwait(false);
                return new OkResponse();
            default:
                // Ответы и события службы клиент присылать не должен.
                return new ErrorResponse(IpcErrorCode.UnknownCommand);
        }
    }

    public ValueTask DisposeAsync() => new(tun.StopAsync(this));
}
