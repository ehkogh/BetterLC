using System.Text;

namespace BetterLC;

internal static class ProtoWire
{
    public static byte[] GetBytes(ReadOnlySpan<byte> message, int wanted)
    {
        var offset = 0;
        while (offset < message.Length)
        {
            var key = ReadVarint(message, ref offset);
            var field = (int)(key >> 3);
            var type = (int)(key & 7);
            if (field == 0) throw new InvalidDataException("Invalid protobuf field.");
            if (type == 2)
            {
                var length = checked((int)ReadVarint(message, ref offset));
                if (length > message.Length - offset) throw new InvalidDataException("Truncated protobuf field.");
                if (field == wanted) return message.Slice(offset, length).ToArray();
                offset += length;
            }
            else if (type == 0) _ = ReadVarint(message, ref offset);
            else if (type == 1) offset += 8;
            else if (type == 5) offset += 4;
            else throw new InvalidDataException("Unsupported protobuf field.");
            if (offset > message.Length) throw new InvalidDataException("Truncated protobuf field.");
        }
        return [];
    }

    public static string GetString(ReadOnlySpan<byte> message, int field) =>
        Encoding.UTF8.GetString(GetBytes(message, field));

    public static List<byte[]> GetRepeated(ReadOnlySpan<byte> message, int wanted)
    {
        var values = new List<byte[]>();
        var offset = 0;
        while (offset < message.Length)
        {
            var key = ReadVarint(message, ref offset);
            var field = (int)(key >> 3);
            var type = (int)(key & 7);
            if (field == 0) throw new InvalidDataException("Invalid protobuf field.");
            if (type == 2)
            {
                var length = checked((int)ReadVarint(message, ref offset));
                if (length > message.Length - offset) throw new InvalidDataException("Truncated protobuf field.");
                if (field == wanted) values.Add(message.Slice(offset, length).ToArray());
                offset += length;
            }
            else if (type == 0) _ = ReadVarint(message, ref offset);
            else if (type == 1) offset += 8;
            else if (type == 5) offset += 4;
            else throw new InvalidDataException("Unsupported protobuf field.");
            if (offset > message.Length) throw new InvalidDataException("Truncated protobuf field.");
        }
        return values;
    }

    public static byte[] Renumber(ReadOnlySpan<byte> message, IReadOnlyDictionary<int, int> mapping)
    {
        using var output = new MemoryStream();
        var offset = 0;
        while (offset < message.Length)
        {
            var keyStart = offset;
            var key = ReadVarint(message, ref offset);
            var valueStart = offset;
            var field = (int)(key >> 3);
            var type = (int)(key & 7);
            if (field == 0) throw new InvalidDataException("Invalid protobuf field.");
            if (type == 2)
            {
                var length = checked((int)ReadVarint(message, ref offset));
                if (length > message.Length - offset) throw new InvalidDataException("Truncated protobuf field.");
                offset += length;
            }
            else if (type == 0) _ = ReadVarint(message, ref offset);
            else if (type == 1) offset += 8;
            else if (type == 5) offset += 4;
            else throw new InvalidDataException("Unsupported protobuf field.");
            if (offset > message.Length) throw new InvalidDataException("Truncated protobuf field.");
            if (!mapping.TryGetValue(field, out var target)) continue;
            WriteVarint(output, (ulong)(target << 3 | type));
            output.Write(message[valueStart..offset]);
        }
        return output.ToArray();
    }

    public static byte[] Message(params (int Field, byte[] Data)[] fields)
    {
        using var stream = new MemoryStream();
        foreach (var (field, data) in fields)
        {
            WriteVarint(stream, (ulong)(field << 3 | 2));
            WriteVarint(stream, (ulong)data.Length);
            stream.Write(data);
        }
        return stream.ToArray();
    }

    public static byte[] VarintField(int field, ulong value)
    {
        using var stream = new MemoryStream();
        WriteVarint(stream, (ulong)(field << 3));
        WriteVarint(stream, value);
        return stream.ToArray();
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset >= data.Length) throw new InvalidDataException("Truncated varint.");
            var part = data[offset++];
            if (shift == 63 && part > 1) throw new InvalidDataException("Oversized varint.");
            value |= (ulong)(part & 0x7F) << shift;
            if ((part & 0x80) == 0) return value;
        }
        throw new InvalidDataException("Oversized varint.");
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }
}
