using System.Text;
using MaszynyAlert.Core.Cluster;

namespace MaszynyAlert.Core.Tests.Cluster;

public sealed class HmacSignerTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Payload = "wiadomość testowa klastra"u8.ToArray();

    [Fact]
    public void SealITryOpen_ZTymSamymKluczem_ZwracajaOryginalnaTresc()
    {
        var signer = new HmacSigner(Key);

        byte[] datagram = signer.Seal(Payload);
        bool ok = signer.TryOpen(datagram, out byte[] payload, out string? reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal(Payload, payload);
        Assert.Equal("MAL1", Encoding.ASCII.GetString(datagram, 0, 4));
        Assert.Equal(4 + 32 + Payload.Length, datagram.Length);
    }

    [Fact]
    public void TryOpen_GdyZmienionoJedenBajtTresci_OdrzucaPakietZPowodemHmac()
    {
        var signer = new HmacSigner(Key);
        byte[] datagram = signer.Seal(Payload);
        datagram[^1] ^= 0x01;

        bool ok = signer.TryOpen(datagram, out _, out string? reason);

        Assert.False(ok);
        Assert.NotNull(reason);
        Assert.Contains("HMAC", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryOpen_GdyZmienionoBajtPodpisu_OdrzucaPakiet()
    {
        var signer = new HmacSigner(Key);
        byte[] datagram = signer.Seal(Payload);
        datagram[10] ^= 0xFF;

        Assert.False(signer.TryOpen(datagram, out _, out _));
    }

    [Fact]
    public void TryOpen_PakietuPodpisanegoInnymKluczem_OdrzucaGo()
    {
        var a = new HmacSigner(HmacSigner.GenerateKey());
        var b = new HmacSigner(HmacSigner.GenerateKey());

        byte[] datagram = a.Seal(Payload);

        Assert.False(b.TryOpen(datagram, out _, out string? reason));
        Assert.Contains("HMAC", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryOpen_ZaKrotkiegoPakietu_OdrzucaGoZPowodemDlugosci()
    {
        var signer = new HmacSigner(Key);

        Assert.False(signer.TryOpen(new byte[10], out _, out string? reason));
        Assert.Contains("krótki", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryOpen_PakietuZeZlymNaglowkiem_OdrzucaGo()
    {
        var signer = new HmacSigner(Key);
        byte[] datagram = signer.Seal(Payload);
        datagram[0] = (byte)'X';

        Assert.False(signer.TryOpen(datagram, out _, out string? reason));
        Assert.Contains("nagłówek", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Konstruktor_ZKluczemKrotszymNiz16Bajtow_RzucaArgumentException()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => new HmacSigner(new byte[15]));
        Assert.Contains("za krótki", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Konstruktor_ZKluczem16Bajtow_JestAkceptowany()
    {
        var signer = new HmacSigner(new byte[16]);
        Assert.True(signer.TryOpen(signer.Seal(Payload), out _, out _));
    }

    [Fact]
    public void GenerateKey_ZwracaTrzydziestoDwuBajtowyLosowyKlucz()
    {
        byte[] a = HmacSigner.GenerateKey();
        byte[] b = HmacSigner.GenerateKey();

        Assert.Equal(32, a.Length);
        Assert.Equal(32, b.Length);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void FromBase64_IKeyBase64_TworzaPareOdwracalna()
    {
        var original = new HmacSigner(HmacSigner.GenerateKey());

        HmacSigner restored = HmacSigner.FromBase64(original.KeyBase64);

        Assert.Equal(original.KeyBase64, restored.KeyBase64);
        Assert.True(restored.TryOpen(original.Seal(Payload), out byte[] payload, out _));
        Assert.Equal(Payload, payload);
    }

    [Fact]
    public void FromBase64_ZNiepoprawnegoTekstu_RzucaArgumentException()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => HmacSigner.FromBase64("to nie jest base64!!!"));
        Assert.Contains("Base64", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Seal_PustejTresci_DzialaIDajeSieOtworzyc()
    {
        var signer = new HmacSigner(Key);

        Assert.True(signer.TryOpen(signer.Seal(ReadOnlySpan<byte>.Empty), out byte[] payload, out _));
        Assert.Empty(payload);
    }
}
