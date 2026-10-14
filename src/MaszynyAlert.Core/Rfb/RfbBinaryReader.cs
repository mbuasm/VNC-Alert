using System.Buffers.Binary;
using System.Net.Sockets;

namespace MaszynyAlert.Core.Rfb;

/// <summary>
/// Odczyt danych binarnych (big-endian, jak cały protokół RFB poza samymi pikselami) ze strumienia
/// sieciowego, z limitem czasu stosowanym do KAŻDEGO odczytu z osobna (nie tylko do całej operacji
/// wywołującej) i tłumaczeniem błędów IO/timeoutu na <see cref="RfbException"/> z komunikatem PL.
/// Używana zarówno przez <see cref="RfbClient"/> (handshake, nagłówki komunikatów), jak i przez
/// wewnętrzne dekodery (<c>RawDecoder</c>, <c>CopyRectDecoder</c>, <c>HextileDecoder</c>).
/// </summary>
internal sealed class RfbBinaryReader
{
    private readonly Stream _stream;
    private readonly TimeSpan _readTimeout;
    private readonly string _endpointDescription;

    public RfbBinaryReader(Stream stream, TimeSpan readTimeout, string endpointDescription)
    {
        _stream = stream;
        _readTimeout = readTimeout;
        _endpointDescription = endpointDescription;
    }

    /// <summary>Czyta dokładnie <paramref name="count"/> bajtów do <paramref name="buffer"/> od <paramref name="offset"/>.</summary>
    public async Task ReadExactlyAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (count == 0)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_readTimeout);

        try
        {
            int total = 0;
            while (total < count)
            {
                int read = await _stream.ReadAsync(buffer.AsMemory(offset + total, count - total), cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new RfbException($"Połączenie z {_endpointDescription} zostało zamknięte przez serwer w trakcie odczytu.");
                }

                total += read;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RfbException(
                $"Połączenie z {_endpointDescription} przerwane: przekroczono limit czasu odczytu ({_readTimeout.TotalSeconds:0}s).");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw new RfbException($"Połączenie z {_endpointDescription} przerwane: {ex.Message}", ex);
        }
    }

    /// <summary>Czyta dokładnie <paramref name="count"/> bajtów i zwraca je jako nową tablicę.</summary>
    public async Task<byte[]> ReadExactlyAsync(int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await ReadExactlyAsync(buffer, 0, count, ct).ConfigureAwait(false);
        return buffer;
    }

    /// <summary>Czyta i odrzuca <paramref name="count"/> bajtów (np. ServerCutText), bez alokowania dużego bufora naraz.</summary>
    public async Task SkipAsync(int count, CancellationToken ct)
    {
        if (count <= 0)
        {
            return;
        }

        byte[] scratch = new byte[Math.Min(count, 8192)];
        int remaining = count;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, scratch.Length);
            await ReadExactlyAsync(scratch, 0, chunk, ct).ConfigureAwait(false);
            remaining -= chunk;
        }
    }

    public async Task<byte> ReadByteAsync(CancellationToken ct)
    {
        byte[] buf = await ReadExactlyAsync(1, ct).ConfigureAwait(false);
        return buf[0];
    }

    public async Task<ushort> ReadUInt16Async(CancellationToken ct)
    {
        byte[] buf = await ReadExactlyAsync(2, ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt16BigEndian(buf);
    }

    public async Task<uint> ReadUInt32Async(CancellationToken ct)
    {
        byte[] buf = await ReadExactlyAsync(4, ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32BigEndian(buf);
    }

    public async Task<int> ReadInt32Async(CancellationToken ct)
    {
        byte[] buf = await ReadExactlyAsync(4, ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32BigEndian(buf);
    }
}
