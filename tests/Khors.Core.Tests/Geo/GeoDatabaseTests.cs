using System.Net;
using System.Text;
using Khors.Core.Geo;
using Xunit;
using static Khors.Core.Tests.Geo.GeoFiles;

namespace Khors.Core.Tests.Geo;

/// <summary>Чтение geosite.dat и geoip.dat на маленьких файлах, собранных здесь же (данные вымышленные).</summary>
public class GeoDatabaseTests
{
    private static readonly byte[] s_site = GeoSiteFile(
        Site("CATEGORY-RU",
            Domain(2, "ru"),
            Domain(3, "www.example.ru"),
            Domain(0, "exampleru", "ads"),
            Domain(1, @"^.+\.example\.su$")),
        Site("PRIVATE", Domain(3, "localhost")),
        Site("EXAMPLE", Domain(2, "example.com", "cn", "ads")));

    private static readonly byte[] s_ip = GeoIpFile(
        Ip("RU", reverse: false, Cidr([203, 0, 113, 0], 24), Cidr([198, 51, 100, 77], 24), Cidr(IPAddress.Parse("2001:db8::").GetAddressBytes(), 32)),
        Ip("PRIVATE", reverse: false, Cidr([10, 0, 0, 0], 8)),
        Ip("NOT-RU", reverse: true, Cidr([203, 0, 113, 0], 24)));

    [Fact]
    public void ListsSiteCategoriesInLowerCase()
    {
        Assert.Equal(["category-ru", "private", "example"], GeoSiteDatabase.ListCategories(s_site));
    }

    [Fact]
    public void ReadsSiteCategoryIgnoringCase()
    {
        var category = GeoSiteDatabase.ReadCategory(s_site, "Category-RU");

        Assert.NotNull(category);
        Assert.Equal("category-ru", category.Code);
        Assert.Equal(
            [
                new GeoDomain(GeoDomainType.Domain, "ru", new([])),
                new GeoDomain(GeoDomainType.Full, "www.example.ru", new([])),
                new GeoDomain(GeoDomainType.Keyword, "exampleru", new(["ads"])),
                new GeoDomain(GeoDomainType.Regex, @"^.+\.example\.su$", new([])),
            ],
            category.Domains);
        Assert.Equal(new GeoDomain(GeoDomainType.Domain, "example.com", new(["cn", "ads"])), GeoSiteDatabase.ReadCategory(s_site, "example")!.Domains[0]);
    }

    [Fact]
    public void MissingCategoryIsNull()
    {
        Assert.Null(GeoSiteDatabase.ReadCategory(s_site, "google"));
        Assert.Null(GeoIpDatabase.ReadCategory(s_ip, "us"));
    }

    [Fact]
    public void ReadsIpCategoryAndNormalizesHostBits()
    {
        Assert.Equal(["ru", "private", "not-ru"], GeoIpDatabase.ListCategories(s_ip));

        var ru = GeoIpDatabase.ReadCategory(s_ip, "ru")!;
        Assert.Equal("ru", ru.Code);
        Assert.False(ru.ReverseMatch);
        Assert.Equal([IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("198.51.100.0/24"), IPNetwork.Parse("2001:db8::/32")], ru.Networks);
        Assert.True(GeoIpDatabase.ReadCategory(s_ip, "NOT-RU")!.ReverseMatch);
    }

    [Fact]
    public void EmptyFileHasNoCategories()
    {
        Assert.Empty(GeoSiteDatabase.ListCategories([]));
        Assert.Empty(GeoIpDatabase.ListCategories([]));
    }

    public static TheoryData<string> BrokenFiles => new()
    {
        "truncated",
        "wrong-wire-type",
        "bad-prefix",
        "bad-ip-length",
        "unknown-domain-type",
        "garbage",
    };

    [Theory]
    [MemberData(nameof(BrokenFiles))]
    public void BrokenFileThrowsInvalidData(string kind)
    {
        byte[] data = kind switch
        {
            "truncated" => s_ip[..^3],
            "wrong-wire-type" => [0x08, 0x01],
            "bad-prefix" => GeoIpFile(Ip("RU", false, Cidr([203, 0, 113, 0], 33))),
            "bad-ip-length" => GeoIpFile(Ip("RU", false, Cidr([203, 0, 113], 24))),
            "unknown-domain-type" => GeoSiteFile(Site("RU", Domain(7, "ru"))),
            _ => Encoding.ASCII.GetBytes("<html>Not Found</html>"),
        };

        // Обход всех записей (как при проверке загруженного файла) и разбор нужной категории.
        Assert.Throws<InvalidDataException>(() =>
        {
            _ = GeoIpDatabase.ListCategories(data);
            _ = GeoIpDatabase.ReadCategory(data, "ru");
            _ = GeoSiteDatabase.ReadCategory(data, "ru");
        });
    }
}
