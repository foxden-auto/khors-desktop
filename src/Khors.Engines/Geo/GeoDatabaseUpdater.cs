using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Khors.Core.Geo;

namespace Khors.Engines.Geo;

public enum GeoUpdateResult
{
    /// <summary>Скачан и установлен новый файл.</summary>
    Updated,

    /// <summary>Файл совпадает с последним релизом.</summary>
    UpToDate,

    Failed,
}

/// <param name="Error">Причина последней неудачной попытки, если <see cref="Result"/> = <see cref="GeoUpdateResult.Failed"/>.</param>
/// <param name="ViaProxy">Напрямую не получилось — загружено через локальный прокси KHORS.</param>
public sealed record GeoUpdateOutcome(GeoDatabaseKind Kind, GeoUpdateResult Result, GeoUpdateError? Error = null, bool ViaProxy = false);

/// <param name="Size">Размер в байтах; 0 — файла нет.</param>
/// <param name="Updated">Когда файл установлен (время изменения); <c>null</c> — файла нет.</param>
public sealed record GeoFileStatus(GeoDatabaseKind Kind, long Size, DateTimeOffset? Updated)
{
    public bool Present => Updated is not null;
}

/// <summary>
/// Загрузка и проверка гео-баз (ROADMAP 3.6) в один каталог: окно — в данные пользователя, служба — в свой каталог.
/// Для каждого файла: контрольная сумма релиза → совпадает с установленным файлом — ничего не делаем; иначе файл,
/// проверка SHA-256, разбор целиком и наличие нужных категорий, запись во временный файл и замена одной операцией.
/// Неудачная загрузка установленный файл не трогает. Источники — по порядку (<see cref="GeoSource.Mirrors"/>);
/// при сетевой ошибке и подключённом KHORS — повтор через его локальный прокси.
/// </summary>
public sealed class GeoDatabaseUpdater : IDisposable
{
    public delegate Task<GeoFetchResult> GeoFetch(Uri url, IWebProxy? proxy, long maxBytes, TimeSpan timeout, CancellationToken cancellationToken);

    private const long MaxChecksumBytes = 4096;
    private static readonly TimeSpan s_checksumTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_dataTimeout = TimeSpan.FromMinutes(5);

    private readonly IReadOnlyList<GeoSource> _sources;
    private readonly Func<int?> _localProxyPort;
    private readonly GeoFetch _fetch;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="directory">Каталог баз; создаётся при первой записи.</param>
    /// <param name="localProxyPort">HTTP-порт подключённого KHORS; <c>null</c> — не подключён или прокси не использовать (служба).</param>
    public GeoDatabaseUpdater(string directory, Func<int?>? localProxyPort = null, GeoFetch? fetch = null, IReadOnlyList<GeoSource>? sources = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
        _localProxyPort = localProxyPort ?? (() => null);
        _fetch = fetch ?? GeoFetcher.FetchAsync;
        _sources = sources ?? GeoSources.All;
    }

    public string Directory { get; }

    public void Dispose() => _gate.Dispose();

    public IReadOnlyList<GeoFileStatus> GetStatus() =>
        [.. _sources.Select(source =>
        {
            var file = new FileInfo(PathOf(source));
            return file.Exists ? new GeoFileStatus(source.Kind, file.Length, file.LastWriteTimeUtc) : new GeoFileStatus(source.Kind, 0, null);
        })];

    /// <summary>Обновляет все файлы по очереди. Параллельный вызов ждёт завершения текущего.</summary>
    public async Task<IReadOnlyList<GeoUpdateOutcome>> UpdateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcomes = new List<GeoUpdateOutcome>();
            foreach (var source in _sources)
            {
                outcomes.Add(await UpdateOneAsync(source, cancellationToken).ConfigureAwait(false));
            }

            return outcomes;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string PathOf(GeoSource source) => Path.Combine(Directory, source.FileName);

