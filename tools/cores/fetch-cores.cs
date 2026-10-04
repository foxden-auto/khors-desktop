// Загрузка закреплённых версий ядер с проверкой SHA-256 (ROADMAP 0.4).
//
//   dotnet run tools/cores/fetch-cores.cs                          ядра для текущей платформы
//   dotnet run tools/cores/fetch-cores.cs -- --platform all        все платформы из cores.lock.json
//   dotnet run tools/cores/fetch-cores.cs -- --platform linux-x64  указанная платформа
//   dotnet run tools/cores/fetch-cores.cs -- --verify              только проверить установленные файлы
//   dotnet run tools/cores/fetch-cores.cs -- --out <каталог>       каталог установки (по умолчанию cores/)
//
// Результат: cores/<платформа>/<ядро>/... Версии, URL и хэши — только из tools/cores/cores.lock.json.
// Архив проверяется до распаковки, каждый извлечённый файл — после. При любом несовпадении
// скачанное удаляется, установленная версия не трогается, код возврата 1.

using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

const string AllPlatforms = "all";

string? platformArg = null;
string? outArg = null;
var verifyOnly = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--platform" when i + 1 < args.Length:
            platformArg = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
            outArg = args[++i];
            break;
        case "--verify":
            verifyOnly = true;
            break;
        case "--help" or "-h":
            Console.WriteLine("Использование: dotnet run tools/cores/fetch-cores.cs -- [--platform <rid>|all] [--verify] [--out <каталог>]");
            return 0;
        default:
            Console.Error.WriteLine($"Неизвестный аргумент: {args[i]} (см. --help)");
            return 2;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    var root = FindRepositoryRoot();
    var lockPath = Path.Combine(root, "tools", "cores", "cores.lock.json");
    var lockFile = LoadLock(lockPath);
    var outDir = Path.GetFullPath(outArg ?? Path.Combine(root, "cores"));
    var platform = platformArg ?? RuntimeInformation.RuntimeIdentifier;

    var selected = lockFile.Cores
        .SelectMany(core => core.Assets.Select(asset => (core, asset)))
        .Where(x => platform == AllPlatforms || x.asset.Platform == platform)
        .ToList();

    if (selected.Count == 0)
    {
        Console.Error.WriteLine($"В cores.lock.json нет ядер для платформы {platform}.");
        return 1;
    }

    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("khors-desktop-fetch-cores");

    var failed = 0;
    foreach (var (core, asset) in selected)
    {
        var targetDir = Path.Combine(outDir, asset.Platform, core.Name);
        var label = $"{core.Name} {core.Version} [{asset.Platform}]";
        var problems = CheckInstalled(asset, targetDir);

        if (problems.Count == 0)
        {
            Console.WriteLine($"OK      {label}");
            continue;
        }

        if (verifyOnly)
        {
            failed++;
            Console.Error.WriteLine($"ОШИБКА  {label}:");
            foreach (var problem in problems)
            {
                Console.Error.WriteLine($"        {problem}");
            }

            continue;
        }

        try
        {
            await InstallAsync(http, asset, outDir, targetDir, cts.Token).ConfigureAwait(false);
            Console.WriteLine($"УСТАНОВЛЕНО {label}");
        }
        catch (InvalidDataException ex)
        {
            failed++;
            Console.Error.WriteLine($"ОШИБКА  {label}: {ex.Message}");
        }
    }

    return failed == 0 ? 0 : 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Прервано.");
    return 1;
}
catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException or JsonException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"ОШИБКА: {ex.Message}");
    return 1;
}

static string FindRepositoryRoot()
{
    for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Khors.slnx")))
        {
            return dir.FullName;
        }
    }

    throw new IOException("Не найден корень репозитория (Khors.slnx). Запускайте из каталога репозитория.");
}

static LockFile LoadLock(string path)
{
    using var stream = File.OpenRead(path);
    var lockFile = JsonSerializer.Deserialize(stream, LockJsonContext.Default.LockFile)
        ?? throw new InvalidDataException("cores.lock.json пуст.");

    if (lockFile.SchemaVersion != 1)
    {
        throw new InvalidDataException($"Неподдерживаемая schemaVersion {lockFile.SchemaVersion} в cores.lock.json.");
    }

    foreach (var core in lockFile.Cores)
    {
        foreach (var asset in core.Assets)
        {
            var where = $"{core.Name}/{asset.Platform}";
            if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com")
            {
                throw new InvalidDataException($"{where}: URL должен быть https://github.com/...");
            }

            RequireSha256(asset.Sha256, where);
            foreach (var file in asset.Files)
            {
                RequireSha256(file.Sha256, $"{where}/{file.Path}");
                if (string.IsNullOrEmpty(file.Path) || Path.GetFileName(file.Path) != file.Path || file.Path is "." or "..")
                {
                    throw new InvalidDataException($"{where}: path должен быть именем файла без каталогов: '{file.Path}'.");
                }
            }
        }
    }

    return lockFile;
}

static void RequireSha256(string value, string where)
{
    if (value is not { Length: 64 } || !value.All(Uri.IsHexDigit))
    {
        throw new InvalidDataException($"{where}: sha256 должен состоять из 64 шестнадцатеричных символов.");
    }
}

