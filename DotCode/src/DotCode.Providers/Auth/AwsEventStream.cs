using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace DotCode.Providers.Auth;

public sealed record AwsEventMessage(IReadOnlyDictionary<string, string> Headers, byte[] Payload)
{
    public string? MessageType => Headers.GetValueOrDefault(":message-type");
    public string? EventType => Headers.GetValueOrDefault(":event-type");
    public string? ExceptionType => Headers.GetValueOrDefault(":exception-type");
}

/// <summary>Decoder for the <c>application/vnd.amazon.eventstream</c> binary framing used by Bedrock streaming:
/// [total length][headers length][prelude CRC32][headers][payload][message CRC32], big-endian, CRC-32 (IEEE).</summary>
public static class AwsEventStream
{
    public static async IAsyncEnumerable<AwsEventMessage> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var prelude = new byte[12];
        while (true)
        {
            if (!await ReadExactlyAsync(stream, prelude, ct).ConfigureAwait(false)) yield break;
            var total = BinaryPrimitives.ReadInt32BigEndian(prelude.AsSpan(0, 4));
            var headersLength = BinaryPrimitives.ReadInt32BigEndian(prelude.AsSpan(4, 4));
            var preludeCrc = BinaryPrimitives.ReadUInt32BigEndian(prelude.AsSpan(8, 4));
            if (Crc32(prelude.AsSpan(0, 8)) != preludeCrc) throw new InvalidDataException("AWS event stream: prelude checksum mismatch");
            if (total < 16 || headersLength < 0 || headersLength > total - 16 || total > 16 * 1024 * 1024)
                throw new InvalidDataException($"AWS event stream: invalid frame length {total}");

            var message = new byte[total];
            prelude.CopyTo(message, 0);
            if (!await ReadExactlyAsync(stream, message.AsMemory(12), ct).ConfigureAwait(false))
                throw new InvalidDataException("AWS event stream: truncated frame");
            var messageCrc = BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(total - 4));
            if (Crc32(message.AsSpan(0, total - 4)) != messageCrc) throw new InvalidDataException("AWS event stream: message checksum mismatch");

            var headers = ParseHeaders(message.AsSpan(12, headersLength));
            var payload = message.AsSpan(12 + headersLength, total - 16 - headersLength).ToArray();
            yield return new AwsEventMessage(headers, payload);
        }
    }

    private static Dictionary<string, string> ParseHeaders(ReadOnlySpan<byte> span)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (i < span.Length)
        {
            int nameLength = span[i++];
            var name = Encoding.UTF8.GetString(span.Slice(i, nameLength));
            i += nameLength;
            var type = span[i++];
            switch (type)
            {
                case 0 or 1: headers[name] = (type == 0).ToString(); break;                    // bool true/false
                case 2: headers[name] = ((sbyte)span[i]).ToString(); i += 1; break;            // byte
                case 3: headers[name] = BinaryPrimitives.ReadInt16BigEndian(span[i..]).ToString(); i += 2; break;
                case 4: headers[name] = BinaryPrimitives.ReadInt32BigEndian(span[i..]).ToString(); i += 4; break;
                case 5 or 8: headers[name] = BinaryPrimitives.ReadInt64BigEndian(span[i..]).ToString(); i += 8; break; // long / timestamp
                case 6 or 7:                                                                    // bytes / string
                {
                    var length = BinaryPrimitives.ReadUInt16BigEndian(span[i..]);
                    i += 2;
                    headers[name] = type == 7 ? Encoding.UTF8.GetString(span.Slice(i, length)) : Convert.ToBase64String(span.Slice(i, length));
                    i += length;
                    break;
                }
                case 9: headers[name] = new Guid(span.Slice(i, 16), bigEndian: true).ToString(); i += 16; break;
                default: throw new InvalidDataException($"AWS event stream: unknown header type {type}");
            }
        }
        return headers;
    }

    /// <summary>Encodes one frame (used by tests and mock servers).</summary>
    public static byte[] Encode(IReadOnlyDictionary<string, string> headers, byte[] payload)
    {
        using var h = new MemoryStream();
        foreach (var (name, value) in headers)
        {
            var n = Encoding.UTF8.GetBytes(name);
            var v = Encoding.UTF8.GetBytes(value);
            h.WriteByte((byte)n.Length);
            h.Write(n);
            h.WriteByte(7);
            h.WriteByte((byte)(v.Length >> 8));
            h.WriteByte((byte)v.Length);
            h.Write(v);
        }
        var headerBytes = h.ToArray();
        var total = 16 + headerBytes.Length + payload.Length;
        var frame = new byte[total];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0), total);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4), headerBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), Crc32(frame.AsSpan(0, 8)));
        headerBytes.CopyTo(frame, 12);
        payload.CopyTo(frame, 12 + headerBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(total - 4), Crc32(frame.AsSpan(0, total - 4)));
        return frame;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new InvalidDataException("AWS event stream: connection closed mid-frame");
            }
            read += n;
        }
        return true;
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
