using Khors.Core.Storage;
using Khors.Engines.Storage;
using Xunit;

namespace Khors.Engines.Tests.Storage;

public sealed class DocumentFileTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 9, 30, 0, TimeSpan.Zero));

    public void Dispose() => _directory.Dispose();

    private DocumentFile<AppSettings> CreateFile() =>
        new(_directory.File("settings.json"), StorageJson.ParseSettings, StorageJson.SerializeSettings, _time);

    [Fact]
    public void MissingFileLoadsAsMissing()
    {
        var result = CreateFile().Load();

        Assert.Equal(StorageLoadStatus.Missing, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public void SaveThenLoadAndPreviousVersionBecomesBackup()
    {
        var file = CreateFile();
        file.Save(new AppSettings(HttpPort: 1001));
        file.Save(new AppSettings(HttpPort: 1002));

        Assert.Equal(1002, file.Load().Value!.HttpPort);
        Assert.Equal(1001, StorageJson.ParseSettings(File.ReadAllText(file.BackupPath)).Value!.HttpPort);
        Assert.False(File.Exists(file.FilePath + ".tmp"));
    }

    [Fact]
    public void CorruptMainFileIsSetAsideAndBackupIsUsed()
    {
        var file = CreateFile();
        file.Save(new AppSettings(HttpPort: 1001));
        file.Save(new AppSettings(HttpPort: 1002));
        File.WriteAllText(file.FilePath, "{ обрыв записи");

        var result = file.Load();

        Assert.Equal(StorageLoadStatus.Ok, result.Status);
        Assert.True(result.FromBackup);
        Assert.Equal(1001, result.Value!.HttpPort);
        Assert.Equal(file.FilePath + ".corrupt-20261005-093000", result.PreservedCorruptPath);
        Assert.Equal("{ обрыв записи", File.ReadAllText(result.PreservedCorruptPath!));
        Assert.False(File.Exists(file.FilePath));
    }

    [Fact]
    public void CorruptFileWithoutBackupIsReportedAndKept()
    {
        var file = CreateFile();
        File.WriteAllText(file.FilePath, "garbage");

        var result = file.Load();

        Assert.Equal(StorageLoadStatus.Corrupt, result.Status);
        Assert.Null(result.Value);
        Assert.True(File.Exists(result.PreservedCorruptPath));
    }

    [Fact]
    public void FileFromNewerVersionIsNeitherParsedNorMoved()
    {
        var file = CreateFile();
        const string future = """{ "schemaVersion": 99 }""";
        File.WriteAllText(file.FilePath, future);

        var result = file.Load();

        Assert.Equal(StorageLoadStatus.FutureVersion, result.Status);
        Assert.Equal(future, File.ReadAllText(file.FilePath));
    }
}
