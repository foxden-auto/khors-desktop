namespace Khors.Platform.Windows.Proxy;

/// <summary>Настройки прокси WinINet для подключения по локальной сети (LAN) текущего пользователя.</summary>
/// <param name="Flags">Комбинация PROXY_TYPE_*.</param>
internal sealed record WinInetProxyState(int Flags, string? ProxyServer, string? ProxyBypass, string? AutoConfigUrl)
{
    public const int ProxyTypeDirect = 0x1;
    public const int ProxyTypeProxy = 0x2;
    public const int ProxyTypeAutoProxyUrl = 0x4;
    public const int ProxyTypeAutoDetect = 0x8;

    public bool UsesProxyServer => (Flags & ProxyTypeProxy) != 0;
}

/// <summary>Чтение и запись настроек WinINet. Отдельный слой, чтобы логику отката проверять без изменения системы.</summary>
internal interface IWinInetProxySettings
{
    WinInetProxyState Read();

    void Write(WinInetProxyState state);
}
