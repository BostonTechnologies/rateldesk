using System.Text;

namespace Helpdesk.Mcp.Tools;

/// <summary>Filters newline-delimited stdio frames before SDK parameter binding.</summary>
public sealed class McpDuplicateArgumentsStream(Stream input) : Stream
{
    private readonly StreamReader _reader = new(input, new UTF8Encoding(false), false, 4096, leaveOpen: true);
    private byte[] _frame = [];
    private int _offset;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        if (_offset == _frame.Length && !Load(_reader.ReadLine())) return 0;
        var length = Math.Min(buffer.Length, _frame.Length - _offset);
        _frame.AsSpan(_offset, length).CopyTo(buffer);
        _offset += length;
        return length;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        if (_offset == _frame.Length && !Load(await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false))) return 0;
        var length = Math.Min(buffer.Length, _frame.Length - _offset);
        _frame.AsMemory(_offset, length).CopyTo(buffer);
        _offset += length;
        return length;
    }

    private bool Load(string? line)
    {
        if (line is null) return false;
        var raw = Encoding.UTF8.GetBytes(line);
        if (McpDuplicateArgumentsGuard.TryNormalize(raw, out var normalized)) raw = normalized;
        _frame = new byte[raw.Length + 1];
        raw.CopyTo(_frame, 0);
        _frame[^1] = (byte)'\n';
        _offset = 0;
        return true;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _reader.Dispose();
        base.Dispose(disposing);
    }
}