static List<string> CheckInstalled(AssetLock asset, string targetDir)
{
    var problems = new List<string>();
    foreach (var file in asset.Files)
    {
        var path = Path.Combine(targetDir, file.Path);
        if (!File.Exists(path))
        {
            problems.Add($"нет файла {file.Path}");
        }
        else if (!HashMatches(HashFile(path), file.Sha256))
        {
            problems.Add($"SHA-256 не совпадает: {file.Path}");
        }
    }

    return problems;
}

static async Task InstallAsync(HttpClient http, AssetLock asset, string outDir, string targetDir, CancellationToken ct)
{
    var downloadDir = Directory.CreateDirectory(Path.Combine(outDir, ".download")).FullName;
    var archivePath = Path.Combine(downloadDir, Path.GetFileName(new Uri(asset.Url).AbsolutePath));
    var stagingDir = Path.Combine(outDir, ".staging", $"{asset.Platform}-{Guid.NewGuid():N}");

    try
    {
        var actual = await DownloadAsync(http, asset.Url, archivePath, ct).ConfigureAwait(false);
        if (!HashMatches(actual, asset.Sha256))
        {
            throw new InvalidDataException($"SHA-256 архива не совпадает: ожидался {asset.Sha256}, получен {actual}. Архив удалён.");
        }

        Directory.CreateDirectory(stagingDir);
        await ExtractAsync(archivePath, asset, stagingDir, ct).ConfigureAwait(false);

        // Заменяем установленную версию только после полной проверки новой.
        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, recursive: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
        Directory.Move(stagingDir, targetDir);
    }
    finally
    {
        File.Delete(archivePath);
        if (Directory.Exists(stagingDir))
        {
            Directory.Delete(stagingDir, recursive: true);
        }

        DeleteIfEmpty(downloadDir);
        DeleteIfEmpty(Path.GetDirectoryName(stagingDir)!);
    }
}

static void DeleteIfEmpty(string dir)
{
    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
    {
        Directory.Delete(dir);
    }
}

static async Task<string> DownloadAsync(HttpClient http, string url, string path, CancellationToken ct)
{
    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();

    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    await using (source.ConfigureAwait(false))
    {
        var target = File.Create(path);
        await using (target.ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
    }

    return Convert.ToHexStringLower(hash.GetHashAndReset());
}

static async Task ExtractAsync(string archivePath, AssetLock asset, string stagingDir, CancellationToken ct)
{
    // Извлекаются только перечисленные в cores.lock.json записи, под заданными именами.
    var wanted = asset.Files.ToDictionary(f => f.Entry, StringComparer.Ordinal);
    var extracted = new HashSet<string>(StringComparer.Ordinal);

    if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
    {
        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var entry in zip.Entries)
        {
            if (wanted.TryGetValue(entry.FullName, out var file))
            {
                var data = entry.Open();
                await using (data.ConfigureAwait(false))
                {
                    await WriteVerifiedAsync(data, file, stagingDir, ct).ConfigureAwait(false);
                }

                extracted.Add(file.Entry);
            }
        }
    }
    else if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
    {
        var archive = File.OpenRead(archivePath);
        await using (archive.ConfigureAwait(false))
        {
            var gzip = new GZipStream(archive, CompressionMode.Decompress);
            await using (gzip.ConfigureAwait(false))
            {
                var reader = new TarReader(gzip);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
                    {
                        var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
                        if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile
                            && entry.DataStream is not null
                            && wanted.TryGetValue(name, out var file))
                        {
                            await WriteVerifiedAsync(entry.DataStream, file, stagingDir, ct).ConfigureAwait(false);
                            extracted.Add(file.Entry);
                        }
                    }
                }
            }
        }
    }
    else
    {
        throw new InvalidDataException($"Неподдерживаемый формат архива: {Path.GetFileName(archivePath)}");
    }

    var missing = wanted.Keys.Where(k => !extracted.Contains(k)).ToList();
    if (missing.Count > 0)
    {
        throw new InvalidDataException($"В архиве нет записей: {string.Join(", ", missing)}");
    }
}

static async Task WriteVerifiedAsync(Stream data, FileLock file, string stagingDir, CancellationToken ct)
{
    var path = Path.Combine(stagingDir, file.Path);
    var target = File.Create(path);
    await using (target.ConfigureAwait(false))
    {
        await data.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    var actual = HashFile(path);
    if (!HashMatches(actual, file.Sha256))
    {
        throw new InvalidDataException($"SHA-256 файла {file.Path} не совпадает: ожидался {file.Sha256}, получен {actual}.");
    }

    if (file.Executable && !OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}

static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}

static bool HashMatches(string actual, string expected) =>
    string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

internal sealed record LockFile(int SchemaVersion, List<CoreLock> Cores);

internal sealed record CoreLock(string Name, string Version, string License, string Source, List<AssetLock> Assets);

internal sealed record AssetLock(string Platform, string Url, string Sha256, List<FileLock> Files);

internal sealed record FileLock(string Entry, string Path, string Sha256, bool Executable = false);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(LockFile))]
internal sealed partial class LockJsonContext : JsonSerializerContext;
