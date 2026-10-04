using Khors.Core.Profiles;
using Xunit;

namespace Khors.Core.Tests.Profiles;

public class ProfileValidatorTests
{
    public static TheoryData<Profile> ValidProfiles => TestProfiles.All;

    [Theory]
    [MemberData(nameof(ValidProfiles))]
    public void ValidProfileHasNoErrors(Profile profile) =>
        Assert.DoesNotContain(ProfileValidator.Validate(profile), i => i.Severity == ProfileIssueSeverity.Error);

    public static TheoryData<string, ProfileIssueCode, ProfileIssueSeverity> BrokenProfiles => new()
    {
        { "name-empty", ProfileIssueCode.NameEmpty, ProfileIssueSeverity.Error },
        { "host-empty", ProfileIssueCode.HostEmpty, ProfileIssueSeverity.Error },
        { "host-invalid", ProfileIssueCode.HostInvalid, ProfileIssueSeverity.Error },
        { "port-zero", ProfileIssueCode.PortOutOfRange, ProfileIssueSeverity.Error },
        { "port-too-big", ProfileIssueCode.PortOutOfRange, ProfileIssueSeverity.Error },
        { "id-empty", ProfileIssueCode.IdEmpty, ProfileIssueSeverity.Error },
        { "id-too-long", ProfileIssueCode.IdInvalid, ProfileIssueSeverity.Error },
        { "alter-id-negative", ProfileIssueCode.AlterIdNegative, ProfileIssueSeverity.Error },
        { "trojan-password-empty", ProfileIssueCode.PasswordEmpty, ProfileIssueSeverity.Error },
        { "ss-method-empty", ProfileIssueCode.MethodEmpty, ProfileIssueSeverity.Error },
        { "flow-unknown", ProfileIssueCode.FlowUnknown, ProfileIssueSeverity.Error },
        { "flow-over-ws", ProfileIssueCode.FlowRequiresTcp, ProfileIssueSeverity.Error },
        { "flow-without-tls", ProfileIssueCode.FlowRequiresTlsOrReality, ProfileIssueSeverity.Error },
        { "flow-with-mux", ProfileIssueCode.MuxIncompatibleWithFlow, ProfileIssueSeverity.Error },
        { "mux-concurrency", ProfileIssueCode.MuxConcurrencyOutOfRange, ProfileIssueSeverity.Error },
        { "grpc-mode", ProfileIssueCode.TransportModeUnknown, ProfileIssueSeverity.Error },
        { "xhttp-mode", ProfileIssueCode.TransportModeUnknown, ProfileIssueSeverity.Error },
        { "xhttp-extra", ProfileIssueCode.XhttpExtraInvalid, ProfileIssueSeverity.Error },
        { "reality-sni-empty", ProfileIssueCode.RealitySniEmpty, ProfileIssueSeverity.Error },
        { "reality-key-short", ProfileIssueCode.RealityPublicKeyInvalid, ProfileIssueSeverity.Error },
        { "reality-key-not-base64", ProfileIssueCode.RealityPublicKeyInvalid, ProfileIssueSeverity.Error },
        { "reality-sid-odd", ProfileIssueCode.RealityShortIdInvalid, ProfileIssueSeverity.Error },
        { "reality-sid-not-hex", ProfileIssueCode.RealityShortIdInvalid, ProfileIssueSeverity.Error },
        { "reality-sid-too-long", ProfileIssueCode.RealityShortIdInvalid, ProfileIssueSeverity.Error },
        { "reality-over-ws", ProfileIssueCode.RealityTransportUnsupported, ProfileIssueSeverity.Error },
        { "tls-insecure", ProfileIssueCode.InsecureTls, ProfileIssueSeverity.Warning },
        { "unknown-params", ProfileIssueCode.UnknownParameters, ProfileIssueSeverity.Warning },
    };

