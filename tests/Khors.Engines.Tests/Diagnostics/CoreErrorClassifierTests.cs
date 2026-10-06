using System.Text;
using Khors.Core.Diagnostics;
using Khors.Engines.Diagnostics;
using Xunit;

namespace Khors.Engines.Tests.Diagnostics;

/// <summary>
/// Строки — из логов Xray 26.9.9 и sing-box 1.14.2 на локальном стенде (адреса заменены вымышленными);
/// тексты ошибок Windows — сообщения Winsock в том виде, в каком их выводит Go.
/// </summary>
public class CoreErrorClassifierTests
{
    private const string XrayDial = "2026/10/05 20:41:52.085452 [Info] [407998944] app/proxyman/outbound: app/proxyman/outbound: failed to process outbound traffic > "
        + "proxy/vless/outbound: failed to find an available destination > common/retry: [";

    private const string XrayDialEnd = "] > common/retry: all retry attempts failed";

    private const string SingBoxDial = "ERROR[0000] [909430771 0ms] connection: open connection to 198.51.100.20:443 using outbound/vless[proxy]: ";

    public static TheoryData<CoreKind, string, CoreProblem> Recognized => new()
    {
        // REALITY: сервер ответил как сайт-маскировка (неверный ключ, short id, SNI; sing-box с сервером Xray 26.9.8+).
        { CoreKind.Xray, "2026/10/05 20:42:25.260525 [Error] [3748635634] transport/internet/reality: REALITY: received real certificate (potential MITM or redirection)", CoreProblem.RealityRejected },
        { CoreKind.SingBox, SingBoxDial + "reality verification failed", CoreProblem.RealityRejected },

        // Запуск.
        { CoreKind.Xray, "Failed to start: main: failed to load config files: [stdin:] > infra/conf: failed to build outbound config with tag  > infra/conf: VLESS users: please add/set \"encryption\":\"none\" for every user", CoreProblem.ConfigRejected },
        { CoreKind.SingBox, "FATAL[0000] decode config at stdin: outbounds[0].uuid: invalid UUID", CoreProblem.ConfigRejected },
        { CoreKind.SingBox, "FATAL[0000] start service: start inbound/socks[0]: listen tcp 127.0.0.1:10808: bind: address already in use", CoreProblem.PortInUse },
        { CoreKind.SingBox, "FATAL[0000] start service: start inbound/tun[tun-in]: configure tun interface: Access is denied.", CoreProblem.TunUnavailable },
        { CoreKind.SingBox, "FATAL[0015] start service: start inbound/tun[tun-in]: configure tun interface: (create adapter: Cannot create a file when that file already exists. | open existing adapter: Element not found.)", CoreProblem.TunUnavailable },
        { CoreKind.SingBox, "+0300 2026-10-06 15:51:18 WARN inbound/tun[tun-in]: open interface take too much time to finish!", CoreProblem.TunUnavailable },
        { CoreKind.SingBox, "ERROR[0000] wintun: create adapter: The system cannot find the file specified.", CoreProblem.TunUnavailable },
        { CoreKind.Xray, "Failed to start: main: failed to start server > app/proxyman/inbound: failed to listen TCP on 10808 > transport/internet: failed to listen on address: 127.0.0.1:10808 > listen tcp 127.0.0.1:10808: bind: Only one usage of each socket address (protocol/network address/port) is normally permitted.", CoreProblem.PortInUse },

        // Подключение к серверу: Xray (Linux и Windows).
        { CoreKind.Xray, XrayDial + "dial tcp 198.51.100.20:443: connect: connection refused" + XrayDialEnd, CoreProblem.ConnectionRefused },
        { CoreKind.Xray, XrayDial + "dial tcp 198.51.100.20:443: connectex: No connection could be made because the target machine actively refused it." + XrayDialEnd, CoreProblem.ConnectionRefused },
        { CoreKind.Xray, XrayDial + "dial tcp 198.51.100.20:443: i/o timeout" + XrayDialEnd, CoreProblem.ConnectionTimeout },
        { CoreKind.Xray, XrayDial + "dial tcp 198.51.100.20:443: connectex: A connection attempt failed because the connected party did not properly respond after a period of time, or established connection failed because connected host has failed to respond." + XrayDialEnd, CoreProblem.ConnectionTimeout },
        { CoreKind.Xray, XrayDial + "read tcp 192.0.2.5:50123->198.51.100.20:443: wsarecv: An existing connection was forcibly closed by the remote host." + XrayDialEnd, CoreProblem.ConnectionReset },
        { CoreKind.Xray, XrayDial + "read tcp 192.0.2.5:50123->198.51.100.20:443: read: connection reset by peer" + XrayDialEnd, CoreProblem.ConnectionReset },
        { CoreKind.Xray, XrayDial + "dial tcp: lookup vpn.example.com: no such host" + XrayDialEnd, CoreProblem.ServerNotFound },
        { CoreKind.Xray, XrayDial + "dial tcp 198.51.100.20:443: connect: network is unreachable" + XrayDialEnd, CoreProblem.NetworkUnreachable },
        { CoreKind.Xray, XrayDial + "dial tcp 198.51.100.20:443: connectex: A socket operation was attempted to an unreachable network." + XrayDialEnd, CoreProblem.NetworkUnreachable },
        { CoreKind.Xray, XrayDial + "x509: certificate signed by unknown authority" + XrayDialEnd, CoreProblem.CertificateUntrusted },
        { CoreKind.Xray, XrayDial + "x509: certificate is valid for vpn.example.com, not cdn.example.net" + XrayDialEnd, CoreProblem.CertificateNameMismatch },
        { CoreKind.Xray, XrayDial + "tls: failed to verify certificate: x509: certificate has expired or is not yet valid: current time 2026-10-05T20:00:00+03:00 is after 2026-09-01T00:00:00Z" + XrayDialEnd, CoreProblem.CertificateExpired },
        { CoreKind.Xray, XrayDial + "remote error: tls: handshake failure" + XrayDialEnd, CoreProblem.TlsHandshakeFailed },

        // Подключение к серверу: sing-box.
        { CoreKind.SingBox, SingBoxDial + "dial tcp 198.51.100.20:443: connect: connection refused", CoreProblem.ConnectionRefused },
        { CoreKind.SingBox, "ERROR[0000] [1682497265 8ms] connection: open connection to 198.51.100.20:443 using outbound/hysteria2[proxy]: authentication failed, status code: 404", CoreProblem.AuthenticationFailed },
        { CoreKind.SingBox, "ERROR[0000] [3486445983 21ms] connection: open connection to 198.51.100.20:443 using outbound/hysteria2[proxy]: INTERNAL_ERROR (local): tls: failed to verify certificate: x509: certificate is valid for vpn.example.com, not cdn.example.net", CoreProblem.CertificateNameMismatch },
        { CoreKind.SingBox, "ERROR[0012] [2934 10s] connection: open packet connection to 198.51.100.20:53 using outbound/tuic[proxy]: timeout: no recent network activity", CoreProblem.ConnectionTimeout },
    };

