using System.Text;

namespace Khors.Core.Geo;

/// <summary>
/// Минимальный разбор protobuf для гео-баз: поля varint и length-delimited, остальные типы пропускаются.
/// Ошибка формата — <see cref="InvalidDataException"/>.
/// </summary>
internal ref struct ProtoReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public readonly bool End => _position >= _data.Length;

    /// <summary>Следующий тег: номер поля и тип.</summary>
    public (int Field, int WireType) ReadTag()
    {
        var tag = ReadVarint();
        var field = tag >> 3;
        if (field is 0 or > int.MaxValue)
        {
            throw new InvalidDataException("Invalid protobuf field number.");
        }

        return ((int)field, (int)(tag & 7));
    }

    public ulong ReadVarint()
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (_position >= _data.Length)
            {
                throw new InvalidDataException("Truncated protobuf varint.");
            }

            var b = _data[_position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("Protobuf varint is too long.");
    }

    /// <summary>Содержимое поля length-delimited (вложенное сообщение, строка, байты).</summary>
    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = ReadVarint();
        if (length > (ulong)(_data.Length - _position))
        {
            throw new InvalidDataException("Truncated protobuf field.");
        }

        var bytes = _data.Slice(_position, (int)length);
        _position += (int)length;
        return bytes;
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint();
                break;
            case 1:
                Advance(8);
                break;
            case 2:
                ReadBytes();
                break;
            case 5:
                Advance(4);
                break;
            default:
                throw new InvalidDataException($"Unsupported protobuf wire type {wireType}.");
        }
    }

    /// <summary>Проверка типа поля: другой тип у известного поля — испорченный файл.</summary>
    public static void Expect(int wireType, int expected)
    {
        if (wireType != expected)
        {
            throw new InvalidDataException($"Unexpected protobuf wire type {wireType}, expected {expected}.");
        }
    }

    private void Advance(int count)
    {
        if (count > _data.Length - _position)
        {
            throw new InvalidDataException("Truncated protobuf field.");
        }

        _position += count;
    }
}
