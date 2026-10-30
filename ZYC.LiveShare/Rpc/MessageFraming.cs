using System.Globalization;
using System.Text;

namespace ZYC.LiveShare.Rpc;

/// <summary>
///     Reads messages framed as <c>Content-Length: N\r\n\r\n</c> followed by N bytes (the same framing as LSP).
/// </summary>
internal sealed class MessageReader(Stream stream)
{
    private const int MaxHeaderLength = 8 * 1024;
    private const int MaxMessageLength = 64 * 1024 * 1024;

    private readonly byte[] buffer = new byte[64 * 1024];
    private int end;
    private int start;

    /// <returns>The message body, or null at end of stream.</returns>
    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        int? contentLength = null;
        while (true)
        {
            var line = await ReadHeaderLineAsync(cancellationToken);
            if (line is null)
            {
                return contentLength is null
                    ? null
                    : throw new EndOfStreamException("Stream ended inside message headers.");
            }

            if (line.Length == 0)
            {
                if (contentLength is null)
                {
                    throw new InvalidDataException("Message has no Content-Length header.");
                }

                break;
            }

            var colon = line.IndexOf(':');
            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                contentLength = int.Parse(line.AsSpan(colon + 1).Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture);
                if (contentLength > MaxMessageLength)
                {
                    throw new InvalidDataException($"Message of {contentLength} bytes exceeds the limit.");
                }
            }
        }

        var body = new byte[contentLength.Value];
        var buffered = Math.Min(end - start, body.Length);
        buffer.AsSpan(start, buffered).CopyTo(body);
        start += buffered;
        if (buffered < body.Length)
        {
            await stream.ReadExactlyAsync(body.AsMemory(buffered), cancellationToken);
        }

        return body;
    }

    private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var newline = buffer.AsSpan(start, end - start).IndexOf("\r\n"u8);
            if (newline >= 0)
            {
                var line = Encoding.ASCII.GetString(buffer, start, newline);
                start += newline + 2;
                return line;
            }

            if (end - start >= MaxHeaderLength)
            {
                throw new InvalidDataException("Message header line is too long.");
            }

            if (start > 0)
            {
                buffer.AsSpan(start, end - start).CopyTo(buffer);
                end -= start;
                start = 0;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(end), cancellationToken);
            if (read == 0)
            {
                return end == start ? null : throw new EndOfStreamException("Stream ended inside a header line.");
            }

            end += read;
        }
    }
}

internal static class MessageWriter
{
    public static byte[] Frame(ReadOnlySpan<byte> body)
    {
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var frame = new byte[header.Length + body.Length];
        header.CopyTo(frame, 0);
        body.CopyTo(frame.AsSpan(header.Length));
        return frame;
    }
}