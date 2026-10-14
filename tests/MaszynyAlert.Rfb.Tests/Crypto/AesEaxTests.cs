using MaszynyAlert.Core.Rfb.Crypto;

namespace MaszynyAlert.Rfb.Tests.Crypto;

public sealed class AesEaxTests
{
    [Theory]
    [InlineData(
        "233952DEE4D5ED5F9B9C6D6FF80FF478", "62EC67F9C3A4A407FCB2A8C49031A8B3",
        "6BFB914FD07EAE6B", "", "", "E037830E8389F27B025A2D6527E79D01")]
    [InlineData(
        "91945D3F4DCBEE0BF45EF52255F095A4", "BECAF043B0A23D843194BA972C66DEBD",
        "FA3BFD4806EB53FA", "F7FB", "19DD", "5C4C9331049D0BDAB0277408F67967E5")]
    [InlineData(
        "01F74AD64077F2E704C0F60ADA3DD523", "70C3DB4F0D26368400A10ED05D2BFF5E",
        "234A3463C1264AC6", "1A47CB4933", "D851D5BAE0", "3A59F238A23E39199DC9266626C40F80")]
    public void Encrypt_ZgadzaSieZWektoramiPracyOEax(
        string keyHex, string nonceHex, string headerHex, string plaintextHex, string cipherHex, string tagHex)
    {
        using var eax = new AesEax(Convert.FromHexString(keyHex));
        byte[] plaintext = Convert.FromHexString(plaintextHex);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[AesEax.TagSize];

        eax.Encrypt(
            Convert.FromHexString(nonceHex), Convert.FromHexString(headerHex), plaintext, ciphertext, tag);

        Assert.Equal(cipherHex.ToUpperInvariant(), Convert.ToHexString(ciphertext));
        Assert.Equal(tagHex.ToUpperInvariant(), Convert.ToHexString(tag));
    }

    [Theory]
    [InlineData(
        "233952DEE4D5ED5F9B9C6D6FF80FF478", "62EC67F9C3A4A407FCB2A8C49031A8B3",
        "6BFB914FD07EAE6B", "", "", "E037830E8389F27B025A2D6527E79D01")]
    [InlineData(
        "91945D3F4DCBEE0BF45EF52255F095A4", "BECAF043B0A23D843194BA972C66DEBD",
        "FA3BFD4806EB53FA", "F7FB", "19DD", "5C4C9331049D0BDAB0277408F67967E5")]
    [InlineData(
        "01F74AD64077F2E704C0F60ADA3DD523", "70C3DB4F0D26368400A10ED05D2BFF5E",
        "234A3463C1264AC6", "1A47CB4933", "D851D5BAE0", "3A59F238A23E39199DC9266626C40F80")]
    public void TryDecrypt_OdtwarzaTekstJawnyZWektorow(
        string keyHex, string nonceHex, string headerHex, string plaintextHex, string cipherHex, string tagHex)
    {
        using var eax = new AesEax(Convert.FromHexString(keyHex));
        byte[] ciphertext = Convert.FromHexString(cipherHex);
        byte[] plaintext = new byte[ciphertext.Length];

        bool ok = eax.TryDecrypt(
            Convert.FromHexString(nonceHex), Convert.FromHexString(headerHex),
            ciphertext, Convert.FromHexString(tagHex), plaintext);

        Assert.True(ok);
        Assert.Equal(plaintextHex.ToUpperInvariant(), Convert.ToHexString(plaintext));
    }

    /// <summary>
    /// Zmiana JEDNEGO bitu w szyfrogramie, znaczniku, nagłówku albo liczbie jednorazowej musi unieważnić
    /// pakiet. Bez tego „uwierzytelnione” szyfrowanie nie uwierzytelnia niczego.
    /// </summary>
    [Theory]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    [InlineData("header")]
    [InlineData("nonce")]
    public void TryDecrypt_ZmienionyBit_OdrzucaPakietIZerujeWynik(string what)
    {
        byte[] key = Convert.FromHexString("01F74AD64077F2E704C0F60ADA3DD523");
        byte[] nonce = Convert.FromHexString("70C3DB4F0D26368400A10ED05D2BFF5E");
        byte[] header = Convert.FromHexString("234A3463C1264AC6");
        byte[] ciphertext = Convert.FromHexString("D851D5BAE0");
        byte[] tag = Convert.FromHexString("3A59F238A23E39199DC9266626C40F80");

        switch (what)
        {
            case "ciphertext": ciphertext[0] ^= 0x01; break;
            case "tag": tag[^1] ^= 0x01; break;
            case "header": header[0] ^= 0x01; break;
            case "nonce": nonce[0] ^= 0x01; break;
        }

        using var eax = new AesEax(key);
        byte[] plaintext = new byte[ciphertext.Length];

        Assert.False(eax.TryDecrypt(nonce, header, ciphertext, tag, plaintext));
        Assert.All(plaintext, b => Assert.Equal(0, b));
    }

    /// <summary>
    /// Wiadomość dłuższa niż jeden blok AES sprawdza przejście licznika CTR między blokami — RFB wysyła
    /// porcje do 8192 bajtów, więc to jest normalny przypadek, a nie skrajny.
    /// </summary>
    [Fact]
    public void EncryptDecrypt_DlugaWiadomosc_WracaBezZmian()
    {
        byte[] key = Convert.FromHexString("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
        byte[] nonce = new byte[16];
        byte[] header = [0x20, 0x00];
        byte[] plaintext = new byte[8192];
        for (int i = 0; i < plaintext.Length; i++)
        {
            plaintext[i] = (byte)(i * 31);
        }

        using var eax = new AesEax(key);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[AesEax.TagSize];
        eax.Encrypt(nonce, header, plaintext, ciphertext, tag);

        Assert.NotEqual(Convert.ToHexString(plaintext), Convert.ToHexString(ciphertext));

        byte[] roundTrip = new byte[plaintext.Length];
        Assert.True(eax.TryDecrypt(nonce, header, ciphertext, tag, roundTrip));
        Assert.Equal(plaintext, roundTrip);
    }
}