    public static TheoryData<CoreKind, string> Ignored => new()
    {
        { CoreKind.Xray, "2026/10/05 20:39:54.869294 [Warning] core: Xray 26.9.9 started" },
        { CoreKind.Xray, "2026/10/05 20:39:40.638250 [Warning] common/errors: The feature Trojan (with no Flow, etc.) is deprecated, not recommended for using and might be removed. Please migrate to VLESS with Flow & Seed as soon as possible." },

        // Прямые соединения (локальная сеть) — не сервер профиля.
        { CoreKind.Xray, "2026/10/05 21:00:00.000000 [Info] [1] app/proxyman/outbound: app/proxyman/outbound: failed to process outbound traffic > proxy/freedom: failed to open connection to tcp:192.168.1.1:80 > dial tcp 192.168.1.1:80: connect: connection refused" },
        { CoreKind.SingBox, "ERROR[0003] [123 5ms] connection: open connection to 192.168.1.1:80 using outbound/direct[direct]: dial tcp 192.168.1.1:80: connect: connection refused" },

        // Обрыв уже работающего соединения и ошибки локального клиента (браузера).
        { CoreKind.Xray, "2026/10/05 21:00:00.000000 [Info] [2] app/proxyman/outbound: app/proxyman/outbound: failed to process outbound traffic > proxy/vless/outbound: connection ends > context canceled" },
        { CoreKind.Xray, "2026/10/05 21:00:00.000000 [Info] [3] proxy/http: failed to write response > write tcp 127.0.0.1:10809->127.0.0.1:50123: write: connection reset by peer" },
        { CoreKind.SingBox, "ERROR[0042] [4567 30s] inbound/http[http-in]: process connection from 127.0.0.1:50123: read tcp 127.0.0.1:10809->127.0.0.1:50123: connection reset by peer" },

        // Обычная работа адаптера TUN — не сбой TUN.
        { CoreKind.SingBox, "INFO[0001] inbound/tun[tun-in]: started at KHORS" },
        { CoreKind.SingBox, "INFO[0005] [8901 0ms] inbound/tun[tun-in]: inbound connection from 172.19.0.1:50123" },
        { CoreKind.SingBox, "ERROR[0060] [8901 55s] inbound/tun[tun-in]: process connection from 172.19.0.1:50123: context canceled" },
    };

