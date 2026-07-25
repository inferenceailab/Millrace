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
        if (!TryDecodeBase64Url(cursor, payload, out var written) || written != PayloadLength)
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
    /// Encodes an ordering key whose tiebreak is a consumer-chosen string rather than an id.
    /// </summary>
    /// <remarks>
    /// Recurring definitions are keyed by a caller-supplied string, so the <see cref="Guid"/> form
    /// above does not fit them. The timestamp stays fixed-width and leading, keeping the encoding
    /// order-preserving; the id follows as UTF-8.
    /// </remarks>
    public static string Encode(DateTimeOffset timestamp, string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        var idBytes = System.Text.Encoding.UTF8.GetBytes(id);
        var payload = new byte[8 + idBytes.Length];
        BinaryPrimitives.WriteInt64BigEndian(payload, timestamp.UtcTicks);
        idBytes.CopyTo(payload, 8);
        return Base64Url.EncodeToString(payload);
    }

    /// <summary>
    /// Decodes a cursor produced by <see cref="Encode(DateTimeOffset, string)"/>.
    /// </summary>
    /// <remarks>
    /// Named distinctly rather than overloading <see cref="TryDecode(string?, out DateTimeOffset, out Guid)"/>:
    /// two overloads differing only in an <c>out</c> type are ambiguous at any call site using
    /// <c>out var</c>, which is how most callers would write it.
    /// </remarks>
    public static bool TryDecodeStringId(string? cursor, out DateTimeOffset timestamp, out string id)
    {
        timestamp = default;
        id = string.Empty;

        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        byte[] payload;
        int written;
        try
        {
            payload = new byte[Base64Url.GetMaxDecodedLength(cursor.Length)];
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!TryDecodeBase64Url(cursor, payload, out written) || written < 8)
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(payload);
        if (ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        timestamp = new DateTimeOffset(ticks, TimeSpan.Zero);
        id = System.Text.Encoding.UTF8.GetString(payload, 8, written - 8);
        return true;
    }

    /// <summary>
    /// Decodes base64url, treating malformed input as a failure rather than an exception.
    /// </summary>
    /// <remarks>
    /// <see cref="Base64Url.TryDecodeFromChars(ReadOnlySpan{char}, Span{byte}, out int)"/> returns
    /// false for a well-formed string of the wrong length but <em>throws</em>
    /// <see cref="FormatException"/> when the input contains a non-base64url character. Cursors
    /// arrive from HTTP clients, so an arbitrary query-string value must produce a rejected cursor —
    /// the contract's <c>MillraceStorageException</c> — not an unhandled exception and a 500.
    /// </remarks>
    private static bool TryDecodeBase64Url(ReadOnlySpan<char> source, Span<byte> destination, out int written)
    {
        try
        {
            return Base64Url.TryDecodeFromChars(source, destination, out written);
        }
        catch (FormatException)
        {
            written = 0;
            return false;
        }
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
