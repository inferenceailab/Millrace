using System.Buffers.Binary;
using System.Buffers.Text;

namespace Millrace.Storage.Monitoring;

/// <summary>
/// The keyset cursor encoding used by the bundled providers, offered publicly so third-party
/// providers get a correct one for free.
/// </summary>
/// <remarks>
/// <para>
/// Cursors are opaque <em>to callers</em> (§11.12) — a provider is free to encode its own. This
/// implementation packs the <c>(CreatedAt, Id)</c> ordering key as big-endian UTC ticks followed by
/// the id in RFC 4122 byte order, base64url-encoded so it survives a URL path or query string
/// untouched.
/// </para>
/// <para>
/// Because the bundled providers share this encoding they will decode one another's cursors. That
/// is harmless: a dashboard is bound to exactly one provider, so a cursor can never legitimately
/// cross between them. What matters is that an <em>undecodable</em> cursor is rejected rather than
/// silently treated as "start from the beginning", which would turn a client bug into an infinite
/// paging loop.
/// </para>
/// </remarks>
public static class MonitoringCursor
{
    private const int PayloadLength = 24; // 8-byte tick count + 16-byte id

    /// <summary>Encodes an ordering key into an opaque cursor.</summary>
    public static string Encode(DateTimeOffset createdAt, Guid id)
    {
        Span<byte> payload = stackalloc byte[PayloadLength];
        BinaryPrimitives.WriteInt64BigEndian(payload, createdAt.UtcTicks);
        // Big-endian (RFC 4122) so the byte order matches how a database orders a uuid column.
        id.TryWriteBytes(payload[8..], bigEndian: true, out _);
        return Base64Url.EncodeToString(payload);
    }

    /// <summary>
    /// Decodes a cursor produced by <see cref="Encode"/>. Returns <see langword="false"/> for
    /// anything else — malformed, truncated, or simply not a cursor.
    /// </summary>
    public static bool TryDecode(string? cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = default;

        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        Span<byte> payload = stackalloc byte[PayloadLength];
        if (!Base64Url.TryDecodeFromChars(cursor, payload, out var written) || written != PayloadLength)
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(payload);
        if (ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        id = new Guid(payload[8..], bigEndian: true);
        return true;
    }

    /// <summary>
    /// Orders ids the way a database orders a uuid column — by RFC 4122 byte order — so the
    /// in-memory provider and a relational one agree on the tiebreak.
    /// </summary>
    /// <remarks>
    /// <see cref="Guid.CompareTo(Guid)"/> does <em>not</em> do this: it compares the first three
    /// fields numerically in native endianness, which differs from byte order on little-endian
    /// machines. Ties are near-impossible with UUIDv7 ids minted from time, but "near-impossible"
    /// is not a contract, and the conformance kit asserts one order across every provider.
    /// </remarks>
    public static int CompareIds(Guid left, Guid right)
    {
        Span<byte> a = stackalloc byte[16];
        Span<byte> b = stackalloc byte[16];
        left.TryWriteBytes(a, bigEndian: true, out _);
        right.TryWriteBytes(b, bigEndian: true, out _);
        return a.SequenceCompareTo(b);
    }
}
