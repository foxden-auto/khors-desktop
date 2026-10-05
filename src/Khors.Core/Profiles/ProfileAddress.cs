using System.Net;

namespace Khors.Core.Profiles;

/// <summary>Подстановка заранее разрешённого IP сервера в профиль.</summary>
public static class ProfileAddress
{
    /// <summary>
    /// Профиль с IP вместо домена сервера. Имя, которое ядро взяло бы из адреса, сохраняется явно: SNI TLS,
    /// заголовок Host у WebSocket/HTTPUpgrade/XHTTP/HTTP-маскировки, authority gRPC. Нужно для цепочки TUN → Xray
    /// (ROADMAP 3.3): адрес сервера исключается из туннеля, а разрешение имени Xray внутри TUN замкнулось бы на сам туннель.
    /// Адрес уже IP — профиль без изменений.
    /// </summary>
    public static Profile WithResolvedHost(Profile profile, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(address);

        var host = profile.Server.Host;
        if (IPAddress.TryParse(host, out _))
        {
            return profile;
        }

        return profile with
        {
            Server = profile.Server with { Host = address.ToString() },
            Security = profile.Security is TlsSecurity { Sni: null or "" } tls ? tls with { Sni = host } : profile.Security,
            Transport = profile.Transport switch
            {
                WsTransport { Host: null or "" } ws => ws with { Host = host },
                HttpUpgradeTransport { Host: null or "" } upgrade => upgrade with { Host = host },
                XhttpTransport { Host: null or "" } xhttp => xhttp with { Host = host },
                GrpcTransport { Authority: null or "" } grpc => grpc with { Authority = host },
                TcpTransport { HeaderType: "http", Host: null or "" } tcp => tcp with { Host = host },
                var other => other,
            },
        };
    }
}