    [Theory]
    [MemberData(nameof(BrokenProfiles))]
    public void BrokenProfileReportsIssue(string scenario, ProfileIssueCode code, ProfileIssueSeverity severity)
    {
        var issues = ProfileValidator.Validate(Break(scenario));

        Assert.Contains(issues, i => i.Code == code && i.Severity == severity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("0123456789abcdef")]
    public void ShortIdOfValidLengthIsAccepted(string shortId)
    {
        var profile = TestProfiles.VlessReality() with
        {
            Security = Reality() with { ShortId = new Secret(shortId) },
        };

        Assert.DoesNotContain(ProfileValidator.Validate(profile), i => i.Code == ProfileIssueCode.RealityShortIdInvalid);
    }

    [Fact]
    public void StringUserIdUpTo30BytesIsAccepted()
    {
        var profile = TestProfiles.VlessReality() with
        {
            Protocol = Vless() with { Id = new Secret("fictional-user-name") },
        };

        Assert.DoesNotContain(ProfileValidator.Validate(profile), i => i.Code == ProfileIssueCode.IdInvalid);
    }

    private static Profile Break(string scenario)
    {
        var vless = TestProfiles.VlessReality() with { UnknownParams = default };
        var vlessPlain = vless with { Protocol = Vless() with { Flow = null } };

        return scenario switch
        {
            "name-empty" => vless with { Name = " " },
            "host-empty" => vless with { Server = new ServerEndpoint("", 443) },
            "host-invalid" => vless with { Server = new ServerEndpoint("user@vpn.example.com", 443) },
            "port-zero" => vless with { Server = new ServerEndpoint(TestProfiles.Host, 0) },
            "port-too-big" => vless with { Server = new ServerEndpoint(TestProfiles.Host, 65536) },
            "id-empty" => vless with { Protocol = Vless() with { Id = new Secret("") } },
            "id-too-long" => vless with { Protocol = Vless() with { Id = new Secret(new string('x', 31)) } },
            "alter-id-negative" => TestProfiles.VmessWsTls() with { Protocol = new VmessSettings { Id = new Secret(TestProfiles.Uuid), AlterId = -1 } },
            "trojan-password-empty" => TestProfiles.TrojanGrpc() with { Protocol = new TrojanSettings { Password = new Secret("") } },
            "ss-method-empty" => TestProfiles.ShadowsocksPlugin() with { Protocol = new ShadowsocksSettings { Method = "", Password = new Secret(TestProfiles.Password) } },
            "flow-unknown" => vless with { Protocol = Vless() with { Flow = "xtls-rprx-direct" } },
            "flow-over-ws" => vless with { Transport = new WsTransport(), Security = new TlsSecurity { Sni = TestProfiles.Sni } },
            "flow-without-tls" => vless with { Security = new NoSecurity() },
            "flow-with-mux" => vless with { Mux = new MuxSettings { Enabled = true } },
            "mux-concurrency" => vlessPlain with { Mux = new MuxSettings { Enabled = true, Concurrency = 2000 } },
            "grpc-mode" => TestProfiles.TrojanGrpc() with { Transport = new GrpcTransport { Mode = "stream" } },
            "xhttp-mode" => TestProfiles.VlessXhttpReality() with { Transport = new XhttpTransport { Mode = "fast" } },
            "xhttp-extra" => TestProfiles.VlessXhttpReality() with { Transport = new XhttpTransport { Extra = "[1,2]" } },
            "reality-sni-empty" => vless with { Security = Reality() with { Sni = "" } },
            "reality-key-short" => vless with { Security = Reality() with { PublicKey = new Secret("Zx8Q2mT9vK4rL7pW") } },
            "reality-key-not-base64" => vless with { Security = Reality() with { PublicKey = new Secret(new string('!', 43)) } },
            "reality-sid-odd" => vless with { Security = Reality() with { ShortId = new Secret("abc") } },
            "reality-sid-not-hex" => vless with { Security = Reality() with { ShortId = new Secret("zz") } },
            "reality-sid-too-long" => vless with { Security = Reality() with { ShortId = new Secret("0123456789abcdef01") } },
            "reality-over-ws" => vlessPlain with { Transport = new WsTransport() },
            "tls-insecure" => TestProfiles.TrojanGrpc() with { Security = new TlsSecurity { AllowInsecure = true } },
            "unknown-params" => TestProfiles.VlessReality(),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };
    }

    private static VlessSettings Vless() => (VlessSettings)TestProfiles.VlessReality().Protocol;

    private static RealitySecurity Reality() => (RealitySecurity)TestProfiles.VlessReality().Security;
}
