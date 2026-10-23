using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RaidActions.Protocol;

public sealed class ProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>JSON for one message: camelCase, enums as camelCase strings, the <c>type</c> anywhere in the object.</summary>
public static class MessageCodec
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static string Serialize(Message m) => JsonSerializer.Serialize(m, Json);

    /// <summary>One message, or a <see cref="ProtocolException"/> ("badMessage" / "versionMismatch") that says what is wrong.</summary>
    public static Message Deserialize(string line)
    {
        Message? m;
        try { m = JsonSerializer.Deserialize<Message>(line, Json); }
        catch (JsonException e) { throw new ProtocolException("badMessage", $"Not a valid message: {e.Message}"); }
        catch (NotSupportedException e) { throw new ProtocolException("badMessage", $"Not a valid message: {e.Message}"); }
        if (m is null) throw new ProtocolException("badMessage", "Empty message.");
        if (m.V != ProtocolInfo.Version)
            throw new ProtocolException("versionMismatch", $"Protocol version {m.V}; this side speaks {ProtocolInfo.Version}.");
        return m;
    }
}

/// <summary>Newline-delimited messages over any stream (the named pipe, or a memory stream in tests). Not thread-safe per direction.</summary>
public sealed class MessageChannel(Stream stream) : IDisposable
{
    private static readonly byte[] Newline = "\n"u8.ToArray();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _buffer = new byte[16 * 1024];
    private readonly MemoryStream _line = new();
    private int _start, _end;

    public async Task WriteAsync(Message m, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(MessageCodec.Serialize(m));
        if (bytes.Length > ProtocolInfo.MaxLineBytes) throw new ProtocolException("badMessage", "Message too long.");
        await _writeLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(bytes, ct);
            await stream.WriteAsync(Newline, ct);
            // Pipe writes are not buffered, and flushing a pipe (FlushFileBuffers) blocks until the other side has READ the data,
            // which deadlocks a peer that writes before it reads. Other streams may buffer, so they are flushed.
            if (stream is not System.IO.Pipes.PipeStream) await stream.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>The next message, or null at the end of the stream. A bad line throws <see cref="ProtocolException"/>; the next read continues after it.</summary>
    public async Task<Message?> ReadAsync(CancellationToken ct = default)
    {
        var line = await ReadLineAsync(ct);
        return line is null ? null : MessageCodec.Deserialize(line);
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        _line.SetLength(0);
        var tooLong = false;
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await stream.ReadAsync(_buffer, ct);
                if (_end == 0) return _line.Length > 0 && !tooLong ? Decode() : null;
            }
            var nl = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            var take = (nl < 0 ? _end : nl) - _start;
            if (!tooLong)
            {
                _line.Write(_buffer, _start, take);
                if (_line.Length > ProtocolInfo.MaxLineBytes) { tooLong = true; _line.SetLength(0); }
            }
            _start += take;
            if (nl < 0) continue;
            _start++; // the newline
            if (tooLong) throw new ProtocolException("badMessage", $"Message longer than {ProtocolInfo.MaxLineBytes} bytes.");
            if (_line.Length == 0) continue; // blank line
            return Decode();
        }
    }

    private string Decode()
    {
        var s = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length);
        return s.EndsWith('\r') ? s[..^1] : s;
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        _line.Dispose();
    }
}