    [Theory]
    [MemberData(nameof(Recognized))]
    public void RecognizesKnownErrors(CoreKind core, string line, CoreProblem expected) =>
        Assert.Equal(expected, CoreErrorClassifier.Classify(core, line));

    [Theory]
    [MemberData(nameof(Recognized))]
    public void RecognizesMaskedLines(CoreKind core, string line, CoreProblem expected)
    {
        // В буфер лога строки попадают уже замаскированными.
        var masked = new SecretMasker(Encoding.UTF8.GetBytes("classifier")).MaskText(line);

        Assert.Equal(expected, CoreErrorClassifier.Classify(core, masked));
    }

    [Theory]
    [MemberData(nameof(Ignored))]
    public void IgnoresNormalAndUnrelatedLines(CoreKind core, string line) =>
        Assert.Null(CoreErrorClassifier.Classify(core, line));

    [Fact]
    public void DiagnoseTakesLatestKnownProblem()
    {
        string[] tail =
        [
            XrayDial + "dial tcp 198.51.100.20:443: i/o timeout" + XrayDialEnd,
            "2026/10/05 20:42:25.260525 [Error] [1] transport/internet/reality: REALITY: received real certificate (potential MITM or redirection)",
            "2026/10/05 20:42:26.000000 [Warning] core: something unrelated",
        ];

        Assert.Equal(new CoreDiagnosis(CoreProblem.RealityRejected, CoreKind.Xray), CoreErrorClassifier.Diagnose(CoreKind.Xray, tail));
        Assert.Null(CoreErrorClassifier.Diagnose(CoreKind.Xray, ["[Warning] core: Xray 26.9.9 started"]));
    }

    [Fact]
    public void XrayRunsWithInfoButKeepsOnlyOutboundFailures()
    {
        var plan = CoreLogLevels.Plan(CoreKind.Xray, "warning");

        Assert.Equal("info", plan.Level);
        Assert.NotNull(plan.KeepLine);

        // Обычные строки info о каждом соединении содержат адреса сайтов — в буфер не попадают.
        Assert.False(plan.KeepLine("2026/10/05 20:41:36.726777 [Info] [2247073741] proxy/socks: TCP Connect request to tcp:www.example.org:443"));
        Assert.False(plan.KeepLine("2026/10/05 20:41:36.726791 [Info] [2247073741] app/dispatcher: default route for tcp:www.example.org:443"));
        Assert.False(plan.KeepLine("2026/10/05 20:41:36.726795 [Info] [2247073741] transport/internet/tcp: dialing TCP to tcp:198.51.100.20:443"));
        Assert.False(plan.KeepLine("2026/10/05 20:40:32.443198 [Info] [1287696847] proxy/vless/outbound: tunneling request to tcp:www.example.org:443 via 198.51.100.20:443"));
        Assert.False(plan.KeepLine("2026/10/05 20:40:31.445901 [Debug] app/log: Logger started"));
        Assert.False(plan.KeepLine("2026/10/05 21:00:00.000000 [Info] [3] proxy/http: failed to write response > write: connection reset by peer"));

        Assert.True(plan.KeepLine(XrayDial + "dial tcp 198.51.100.20:443: connect: connection refused" + XrayDialEnd));
        Assert.True(plan.KeepLine("2026/10/05 20:42:25.260525 [Error] [1] transport/internet/reality: REALITY: received real certificate (potential MITM or redirection)"));
        Assert.True(plan.KeepLine("2026/10/05 20:39:54.869294 [Warning] core: Xray 26.9.9 started"));
        Assert.True(plan.KeepLine("Failed to start: main: failed to load config files"));
    }

    [Theory]
    [InlineData(CoreKind.Xray, "info")]
    [InlineData(CoreKind.Xray, "debug")]
    [InlineData(CoreKind.Xray, "none")]
    [InlineData(CoreKind.SingBox, "warning")]
    [InlineData(CoreKind.SingBox, "info")]
    public void OtherLevelsAreUsedAsIs(CoreKind core, string level)
    {
        var plan = CoreLogLevels.Plan(core, level);

        Assert.Equal(level, plan.Level);
        Assert.Null(plan.KeepLine);
    }
}
