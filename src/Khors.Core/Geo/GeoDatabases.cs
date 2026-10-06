using System.Net;
using Khors.Core.Profiles;

namespace Khors.Core.Geo;

/// <summary>Вид правила домена в geosite (как в Xray: <c>keyword:</c>, <c>regexp:</c>, <c>domain:</c>, <c>full:</c>).</summary>
public enum GeoDomainType
{
    /// <summary>Подстрока имени.</summary>
    Keyword = 0,

    /// <summary>Регулярное выражение (синтаксис Go RE2).</summary>
    Regex = 1,

    /// <summary>Домен и все его поддомены.</summary>
    Domain = 2,

    /// <summary>Точное имя.</summary>
    Full = 3,
}

/// <param name="Attributes">Атрибуты правила (например, <c>ads</c>, <c>cn</c>) — в Xray выбираются как <c>geosite:имя@атрибут</c>.</param>
public sealed record GeoDomain(GeoDomainType Type, string Value, EquatableArray<string> Attributes);

/// <param name="Code">Имя категории в нижнем регистре (<c>category-ru</c>).</param>
public sealed record GeoSiteCategory(string Code, IReadOnlyList<GeoDomain> Domains);

/// <param name="Code">Код в нижнем регистре (<c>ru</c>, <c>private</c>).</param>
/// <param name="ReverseMatch">Совпадение — адреса, которых нет в списке.</param>
public sealed record GeoIpCategory(string Code, IReadOnlyList<IPNetwork> Networks, bool ReverseMatch);

/// <summary>
/// Чтение <c>geosite.dat</c> (формат v2fly/Xray: GeoSiteList → GeoSite → Domain). Код свой, по описанию формата
/// (CLAUDE.md, правило 1). Испорченный файл — <see cref="InvalidDataException"/>.
/// </summary>
public static class GeoSiteDatabase
{
    /// <summary>Имена всех категорий в нижнем регистре, в порядке файла.</summary>
    public static IReadOnlyList<string> ListCategories(ReadOnlySpan<byte> data)
    {
        var codes = new List<string>();
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var entry = GeoDatabase.NextEntry(ref reader);
            if (!entry.IsEmpty)
            {
                codes.Add(GeoDatabase.CodeOf(entry));
            }
        }

        return codes;
    }

    /// <summary>Категория по имени без учёта регистра; <c>null</c> — такой нет.</summary>
    public static GeoSiteCategory? ReadCategory(ReadOnlySpan<byte> data, string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var entry = GeoDatabase.NextEntry(ref reader);
            if (!entry.IsEmpty && GeoDatabase.CodeOf(entry).Equals(code, StringComparison.OrdinalIgnoreCase))
            {
                return ParseSite(entry);
            }
        }

        return null;
    }

    private static GeoSiteCategory ParseSite(ReadOnlySpan<byte> entry)
    {
        var code = string.Empty;
        var domains = new List<GeoDomain>();
        var reader = new ProtoReader(entry);
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1:
                    ProtoReader.Expect(wire, 2);
                    code = reader.ReadString().ToLowerInvariant();
                    break;
                case 2:
                    ProtoReader.Expect(wire, 2);
                    domains.Add(ParseDomain(reader.ReadBytes()));
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new GeoSiteCategory(code, domains);
    }

    private static GeoDomain ParseDomain(ReadOnlySpan<byte> data)
    {
        ulong type = 0;
        var value = string.Empty;
        var attributes = new List<string>();
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1:
                    ProtoReader.Expect(wire, 0);
                    type = reader.ReadVarint();
                    break;
                case 2:
                    ProtoReader.Expect(wire, 2);
                    value = reader.ReadString();
                    break;
                case 3:
                    ProtoReader.Expect(wire, 2);
                    attributes.Add(AttributeKey(reader.ReadBytes()));
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (type > (ulong)GeoDomainType.Full || value.Length == 0)
        {
            throw new InvalidDataException("Invalid geosite domain rule.");
        }

        return new GeoDomain((GeoDomainType)type, value, new EquatableArray<string>([.. attributes]));
    }

    private static string AttributeKey(ReadOnlySpan<byte> data)
    {
        var reader = new ProtoReader(data);
        var key = string.Empty;
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            if (field == 1)
            {
                ProtoReader.Expect(wire, 2);
                key = reader.ReadString().ToLowerInvariant();
            }
            else
            {
                reader.Skip(wire);
            }
        }

        return key.Length > 0 ? key : throw new InvalidDataException("Geosite attribute without a key.");
    }
}

