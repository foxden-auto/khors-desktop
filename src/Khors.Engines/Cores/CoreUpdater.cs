using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Khors.Engines.Geo;

namespace Khors.Engines.Cores;

public enum CoreUpdateError
{
    /// <summary>Сеть недоступна или соединение оборвалось.</summary>
    Network,

    Timeout,

    /// <summary>GitHub ответил ошибкой (в том числе лимит запросов API).</summary>
    HttpError,

    /// <summary>Ответ или архив больше допустимого.</summary>
    TooLarge,

    /// <summary>Ответ GitHub не разбирается.</summary>
    InvalidResponse,

    /// <summary>Релиза этой версии нет, он не стабильный, не из серии поставки или без контрольной суммы.</summary>
    NotFound,

    /// <summary>Скачанный архив не совпал с контрольной суммой релиза.</summary>
    ChecksumMismatch,

    /// <summary>В архиве нет ожидаемых файлов.</summary>
    InvalidArchive,

    /// <summary>Ядро не прошло проверку конфигами KHORS.</summary>
    Incompatible,

    /// <summary>Не удалось записать файлы.</summary>
    WriteFailed,
}

/// <summary>Итог проверки обновлений одного ядра.</summary>
public sealed record CoreCheckOutcome(CoreKind Kind, CoreReleaseChoice? Choice, CoreUpdateError? Error = null)
{
    /// <summary>Есть версия новее работающей.</summary>
    public bool HasUpdate(string current) => Choice?.Latest is { } latest && CoreVersion.Compare(latest.Version, current) > 0;
}

