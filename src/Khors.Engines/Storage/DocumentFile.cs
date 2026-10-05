using System.Globalization;
using System.Text;
using Khors.Core.Storage;

namespace Khors.Engines.Storage;

/// <summary>Результат чтения файла данных.</summary>
/// <param name="FromBackup">Основной файл отсутствовал или был повреждён — данные взяты из резервной копии.</param>
/// <param name="PreservedCorruptPath">Куда отложен повреждённый основной файл (для диагностики).</param>
public sealed record DocumentLoadResult<T>(T? Value, StorageLoadStatus Status, bool FromBackup, string? PreservedCorruptPath)
    where T : class;

/// <summary>
/// JSON-файл данных с резервной копией (docs/SPEC.md, 5: повреждённый файл не приводит к потере всех профилей).
/// Запись: временный файл → сброс на диск → атомарная замена основного, прежняя версия становится <c>.bak</c>.
/// Чтение: основной файл; если он повреждён — откладывается как <c>.corrupt-*</c>, данные берутся из <c>.bak</c>.
/// </summary>
public sealed class DocumentFile<T>
    where T : class
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Func<string, VersionedParseResult<T>> _parse;
    private readonly Func<T, string> _serialize;
    private readonly TimeProvider _time;

    public DocumentFile(string path, Func<string, VersionedParseResult<T>> parse, Func<T, string> serialize, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(serialize);
        FilePath = Path.GetFullPath(path);
        _parse = parse;
        _serialize = serialize;
        _time = time ?? TimeProvider.System;
    }

    public string FilePath { get; }

    public string BackupPath => FilePath + ".bak";

    public DocumentLoadResult<T> Load()
    {
        string? preserved = null;
        var mainExists = File.Exists(FilePath);

        if (mainExists)
        {
            var main = _parse(File.ReadAllText(FilePath, s_utf8));
            switch (main.Status)
            {
                case StorageLoadStatus.Ok:
                    return new(main.Value, StorageLoadStatus.Ok, FromBackup: false, null);

                // Файл новой версии KHORS: не читаем и не трогаем, иначе потеряем данные при сохранении.
                case StorageLoadStatus.FutureVersion:
                    return new(null, StorageLoadStatus.FutureVersion, FromBackup: false, null);

                default:
                    preserved = PreserveCorrupt();
                    break;
            }
        }

        if (File.Exists(BackupPath) && _parse(File.ReadAllText(BackupPath, s_utf8)) is { Status: StorageLoadStatus.Ok } backup)
        {
            return new(backup.Value, StorageLoadStatus.Ok, FromBackup: true, preserved);
        }

        return new(null, mainExists ? StorageLoadStatus.Corrupt : StorageLoadStatus.Missing, FromBackup: false, preserved);
    }

    public void Save(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        var bytes = s_utf8.GetBytes(_serialize(value));
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(FilePath))
        {
            File.Replace(temporary, FilePath, BackupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporary, FilePath);
        }
    }

    private string PreserveCorrupt()
    {
        var stamp = _time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = $"{FilePath}.corrupt-{stamp}";
        File.Move(FilePath, target, overwrite: true);
        return target;
    }
}
