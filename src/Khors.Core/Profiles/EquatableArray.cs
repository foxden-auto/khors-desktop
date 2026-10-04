using System.Collections;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Khors.Core.Profiles;

/// <summary>
/// Неизменяемый список со сравнением по содержимому — чтобы записи модели с коллекциями
/// сохраняли семантику равенства record. <c>default</c> — пустой список.
/// </summary>
[JsonConverter(typeof(EquatableArrayJsonConverterFactory))]
public readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
{
    private readonly ImmutableArray<T> _items;

    public EquatableArray(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = [.. items];
    }

    private ImmutableArray<T> Items => _items.IsDefault ? [] : _items;

    public int Count => Items.Length;

    public T this[int index] => Items[index];

    public bool Equals(EquatableArray<T> other) => Items.SequenceEqual(other.Items);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => "[" + string.Join(", ", Items) + "]";

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

    public static implicit operator EquatableArray<T>(T[] items) => new(items);
}

internal sealed class EquatableArrayJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(EquatableArray<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(EquatableArrayJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}

internal sealed class EquatableArrayJsonConverter<T> : JsonConverter<EquatableArray<T>>
{
    public override EquatableArray<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var typeInfo = options.GetTypeInfo(typeof(T[]));
        var items = (T[]?)JsonSerializer.Deserialize(ref reader, typeInfo);
        return items is null ? default : new EquatableArray<T>(items);
    }

    public override void Write(Utf8JsonWriter writer, EquatableArray<T> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToArray(), options.GetTypeInfo(typeof(T[])));
}
