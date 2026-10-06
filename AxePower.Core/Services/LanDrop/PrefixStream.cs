using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services.LanDrop;

/// <summary>
/// A non-buffering stream that prefixes an existing stream with an initial byte array.
/// Allows streaming HTTP bodies when header parsing already consumed the first few body bytes.
/// </summary>
public sealed class PrefixStream : Stream
{
    private readonly byte[] _prefix;
    private int _prefixOffset;
    private readonly Stream _underlying;

    public PrefixStream(byte[] prefix, Stream underlying)
    {
        _prefix = prefix ?? Array.Empty<byte>();
        _underlying = underlying ?? throw new ArgumentNullException(nameof(underlying));
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_prefixOffset < _prefix.Length)
        {
            int toCopy = Math.Min(count, _prefix.Length - _prefixOffset);
            Array.Copy(_prefix, _prefixOffset, buffer, offset, toCopy);
            _prefixOffset += toCopy;
            return toCopy;
        }

        return _underlying.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_prefixOffset < _prefix.Length)
        {
            int toCopy = Math.Min(buffer.Length, _prefix.Length - _prefixOffset);
            _prefix.AsMemory(_prefixOffset, toCopy).CopyTo(buffer);
            _prefixOffset += toCopy;
            return toCopy;
        }

        return await _underlying.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_prefixOffset < _prefix.Length)
        {
            int toCopy = Math.Min(count, _prefix.Length - _prefixOffset);
            Array.Copy(_prefix, _prefixOffset, buffer, offset, toCopy);
            _prefixOffset += toCopy;
            return Task.FromResult(toCopy);
        }

        return _underlying.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _underlying.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _underlying.DisposeAsync();
        await base.DisposeAsync();
    }
}
