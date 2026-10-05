using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Khors.Core.Profiles;
using Khors.Core.Storage;
using Khors.Core.Tests.Profiles;
using Xunit;

namespace Khors.Core.Tests.Storage;

public partial class StorageJsonTests
{
    [Fact]
    public void ProfileDocumentRoundTrips()
    {
        var document = new ProfileDocument { Profiles = new EquatableArray<Profile>(TestProfiles.All.Select(r => (Profile)r.Data)) };

        var json = StorageJson.SerializeProfiles(document);
        var parsed = StorageJson.ParseProfiles(json);

        Assert.Equal(StorageLoadStatus.Ok, parsed.Status);
        Assert.Equal(document, parsed.Value);
        Assert.Equal(1, JsonNode.Parse(json)!["schemaVersion"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "profiles": [] }""")]
    [InlineData("""{ "schemaVersion": "one", "profiles": [] }""")]
    [InlineData("""{ "schemaVersion": 0, "profiles": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "profiles": [ { "name": "no id" } ] }""")]
    public void BrokenProfileFileIsCorrupt(string json) =>
        Assert.Equal(StorageLoadStatus.Corrupt, StorageJson.ParseProfiles(json).Status);

    [Fact]
    public void NewerSchemaVersionIsReportedNotParsed()
    {
        var parsed = StorageJson.ParseProfiles("""{ "schemaVersion": 7, "profiles": [ { "anything": true } ] }""");

        Assert.Equal(StorageLoadStatus.FutureVersion, parsed.Status);
        Assert.Equal(7, parsed.SchemaVersion);
        Assert.Null(parsed.Value);
    }

    [Fact]
    public void SettingsWithOnlyVersionGetDefaults()
    {
        var parsed = StorageJson.ParseSettings("""{ "schemaVersion": 1, "futureOption": 42 }""");

        Assert.Equal(StorageLoadStatus.Ok, parsed.Status);
        Assert.Equal(new AppSettings(), parsed.Value);
        Assert.Equal(10808, parsed.Value!.SocksPort);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var settings = new AppSettings(SelectedProfileId: Guid.Parse("0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a"), HttpPort: 20809, Language: "en");

        Assert.Equal(settings, StorageJson.ParseSettings(StorageJson.SerializeSettings(settings)).Value);
    }

    [Fact]
    public void MigrationsRunStepByStepUpToCurrentVersion()
    {
        var migrations = new Dictionary<int, Func<JsonObject, JsonObject>>
        {
            [1] = root =>
            {
                root["fullName"] = root["name"]!.GetValue<string>();
                root.Remove("name");
                return root;
            },
            [2] = root =>
            {
                root["fullName"] = root["fullName"]!.GetValue<string>().ToUpperInvariant();
                return root;
            },
        };

        var parsed = VersionedJson.Parse("""{ "schemaVersion": 1, "name": "khors" }""", 3, MigrationTestContext.Default.MigratedRecord, migrations);

        Assert.Equal(StorageLoadStatus.Ok, parsed.Status);
        Assert.Equal(new MigratedRecord(3, "KHORS"), parsed.Value);
        Assert.Equal(1, parsed.SchemaVersion);
    }

    [Fact]
    public void MissingMigrationMakesFileCorrupt() =>
        Assert.Equal(
            StorageLoadStatus.Corrupt,
            VersionedJson.Parse("""{ "schemaVersion": 1, "name": "x" }""", 2, MigrationTestContext.Default.MigratedRecord).Status);

    public sealed record MigratedRecord(int SchemaVersion, string FullName);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(MigratedRecord))]
    internal sealed partial class MigrationTestContext : JsonSerializerContext;
}
