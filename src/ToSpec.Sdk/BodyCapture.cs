namespace ToSpec.Sdk;

/// <summary>
/// A write-through stream that forwards every write to the real response body while
/// copying up to a byte cap into an in-memory buffer for capture. It never delays or
/// alters what the client receives — the client's bytes flow straight through; the copy
/// is bounded so a large response cannot grow SDK memory. <see cref="TotalBytes"/> is the
/// true forwarded length (used for <c>resp_size</c>) even when only the first
/// <see cref="_cap"/> bytes were captured.
/// </summary>
internal sealed class TeeStream(Stream inner, int cap) : Stream
{
    private readonly MemoryStream _captured = new();
    private long _total;

    public byte[] CapturedBytes => _captured.ToArray();

    public long TotalBytes => _total;

    private void Capture(ReadOnlySpan<byte> data)
    {
        _total += data.Length;
        int remaining = cap - (int)_captured.Length;
        if (remaining > 0)
        {
            _captured.Write(data[..Math.Min(remaining, data.Length)]);
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Capture(buffer.AsSpan(offset, count));
        inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Capture(buffer);
        inner.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Capture(buffer.Span);
        await inner.WriteAsync(buffer, cancellationToken);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Capture(buffer.AsSpan(offset, count));
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override bool CanWrite => true;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _captured.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Reads a bounded copy of the request body without consuming it from the app:
/// enables buffering, reads up to <paramref name="cap"/> bytes, then rewinds so the
/// downstream pipeline still sees the full body.</summary>
internal sealed class ReadTeeStream(Stream inner, int cap) : Stream
{
    private readonly MemoryStream _captured = new();
    private long _sequentialPosition;

    public byte[] CapturedBytes => _captured.ToArray();

    private void Capture(ReadOnlySpan<byte> data, long sourcePosition)
    {
        if (sourcePosition >= cap || data.IsEmpty)
        {
            return;
        }

        int take = Math.Min(data.Length, cap - (int)sourcePosition);
        _captured.Position = sourcePosition;
        _captured.Write(data[..take]);
        _sequentialPosition = Math.Max(_sequentialPosition, sourcePosition + data.Length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long position = inner.CanSeek ? inner.Position : _sequentialPosition;
        int read = inner.Read(buffer, offset, count);
        Capture(buffer.AsSpan(offset, read), position);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        long position = inner.CanSeek ? inner.Position : _sequentialPosition;
        int read = inner.Read(buffer);
        Capture(buffer[..read], position);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        long position = inner.CanSeek ? inner.Position : _sequentialPosition;
        int read = await inner.ReadAsync(buffer, cancellationToken);
        Capture(buffer.Span[..read], position);
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        long position = inner.CanSeek ? inner.Position : _sequentialPosition;
        int read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        Capture(buffer.AsSpan(offset, read), position);
        return read;
    }

    public override int ReadByte()
    {
        long position = inner.CanSeek ? inner.Position : _sequentialPosition;
        int value = inner.ReadByte();
        if (value >= 0)
        {
            Span<byte> one = stackalloc byte[1] { (byte)value };
            Capture(one, position);
        }

        return value;
    }

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _captured.Dispose();
        }

        // The request owns the inner stream. Restoring it in middleware keeps its
        // lifetime independent from this bounded capture wrapper.
        base.Dispose(disposing);
    }
}
