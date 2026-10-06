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
        Assert.Equal(ProfileDocument.CurrentSchemaVersion, JsonNode.Parse(json)!["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void FavoriteIsStoredOnlyWhenSet()
    {
        var plain = TestProfiles.VlessReality();
        var favorite = plain with { IsFavorite = true };

        Assert.Null(JsonNode.Parse(StorageJson.SerializeProfile(plain))!["isFavorite"]);
        Assert.True(JsonNode.Parse(StorageJson.SerializeProfile(favorite))!["isFavorite"]!.GetValue<bool>());
        Assert.Equal(favorite, StorageJson.ParseProfile(StorageJson.SerializeProfile(favorite)));
    }

    [Fact]
    public void VersionOneFileIsMigratedWithoutLosingProfiles()
    {
        // Файл формата 1.7 (до подписок).
        const string v1 = """
            {
              "schemaVersion": 1,
              "profiles": [
                {
                  "id": "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a",
                  "name": "старый профиль",
                  "server": { "host": "vpn.example.com", "port": 443 },
                  "protocol": { "type": "trojan", "password": "Fictional-Pa55" }
                }
              ]
            }
            """;

        var parsed = StorageJson.ParseProfiles(v1);

        Assert.Equal(StorageLoadStatus.Ok, parsed.Status);
        Assert.Equal(1, parsed.SchemaVersion);
        Assert.Equal("старый профиль", Assert.Single(parsed.Value!.Profiles).Name);
        Assert.Empty(parsed.Value.Subscriptions);
        Assert.Equal(ProfileDocument.CurrentSchemaVersion, parsed.Value.SchemaVersion);
        Assert.Equal(2, JsonNode.Parse(StorageJson.SerializeProfiles(parsed.Value))!["schemaVersion"]!.GetValue<int>());
    }

    /// <summary>Подписка с частично заполненными полями (null опускаются при записи) читается обратно без потерь.</summary>
    [Fact]
    public void SubscriptionWithPartialInfoRoundTrips()
    {
        var subscription = new Khors.Core.Subscriptions.Subscription
        {
            Id = Guid.Parse("9e8d7c6b-5a4f-4e3d-9c2b-1a0f9e8d7c6b"),
            Name = "Вымышленная",
            Url = new Secret("https://sub.example.com/s/fictional-token"),
            UserInfo = new Khors.Core.Subscriptions.SubscriptionUserInfo(Total: 100),
            LastError = Khors.Core.Subscriptions.SubscriptionUpdateError.Timeout,
        };
        var document = new ProfileDocument { Subscriptions = new EquatableArray<Khors.Core.Subscriptions.Subscription>([subscription]) };

        var parsed = StorageJson.ParseProfiles(StorageJson.SerializeProfiles(document));

        Assert.Equal(StorageLoadStatus.Ok, parsed.Status);
        Assert.Equal(document, parsed.Value);
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
