using System.Text.Json;

namespace Khors.Engines.Cores;

/// <summary>Релиз ядра на GitHub, подходящий для этой платформы.</summary>
/// <param name="Sha256">SHA-256 архива из поля <c>digest</c> API GitHub (GitHub считает его при загрузке файла в релиз).</param>
public sealed record CoreRelease(CoreKind Kind, string Version, Uri AssetUrl, byte[] Sha256, long Size)
{
    public override string ToString() => $"CoreRelease {{ {Kind} {Version} }}";
}

/// <summary>Итог проверки: последний релиз серии поставки и есть ли версия новой серии.</summary>
/// <param name="Latest">Последний подходящий релиз серии; <c>null</c> — подходящих нет.</param>
/// <param name="NewerSeries">Версия новее серии поставки (например, sing-box 1.15.0) — для неё нужен новый KHORS.</param>
public sealed record CoreReleaseChoice(CoreRelease? Latest, string? NewerSeries);

/// <summary>
/// Разбор ответов API GitHub — чистые функции. Версии — по тегам (<c>/git/matching-refs/tags/v{серия}.</c>): ответ
/// маленький и содержит всю серию; список релизов для этого не годится — один релиз sing-box весит ~350 КБ, а между
/// стабильными версиями выходят десятки альф. Сам релиз (файлы и контрольные суммы) — <c>/releases/tags/v{версия}</c>.
/// Стабильная версия — тег из одних чисел («v1.14.2»); суффиксы («-alpha.10», «-rc.2») пропускаются. Флаг GitHub
/// «pre-release» не учитывается: Xray помечает им все свои релизы (проверено 2026-10-08, в том числе 26.9.9 из поставки).
/// </summary>
public static class CoreReleases
{
    /// <summary>Теги серии поставки, например <c>v26.</c> или <c>v1.14.</c>.</summary>
    public static Uri SeriesTagsUrl(CoreInfo core)
    {
        ArgumentNullException.ThrowIfNull(core);
        return TagsUrl(core, core.Series + ".");
    }

    /// <summary>Теги следующих серий (Xray 26 → «27.», sing-box 1.14 → «1.15.» и «2.») — только чтобы сказать, что вышла новая.</summary>
    public static IReadOnlyList<Uri> NextSeriesTagsUrls(CoreInfo core)
    {
        ArgumentNullException.ThrowIfNull(core);
        var urls = new List<Uri>();
        if (!CoreVersion.TryParse(core.ShippedVersion, out var shipped))
        {
            return urls;
        }

        for (var level = core.SeriesParts; level >= 1; level--)
        {
            var prefix = shipped.Take(level).ToArray();
            prefix[^1]++;
            urls.Add(TagsUrl(core, string.Join('.', prefix) + "."));
        }

        return urls;
    }

    public static Uri TagUrl(CoreInfo core, string version)
    {
        ArgumentNullException.ThrowIfNull(core);
        if (!CoreVersion.IsValid(version))
        {
            throw new ArgumentException("Invalid core version.", nameof(version));
        }

        return new Uri($"https://api.github.com/repos/{core.Repository}/releases/tags/v{version}");
    }

    /// <summary>Стабильные версии из ответа <c>matching-refs</c>: <c>refs/tags/v1.2.3</c> → «1.2.3».</summary>
    /// <exception cref="JsonException">Ответ не разбирается.</exception>
    public static IReadOnlyList<string> ParseTags(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Refs list expected.");
        }

        const string prefix = "refs/tags/v";
        return [.. document.RootElement.EnumerateArray()
            .Select(e => Text(e, "ref"))
            .Where(r => r is not null && r.StartsWith(prefix, StringComparison.Ordinal))
            .Select(r => r![prefix.Length..])
            .Where(CoreVersion.IsValid)
            .Distinct()];
    }

    private static Uri TagsUrl(CoreInfo core, string prefix) => new($"https://api.github.com/repos/{core.Repository}/git/matching-refs/tags/v{prefix}");

    /// <summary>Один релиз по тегу — для службы, которой окно называет версию. <c>null</c> — не стабильный, не та серия или нет архива с контрольной суммой.</summary>
    /// <exception cref="JsonException">Ответ не разбирается.</exception>
    public static CoreRelease? Parse(CoreInfo core, string version, byte[] json)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        return Stable(document.RootElement) == version && core.InSeries(version) ? Asset(core, version, document.RootElement) : null;
    }

    /// <summary>Версия не-черновика из тега <c>v1.2.3</c> (только числа); иначе <c>null</c>.</summary>
    private static string? Stable(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object
            || Bool(release, "draft")
            || !release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = tag.GetString()!;
        var version = text.StartsWith('v') ? text[1..] : text;
        return CoreVersion.IsValid(version) ? version : null;
    }

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Архив платформы с адресом на github.com и контрольной суммой SHA-256; без суммы — не берём.</summary>
    private static CoreRelease? Asset(CoreInfo core, string version, JsonElement release)
    {
        if (core.AssetFor(version) is not { } expected || !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (Text(asset, "name") != expected.Name
                || Text(asset, "browser_download_url") is not { } url
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com"
                || ParseDigest(Text(asset, "digest")) is not { } sha256)
            {
                continue;
            }

            var size = asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0;
            return new CoreRelease(core.Kind, version, uri, sha256, size);
        }

        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary><c>sha256:</c> и 64 шестнадцатеричных символа.</summary>
    internal static byte[]? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var hex = digest[prefix.Length..];
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? Convert.FromHexString(hex) : null;
    }
}