/// <param name="Detail">Текст ядра при <see cref="CoreUpdateError.Incompatible"/> (конфиги проверки вымышленные).</param>
public sealed record CoreUpdateOutcome(CoreKind Kind, string Version, CoreUpdateError? Error = null, string? Detail = null)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Обновление ядер из GitHub Releases (ROADMAP 5.2, docs/SPEC.md, 4.8) — только по команде пользователя (CLAUDE.md,
/// правило 6). Релиз — стабильный, из серии поставки (<see cref="CoreInfo.InSeries"/>); архив сверяется с SHA-256 из API
/// GitHub, из него берутся исполняемый файл и лицензия; ядро проверяется конфигами KHORS (<see cref="CoreCompatibility"/>)
/// и только потом включается. Работающая версия остаётся для отката. Окно при сетевой ошибке повторяет загрузку через
/// подключённый KHORS; служба — только напрямую.
/// </summary>
public sealed class CoreUpdater : IDisposable
{
    public delegate Task<string?> CompatibilityCheck(CoreKind kind, string executable, string version, CancellationToken cancellationToken);

    private const long MaxApiBytes = 2 * 1024 * 1024;
    private const int MaxReleaseTries = 3;
    private const long MaxArchiveBytes = 200L * 1024 * 1024;
    private static readonly TimeSpan s_apiTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_archiveTimeout = TimeSpan.FromMinutes(10);

    private readonly Func<int?> _localProxyPort;
    private readonly GeoDatabaseUpdater.GeoFetch _fetch;
    private readonly CompatibilityCheck _check;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="localProxyPort">HTTP-порт подключённого KHORS; <c>null</c> — не подключён или прокси не использовать (служба).</param>
    public CoreUpdater(CoreStore store, Func<int?>? localProxyPort = null, GeoDatabaseUpdater.GeoFetch? fetch = null, CompatibilityCheck? check = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        Store = store;
        _localProxyPort = localProxyPort ?? (() => null);
        _fetch = fetch ?? GeoFetcher.FetchAsync;
        _check = check ?? CoreCompatibility.CheckAsync;
    }

    public CoreStore Store { get; }

    public void Dispose() => _gate.Dispose();

    public IReadOnlyList<CoreVersionStatus> Status() => [.. CoreCatalog.All.Select(c => Store.Status(c.Kind))];

    /// <summary>
    /// Последний релиз серии и есть ли версия новой серии. Теги серии — от старшего: у тега может не быть релиза
    /// (или файла платформы с контрольной суммой) — тогда следующий, не больше <see cref="MaxReleaseTries"/>.
    /// Сеть — только здесь и в <see cref="InstallAsync"/>.
    /// </summary>
    public async Task<CoreCheckOutcome> CheckAsync(CoreKind kind, CancellationToken cancellationToken = default)
    {
        var core = CoreCatalog.Get(kind);
        var (tags, error) = await FetchTagsAsync(core, CoreReleases.SeriesTagsUrl(core), cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return new CoreCheckOutcome(kind, null, error);
        }

        CoreRelease? latest = null;
        foreach (var version in tags!.Where(core.InSeries).OrderDescending(Comparer<string>.Create(CoreVersion.Compare)).Take(MaxReleaseTries))
        {
            var (data, releaseError) = await FetchAsync(CoreReleases.TagUrl(core, version), MaxApiBytes, s_apiTimeout, cancellationToken).ConfigureAwait(false);
            if (releaseError is CoreUpdateError.HttpError)
            {
                continue;
            }

            if (releaseError is not null)
            {
                return new CoreCheckOutcome(kind, null, releaseError);
            }

            try
            {
                latest = CoreReleases.Parse(core, version, data!);
            }
            catch (JsonException)
            {
                return new CoreCheckOutcome(kind, null, CoreUpdateError.InvalidResponse);
            }

            if (latest is not null)
            {
                break;
            }
        }

        // Новая серия — только для подсказки; ошибки здесь проверку не портят.
        string? newerSeries = null;
        foreach (var url in CoreReleases.NextSeriesTagsUrls(core))
        {
            var (next, _) = await FetchTagsAsync(core, url, cancellationToken).ConfigureAwait(false);
            foreach (var version in next ?? [])
            {
                if (CoreVersion.Compare(version, newerSeries) > 0)
                {
                    newerSeries = version;
                }
            }
        }

        return new CoreCheckOutcome(kind, new CoreReleaseChoice(latest, newerSeries));
    }

    private async Task<(IReadOnlyList<string>? Tags, CoreUpdateError? Error)> FetchTagsAsync(CoreInfo core, Uri url, CancellationToken cancellationToken)
    {
        var (data, error) = await FetchAsync(url, MaxApiBytes, s_apiTimeout, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        try
        {
            return (CoreReleases.ParseTags(data!, core), null);
        }
        catch (JsonException)
        {
            return (null, CoreUpdateError.InvalidResponse);
        }
    }

    /// <summary>Устанавливает и включает версию, названную окном (служба проверяет релиз сама).</summary>
    public async Task<CoreUpdateOutcome> InstallVersionAsync(CoreKind kind, string version, CancellationToken cancellationToken = default)
    {
        var core = CoreCatalog.Get(kind);
        if (!CoreVersion.IsValid(version) || !core.InSeries(version))
        {
            return new CoreUpdateOutcome(kind, version, CoreUpdateError.NotFound);
        }

        var (data, error) = await FetchAsync(CoreReleases.TagUrl(core, version), MaxApiBytes, s_apiTimeout, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return new CoreUpdateOutcome(kind, version, error == CoreUpdateError.HttpError ? CoreUpdateError.NotFound : error);
        }

        CoreRelease? release;
        try
        {
            release = CoreReleases.Parse(core, version, data!);
        }
        catch (JsonException)
        {
            return new CoreUpdateOutcome(kind, version, CoreUpdateError.InvalidResponse);
        }

        return release is null
            ? new CoreUpdateOutcome(kind, version, CoreUpdateError.NotFound)
            : await InstallAsync(release, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Загрузка, проверка, установка и включение. Неудача не трогает работающую версию.</summary>
    public async Task<CoreUpdateOutcome> InstallAsync(CoreRelease release, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        var core = CoreCatalog.Get(release.Kind);
        if (!core.InSeries(release.Version) || core.AssetFor(release.Version) is not { } asset)
        {
            return Failed(CoreUpdateError.NotFound);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (archive, error) = await FetchAsync(release.AssetUrl, MaxArchiveBytes, s_archiveTimeout, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                return Failed(error.Value);
            }

            if (!SHA256.HashData(archive!).AsSpan().SequenceEqual(release.Sha256))
            {
                return Failed(CoreUpdateError.ChecksumMismatch);
            }

            if (Extract(archive!, asset) is not { } files)
            {
                return Failed(CoreUpdateError.InvalidArchive);
            }

            string executable;
            try
            {
                executable = Store.Install(release.Kind, release.Version, files.Executable, files.License);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failed(CoreUpdateError.WriteFailed);
            }

            if (await _check(release.Kind, executable, release.Version, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                Store.Discard(release.Kind, release.Version);
                return new CoreUpdateOutcome(release.Kind, release.Version, CoreUpdateError.Incompatible, problem);
            }

            try
            {
                Store.Activate(release.Kind, release.Version);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failed(CoreUpdateError.WriteFailed);
            }

            return new CoreUpdateOutcome(release.Kind, release.Version);
        }
        finally
        {
            _gate.Release();
        }

        CoreUpdateOutcome Failed(CoreUpdateError error) => new(release.Kind, release.Version, error);
    }

    /// <summary>Возвращает предыдущую версию или поставку; <c>false</c> — откатываться некуда.</summary>
    public bool Rollback(CoreKind kind)
    {
        try
        {
            return Store.Rollback(kind);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Исполняемый файл и лицензия из zip-архива релиза; <c>null</c> — архив не разбирается или файлов нет.</summary>
    internal static (byte[] Executable, byte[] License)? Extract(byte[] archive, CoreAsset asset)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
            var executable = Read(zip, asset.ExecutableEntry);
            var license = Read(zip, asset.LicenseEntry);
            return executable is { Length: > 0 } && license is not null ? (executable, license) : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static byte[]? Read(ZipArchive zip, string name)
    {
        if (zip.GetEntry(name) is not { } entry || entry.Length > MaxArchiveBytes)
        {
            return null;
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private async Task<(byte[]? Data, CoreUpdateError? Error)> FetchAsync(Uri url, long maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await _fetch(url, null, maxBytes, timeout, cancellationToken).ConfigureAwait(false);
        if (result.Error is GeoUpdateError.Network or GeoUpdateError.Timeout && _localProxyPort() is { } port)
        {
            result = await _fetch(url, new WebProxy(new Uri($"http://127.0.0.1:{port}")), maxBytes, timeout, cancellationToken).ConfigureAwait(false);
        }

        return (result.Data, result.Error switch
        {
            null => null,
            GeoUpdateError.Network => CoreUpdateError.Network,
            GeoUpdateError.Timeout => CoreUpdateError.Timeout,
            GeoUpdateError.TooLarge => CoreUpdateError.TooLarge,
            _ => CoreUpdateError.HttpError,
        });
    }
}
