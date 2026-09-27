namespace Noof.Ledger.Telegram;

// Thrown by SizeLimitedBuffer as soon as more than its limit has been received - never once the
// whole oversized download has already been buffered.
internal sealed class StreamSizeLimitExceededException(long bytesReceived) : Exception
{
    public long BytesReceived { get; } = bytesReceived;
}

// A write-only destination for Stream.CopyToAsync (what Telegram.Bot's DownloadFile ultimately calls)
// that counts bytes as they arrive and throws the moment the running total passes maxBytes, before
// that chunk ever reaches the inner MemoryStream - so the buffer itself never holds more than
// maxBytes, regardless of what the caller claimed about the file's size up front.
internal sealed class SizeLimitedBuffer(long maxBytes) : Stream
{
    readonly MemoryStream inner = new();
    long bytesReceived;

    public byte[] ToArray() => inner.ToArray();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Account(buffer.Length);
        inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Account(buffer.Length);
        return inner.WriteAsync(buffer, cancellationToken);
    }

    void Account(int count)
    {
        bytesReceived += count;
        if (bytesReceived > maxBytes)
            throw new StreamSizeLimitExceededException(bytesReceived);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
