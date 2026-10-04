using System.Text.Json.Serialization;

namespace Khors.Core.Profiles;

/// <summary>Транспорт. QUIC (только sing-box) добавляется в этапе 2.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TcpTransport), "tcp")]
[JsonDerivedType(typeof(WsTransport), "ws")]
[JsonDerivedType(typeof(GrpcTransport), "grpc")]
[JsonDerivedType(typeof(HttpUpgradeTransport), "httpupgrade")]
[JsonDerivedType(typeof(XhttpTransport), "xhttp")]
public abstract record TransportSettings;

/// <summary>TCP (в Xray — RAW), опционально с маскировкой заголовка под HTTP.</summary>
public sealed record TcpTransport : TransportSettings
{
    /// <summary><c>none</c> или <c>http</c>.</summary>
    public string HeaderType { get; init => field = value ?? "none"; } = "none";

    public string? Host { get; init; }

    public string? Path { get; init; }

    public override string ToString() => $"TcpTransport {{ HeaderType = {HeaderType} }}";
}

public sealed record WsTransport : TransportSettings
{
    /// <summary>Путь, может содержать параметр early data (<c>/path?ed=2048</c>).</summary>
    public string Path { get; init => field = value ?? "/"; } = "/";

    public string? Host { get; init; }

    public override string ToString() => "WsTransport";
}

public sealed record GrpcTransport : TransportSettings
{
    public string ServiceName { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary><c>gun</c> или <c>multi</c>.</summary>
    public string Mode { get; init => field = value ?? "gun"; } = "gun";

    public string? Authority { get; init; }

    public override string ToString() => $"GrpcTransport {{ Mode = {Mode} }}";
}

public sealed record HttpUpgradeTransport : TransportSettings
{
    public string Path { get; init => field = value ?? "/"; } = "/";

    public string? Host { get; init; }

    public override string ToString() => "HttpUpgradeTransport";
}

public sealed record XhttpTransport : TransportSettings
{
    public string Path { get; init => field = value ?? "/"; } = "/";

    public string? Host { get; init; }

    /// <summary><c>auto</c>, <c>packet-up</c>, <c>stream-up</c> или <c>stream-one</c>.</summary>
    public string Mode { get; init => field = value ?? "auto"; } = "auto";

    /// <summary>Дополнительные параметры XHTTP (<c>extra</c>) — JSON-объект как строка, без интерпретации.</summary>
    public string? Extra { get; init; }

    public override string ToString() => $"XhttpTransport {{ Mode = {Mode} }}";
}
