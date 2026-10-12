using System.Buffers;

namespace MethodCache.HttpCaching.Internal;

/// <summary>
/// Owns the final array for a Content-Length of at most <see cref="MaxPreallocation"/>, or rents temporary storage
/// for an unknown or larger length.
/// </summary>
internal sealed class ResponseBodyBuffer : IDisposable
{
    /// <summary>
    /// The largest body allocated up front from Content-Length alone. The header is not verified until the body is
    /// read, so a lying or bodiless response (HEAD) must not be able to force a larger allocation; larger bodies grow
    /// in pooled storage as their bytes actually arrive.
    /// </summary>
    internal const int MaxPreallocation = 1024 * 1024;
    private const int MinimumRental = 16_384;
    private readonly ArrayPool<byte> _pool;
    private byte[] _bytes;
    private bool _rented;

    internal ResponseBodyBuffer(long? contentLength, long limit, ArrayPool<byte> pool)
    {
        _pool = pool;
        if (contentLength is >= 0 && contentLength <= MaxPreallocation && contentLength <= limit)
        {
            _bytes = contentLength == 0 ? Array.Empty<byte>() : new byte[(int)contentLength.Value];
        }
        else
        {
            // Start a large declared length at the pre-allocation bound: pooled, and grown only as bytes arrive.
            var initial = Math.Clamp(contentLength ?? MinimumRental, MinimumRental, MaxPreallocation);
            _bytes = pool.Rent((int)Math.Min(initial, limit < Array.MaxLength ? limit + 1 : Array.MaxLength));
            _rented = true;
        }
    }

    internal int Length { get; set; }
    internal int Capacity => _bytes.Length;
    internal Memory<byte> Available => _bytes.AsMemory(Length);

    internal void Append(byte value, long limit)
    {
        if (Length == Capacity)
        {
            var requested = (int)Math.Min(Math.Max(1L, Capacity * 2L), Math.Min(Array.MaxLength, limit < Array.MaxLength ? limit + 1 : Array.MaxLength));
            var replacement = _pool.Rent(requested);
            _bytes.AsSpan(0, Length).CopyTo(replacement);
            ReturnRental();
            _bytes = replacement;
            _rented = true;
        }
        _bytes[Length++] = value;
    }

    internal byte[] ToOwnedArray() => !_rented && Length == Capacity ? _bytes : _bytes.AsSpan(0, Length).ToArray();

    internal Stream DetachStream()
    {
        var stream = _rented
            ? new RentalStream(_bytes, Length, _pool)
            : new MemoryStream(_bytes, 0, Length, writable: false);
        _rented = false;
        _bytes = Array.Empty<byte>();
        return stream;
    }

    public void Dispose() => ReturnRental();

    private void ReturnRental()
    {
        if (!_rented) return;
        _rented = false;
        _pool.Return(_bytes, clearArray: true);
    }

    private sealed class RentalStream(byte[] bytes, int length, ArrayPool<byte> pool) : MemoryStream(bytes, 0, length, writable: false)
    {
        private byte[]? _rental = bytes;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Interlocked.Exchange(ref _rental, null) is { } rental)
                pool.Return(rental, clearArray: true);
        }
    }
}
