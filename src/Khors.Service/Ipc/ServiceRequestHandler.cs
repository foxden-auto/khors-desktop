using Khors.Ipc;

namespace Khors.Service.Ipc;

/// <summary>Сведения о запущенной службе.</summary>
public sealed record ServiceInfo(string Version, DateTimeOffset StartedAt);

/// <summary>
/// Обработка команд одного соединения. Принимаются только команды протокола (CLAUDE.md, правило 8)
/// и только после рукопожатия с той же версией протокола; ответы — только из контракта <see cref="IpcPayload"/>.
/// </summary>
public sealed class ServiceRequestHandler(ServiceInfo info)
{
    private bool _greeted;

    public IpcPayload Handle(IpcPayload request)
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
            default:
                // Ответы и события службы клиент присылать не должен.
                return new ErrorResponse(IpcErrorCode.UnknownCommand);
        }
    }
}