    private async Task<GeoUpdateOutcome> UpdateOneAsync(GeoSource source, CancellationToken cancellationToken)
    {
        GeoUpdateError? lastError = null;
        var viaProxy = false;
        foreach (var mirror in source.Mirrors)
        {
            var (outcome, usedProxy) = await TryMirrorAsync(source, mirror, proxy: null, cancellationToken).ConfigureAwait(false);
            if (outcome.Error is GeoUpdateError.Network or GeoUpdateError.Timeout && _localProxyPort() is { } port)
            {
                (outcome, usedProxy) = await TryMirrorAsync(source, mirror, new WebProxy(new Uri($"http://127.0.0.1:{port}")), cancellationToken).ConfigureAwait(false);
            }

            if (outcome.Result != GeoUpdateResult.Failed)
            {
                return outcome;
            }

            lastError = outcome.Error;
            viaProxy = usedProxy;
        }

        return new GeoUpdateOutcome(source.Kind, GeoUpdateResult.Failed, lastError, viaProxy);
    }

    private async Task<(GeoUpdateOutcome Outcome, bool ViaProxy)> TryMirrorAsync(GeoSource source, GeoMirror mirror, IWebProxy? proxy, CancellationToken cancellationToken)
    {
        var viaProxy = proxy is not null;
        var checksum = await _fetch(mirror.Checksum, proxy, MaxChecksumBytes, s_checksumTimeout, cancellationToken).ConfigureAwait(false);
        if (checksum.Error is { } checksumError)
        {
            return (Failed(checksumError), viaProxy);
        }

        if (ParseChecksum(checksum.Data!) is not { } expected)
        {
            return (Failed(GeoUpdateError.Invalid), viaProxy);
        }

        var path = PathOf(source);
        if (await HashFileAsync(path, cancellationToken).ConfigureAwait(false) is { } current && current.SequenceEqual(expected))
        {
            return (new GeoUpdateOutcome(source.Kind, GeoUpdateResult.UpToDate, ViaProxy: viaProxy), viaProxy);
        }

        var data = await _fetch(mirror.Data, proxy, source.MaxBytes, s_dataTimeout, cancellationToken).ConfigureAwait(false);
        if (data.Error is { } dataError)
        {
            return (Failed(dataError), viaProxy);
        }

        if (!SHA256.HashData(data.Data!).AsSpan().SequenceEqual(expected))
        {
            return (Failed(GeoUpdateError.ChecksumMismatch), viaProxy);
        }

        if (!IsValid(source, data.Data!))
        {
            return (Failed(GeoUpdateError.Invalid), viaProxy);
        }

        try
        {
            await InstallAsync(path, data.Data!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (Failed(GeoUpdateError.WriteFailed), viaProxy);
        }

        return (new GeoUpdateOutcome(source.Kind, GeoUpdateResult.Updated, ViaProxy: viaProxy), viaProxy);

        GeoUpdateOutcome Failed(GeoUpdateError error) => new(source.Kind, GeoUpdateResult.Failed, error, viaProxy);
    }

    /// <summary>Файл разбирается целиком, нужные категории есть и не пусты.</summary>
    public static bool IsValid(GeoSource source, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            return source.Kind switch
            {
                GeoDatabaseKind.Site => GeoSiteDatabase.ListCategories(data).Count > 0
                    && source.RequiredCategories.All(c => GeoSiteDatabase.ReadCategory(data, c) is { Domains.Count: > 0 }),
                _ => GeoIpDatabase.ListCategories(data).Count > 0
                    && source.RequiredCategories.All(c => GeoIpDatabase.ReadCategory(data, c) is { Networks.Count: > 0 }),
            };
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Первое слово файла <c>.sha256sum</c> — 64 шестнадцатеричных символа.</summary>
    internal static byte[]? ParseChecksum(byte[] text)
    {
        var line = System.Text.Encoding.ASCII.GetString(text).Trim();
        var end = line.IndexOfAny([' ', '\t', '\r', '\n']);
        var hex = end < 0 ? line : line[..end];
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? Convert.FromHexString(hex) : null;
    }

    private static async Task<byte[]?> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var stream = File.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static async Task InstallAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, data, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
