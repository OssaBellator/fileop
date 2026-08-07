using System.Buffers.Binary;
using System.Text.Json;
using FileOp.Core.Indexing.Service;

namespace FileOp.Windows.IndexingService;

internal static class IndexingPipeTransport
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async ValueTask WriteAsync<T>(
        Stream stream,
        T message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        if (payload.Length > IndexingServiceProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException(
                $"Indexing service frame length {payload.Length:N0} exceeds the " +
                $"{IndexingServiceProtocol.MaximumFrameBytes:N0}-byte protocol limit.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T?> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        var hasHeader = await ReadExactlyOrEndAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (!hasHeader)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > IndexingServiceProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException($"Invalid indexing service frame length {length:N0}.");
        }

        var payload = new byte[length];
        if (!await ReadExactlyOrEndAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("The indexing service connection closed during a frame.");
        }

        return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
            ?? throw new InvalidDataException($"The indexing service frame did not contain a {typeof(T).Name} value.");
    }

    private static async ValueTask<bool> ReadExactlyOrEndAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("The indexing service connection closed during a frame.");
            }

            offset += read;
        }

        return true;
    }
}