/// <summary>
/// Чтение <c>geoip.dat</c> (формат v2fly/Xray: GeoIPList → GeoIP → CIDR). Испорченный файл — <see cref="InvalidDataException"/>.
/// </summary>
public static class GeoIpDatabase
{
    /// <summary>Коды всех категорий в нижнем регистре, в порядке файла.</summary>
    public static IReadOnlyList<string> ListCategories(ReadOnlySpan<byte> data)
    {
        var codes = new List<string>();
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var entry = GeoDatabase.NextEntry(ref reader);
            if (!entry.IsEmpty)
            {
                codes.Add(GeoDatabase.CodeOf(entry));
            }
        }

        return codes;
    }

    /// <summary>Категория по коду без учёта регистра; <c>null</c> — такой нет.</summary>
    public static GeoIpCategory? ReadCategory(ReadOnlySpan<byte> data, string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var entry = GeoDatabase.NextEntry(ref reader);
            if (!entry.IsEmpty && GeoDatabase.CodeOf(entry).Equals(code, StringComparison.OrdinalIgnoreCase))
            {
                return ParseIp(entry);
            }
        }

        return null;
    }

    private static GeoIpCategory ParseIp(ReadOnlySpan<byte> entry)
    {
        var code = string.Empty;
        var networks = new List<IPNetwork>();
        var reverse = false;
        var reader = new ProtoReader(entry);
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1:
                    ProtoReader.Expect(wire, 2);
                    code = reader.ReadString().ToLowerInvariant();
                    break;
                case 2:
                    ProtoReader.Expect(wire, 2);
                    networks.Add(ParseCidr(reader.ReadBytes()));
                    break;
                case 3:
                    ProtoReader.Expect(wire, 0);
                    reverse = reader.ReadVarint() != 0;
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new GeoIpCategory(code, networks, reverse);
    }

    private static IPNetwork ParseCidr(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> ip = default;
        ulong prefix = 0;
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1:
                    ProtoReader.Expect(wire, 2);
                    ip = reader.ReadBytes();
                    break;
                case 2:
                    ProtoReader.Expect(wire, 0);
                    prefix = reader.ReadVarint();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (ip.Length is not (4 or 16) || prefix > (ulong)ip.Length * 8)
        {
            throw new InvalidDataException("Invalid geoip CIDR.");
        }

        // В файлах встречаются адреса с ненулевыми битами хоста — приводим к началу сети.
        var bytes = ip.ToArray();
        var bits = (int)prefix;
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(bits - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }

        return new IPNetwork(new IPAddress(bytes), bits);
    }
}

/// <summary>Общее для обоих файлов: верхний уровень — повторяющееся поле 1 с записями, у записи поле 1 — код.</summary>
internal static class GeoDatabase
{
    /// <summary>Следующая запись верхнего уровня; пустой span — поле не запись (пропущено).</summary>
    public static ReadOnlySpan<byte> NextEntry(ref ProtoReader reader)
    {
        var (field, wire) = reader.ReadTag();
        if (field != 1)
        {
            reader.Skip(wire);
            return default;
        }

        ProtoReader.Expect(wire, 2);
        return reader.ReadBytes();
    }

    /// <summary>Код записи (поле 1) в нижнем регистре без разбора остального.</summary>
    public static string CodeOf(ReadOnlySpan<byte> entry)
    {
        var reader = new ProtoReader(entry);
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            if (field == 1)
            {
                ProtoReader.Expect(wire, 2);
                return reader.ReadString().ToLowerInvariant();
            }

            reader.Skip(wire);
        }

        throw new InvalidDataException("Geo database entry without a code.");
    }
}
