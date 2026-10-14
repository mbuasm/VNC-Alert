using System.Buffers.Binary;
using MaszynyAlert.Core.Rfb.Crypto;

namespace MaszynyAlert.Core.Rfb;

internal sealed class RsaAesStream : Stream
{
    /// <summary>Największa porcja tekstu jawnego w jednej wiadomości (długość mieści się w 2 bajtach).</summary>
    public const int MaxMessageBytes = 8192;

    private readonly Stream _inner;
    private readonly AesEax _readCipher;
    private readonly AesEax _writeCipher;
    private readonly byte[] _readCounter = new byte[AesEax.BlockSize];
    private readonly byte[] _writeCounter = new byte[AesEax.BlockSize];
    private readonly string _endpointDescription;

    private byte[] _plaintext = [];
    private int _plaintextOffset;
    private bool _disposed;

    public RsaAesStream(Stream inner, byte[] readKey, byte[] writeKey, string endpointDescription)
    {
        _inner = inner;
        _readCipher = new AesEax(readKey);
        _writeCipher = new AesEax(writeKey);
        _endpointDescription = endpointDescription;
    }

    /// <summary>
    /// Czy w buforze leży jeszcze odszyfrowany tekst jawny. Potrzebne, bo <c>RfbClient</c> czeka na
    /// odpowiedź serwera, odpytując <c>NetworkStream.DataAvailable</c> — a tam danych może już nie być,
    /// mimo że cała wiadomość została odczytana z gniazda i czeka tu odszyfrowana.
    /// </summary>
    public bool HasBufferedPlaintext => _plaintextOffset < _plaintext.Length;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        if (!HasBufferedPlaintext && !await ReadMessageAsync(ct).ConfigureAwait(false))
        {
            return 0; // koniec strumienia — RfbBinaryReader zamieni to na czytelny komunikat PL
        }

        int count = Math.Min(buffer.Length, _plaintext.Length - _plaintextOffset);
        _plaintext.AsMemory(_plaintextOffset, count).CopyTo(buffer);
        _plaintextOffset += count;
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int count = Math.Min(MaxMessageBytes, buffer.Length - offset);
            await WriteMessageAsync(buffer.Slice(offset, count), ct).ConfigureAwait(false);
            offset += count;
        }
    }

    private async Task WriteMessageAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct)
    {
        byte[] message = new byte[2 + plaintext.Length + AesEax.TagSize];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(0, 2), (ushort)plaintext.Length);

        _writeCipher.Encrypt(
            _writeCounter,
            message.AsSpan(0, 2),
            plaintext.Span,
            message.AsSpan(2, plaintext.Length),
            message.AsSpan(2 + plaintext.Length, AesEax.TagSize));

        IncrementLittleEndian(_writeCounter);
        await _inner.WriteAsync(message, ct).ConfigureAwait(false);
    }

    private async Task<bool> ReadMessageAsync(CancellationToken ct)
    {
        byte[] header = new byte[2];
        if (!await ReadExactlyOrEndAsync(header, ct).ConfigureAwait(false))
        {
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(header);
        byte[] payload = new byte[length + AesEax.TagSize];
        if (!await ReadExactlyOrEndAsync(payload, ct).ConfigureAwait(false))
        {
            return false;
        }

        byte[] plaintext = new byte[length];
        bool ok = _readCipher.TryDecrypt(
            _readCounter,
            header,
            payload.AsSpan(0, length),
            payload.AsSpan(length, AesEax.TagSize),
            plaintext);

        if (!ok)
        {
            throw new RfbProtocolException(
                $"Serwer {_endpointDescription} przysłał wiadomość RSA-AES z niepoprawnym znacznikiem " +
                "uwierzytelniającym (uszkodzone dane albo obcy nadawca).");
        }

        IncrementLittleEndian(_readCounter);
        _plaintext = plaintext;
        _plaintextOffset = 0;
        return true;
    }

    private async Task<bool> ReadExactlyOrEndAsync(byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await _inner.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    /// <summary>Licznik liczb jednorazowych rośnie od najmłodszego bajtu (little-endian).</summary>
    private static void IncrementLittleEndian(byte[] counter)
    {
        for (int i = 0; i < counter.Length; i++)
        {
            if (++counter[i] != 0)
            {
                break;
            }
        }
    }

    // ---- Stream: tylko asynchroniczna część jest używana przez klienta RFB --------------------

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _readCipher.Dispose();
            _writeCipher.Dispose();
        }

        base.Dispose(disposing);
    }
}
