using Khors.Core.Storage;
using Khors.Engines.Storage;
using Xunit;

namespace Khors.Engines.Tests.Storage;

public sealed class ProfileRepositoryTests : IDisposable
{
    private const string Vless = "vless://3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b@vpn.example.com:443?security=tls&sni=vpn.example.com#Один";
    private const string Trojan = "trojan://Fictional-Pa55@trojan.example.com:443#Два";

    private readonly TempDirectory _directory = new();
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 9, 30, 0, TimeSpan.Zero));

    private string ProfilesPath => _directory.File("profiles.json");

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void ImportedProfilesSurviveRestart()
    {
        var repository = ProfileRepository.Open(ProfilesPath, _time);
        var changes = 0;
        repository.Changed += (_, _) => changes++;

        var result = repository.Import($"{Vless}\n{Trojan}");

        Assert.Equal(2, result.Added.Count);
        Assert.Equal(1, changes);
        var reopened = ProfileRepository.Open(ProfilesPath, _time);
        Assert.Equal(repository.Profiles, reopened.Profiles);
        Assert.Equal(StorageLoadStatus.Ok, reopened.LoadResult.Status);
    }

    [Fact]
    public void ImportOfOnlyDuplicatesDoesNotRewriteFile()
    {
        var repository = ProfileRepository.Open(ProfilesPath, _time);
        repository.Import(Vless);
        var written = File.GetLastWriteTimeUtc(ProfilesPath);

        var result = repository.Import(Vless);

        Assert.Equal(1, result.Duplicates);
        Assert.Equal(written, File.GetLastWriteTimeUtc(ProfilesPath));
        Assert.False(File.Exists(ProfilesPath + ".bak"));
    }

    [Fact]
    public void RemoveAndUpdateArePersisted()
    {
        var repository = ProfileRepository.Open(ProfilesPath, _time);
        repository.Import($"{Vless}\n{Trojan}");
        var first = repository.Profiles[0];
        var second = repository.Profiles[1];

        _time.Now = _time.Now.AddHours(1);
        repository.Update(first with { Name = "Переименован" });
        Assert.True(repository.Remove(second.Id));
        Assert.False(repository.Remove(second.Id));

        var reopened = ProfileRepository.Open(ProfilesPath, _time).Profiles;
        var updated = Assert.Single(reopened);
        Assert.Equal("Переименован", updated.Name);
        Assert.Equal(_time.Now, updated.UpdatedAt);
    }

    [Fact]
    public void CorruptProfilesFileFallsBackToBackup()
    {
        var repository = ProfileRepository.Open(ProfilesPath, _time);
        repository.Import(Vless);
        repository.Import(Trojan);
        File.WriteAllText(ProfilesPath, "{");

        var reopened = ProfileRepository.Open(ProfilesPath, _time);

        Assert.True(reopened.LoadResult.FromBackup);
        Assert.Equal(["Один"], reopened.Profiles.Select(p => p.Name));
    }

    [Fact]
    public void FileFromNewerVersionMakesRepositoryReadOnly()
    {
        const string future = """{ "schemaVersion": 2, "profiles": [] }""";
        File.WriteAllText(ProfilesPath, future);

        var repository = ProfileRepository.Open(ProfilesPath, _time);

        Assert.True(repository.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => repository.Import(Vless));
        Assert.Equal(future, File.ReadAllText(ProfilesPath));
    }

    [Fact]
    public void SettingsAreSavedAndDefaultsUsedForMissingFile()
    {
        var settingsPath = _directory.File("settings.json");
        var store = SettingsStore.Open(settingsPath, _time);
        Assert.Equal(new AppSettings(), store.Current);

        var selected = Guid.NewGuid();
        store.Update(s => s with { SelectedProfileId = selected });

        Assert.Equal(selected, SettingsStore.Open(settingsPath, _time).Current.SelectedProfileId);
    }
}
