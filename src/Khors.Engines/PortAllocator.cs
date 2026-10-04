using System.Net;
using System.Net.Sockets;

namespace Khors.Engines;

/// <summary>Выбор свободных локальных портов для входов ядра.</summary>
public static class PortAllocator
{
    /// <summary>
    /// Для каждого желаемого порта возвращает его же, если он свободен на 127.0.0.1, иначе — свободный порт от ОС.
    /// Порты в результате различны. Между проверкой и запуском ядра порт может занять другой процесс —
    /// тогда ядро не стартует, и запуск повторяется.
    /// </summary>
    public static int[] Allocate(params int?[] preferred)
    {
        ArgumentNullException.ThrowIfNull(preferred);

        var listeners = new List<TcpListener>(preferred.Length);
        try
        {
            foreach (var port in preferred)
            {
                listeners.Add(TryListen(port) ?? TryListen(0)!);
            }

            return [.. listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port)];
        }
        finally
        {
            foreach (var listener in listeners)
            {
                listener.Stop();
            }
        }
    }

    private static TcpListener? TryListen(int? port)
    {
        if (port is null)
        {
            return null;
        }

        var listener = new TcpListener(IPAddress.Loopback, port.Value);
        listener.Server.ExclusiveAddressUse = true;
        try
        {
            listener.Start();
            return listener;
        }
        catch (SocketException) when (port != 0)
        {
            listener.Stop();
            return null;
        }
    }
}
