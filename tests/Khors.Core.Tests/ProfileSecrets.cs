using Khors.Core.Profiles;

namespace Khors.Core.Tests;

/// <summary>Значения профиля, которые не должны появляться в логах и отчётах.</summary>
internal static class ProfileSecrets
{
    /// <summary>Секреты и адреса профиля длиной от 6 символов (короче — неизбежно совпадают с частями масок и текста).</summary>
    public static IReadOnlyList<string> Of(Profile profile) => Enumerate(profile).Where(s => s.Length >= 6).Distinct().ToList();

    private static IEnumerable<string> Enumerate(Profile profile)
    {
        yield return profile.Server.Host;

        switch (profile.Protocol)
        {
            case VlessSettings vless:
                yield return vless.Id.Value;
                break;
            case VmessSettings vmess:
                yield return vmess.Id.Value;
                break;
            case TrojanSettings trojan:
                yield return trojan.Password.Value;
                break;
            case ShadowsocksSettings ss:
                yield return ss.Password.Value;
                break;
            case Hysteria2Settings hysteria:
                yield return hysteria.Password.Value;
                if (hysteria.ObfsPassword is { } obfs)
                {
                    yield return obfs.Value;
                }

                break;
            case TuicSettings tuic:
                yield return tuic.Uuid.Value;
                yield return tuic.Password.Value;
                break;
            case WireGuardSettings wireGuard:
                yield return wireGuard.PrivateKey.Value;
                yield return wireGuard.PeerPublicKey.Value;
                if (wireGuard.PreSharedKey is { } psk)
                {
                    yield return psk.Value;
                }

                break;
        }

        switch (profile.Security)
        {
            case TlsSecurity tls:
                if (tls.Sni is not null)
                {
                    yield return tls.Sni;
                }

                foreach (var name in tls.VerifyPeerCertByName)
                {
                    yield return name;
                }

                break;
            case RealitySecurity reality:
                yield return reality.Sni;
                yield return reality.PublicKey.Value;
                if (reality.ShortId is { } shortId)
                {
                    yield return shortId.Value;
                }

                break;
        }
    }
}
