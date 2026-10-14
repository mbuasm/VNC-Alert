using MaszynyAlert.Core.Rfb.Crypto;

namespace MaszynyAlert.Rfb.Tests.Crypto;

public sealed class AesCmacTests
{
    private const string Key128 = "2b7e151628aed2a6abf7158809cf4f3c";
    private const string Key256 = "603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4";

    private const string Block1 = "6bc1bee22e409f96e93d7e117393172a";
    private const string Block2 = "ae2d8a571e03ac9c9eb76fac45af8e51";
    private const string Block3 = "30c81c46a35ce411e5fbc1191a0a52ef";
    private const string Block4 = "f69f2445df4f9b17ad2b417be66c3710";

    [Theory]
    // SP 800-38B D.1 — AES-128
    [InlineData(Key128, "", "bb1d6929e95937287fa37d129b756746")]
    [InlineData(Key128, Block1, "070a16b46b4d4144f79bdd9dd04a287c")]
    [InlineData(Key128, Block1 + Block2 + "30c81c46a35ce411", "dfa66747de9ae63030ca32611497c827")]
    [InlineData(Key128, Block1 + Block2 + Block3 + Block4, "51f0bebf7e3b9d92fc49741779363cfe")]
    // SP 800-38B D.3 — AES-256
    [InlineData(Key256, "", "028962f61b7bf89efc6b551f4667d983")]
    [InlineData(Key256, Block1, "28a7023f452e8f82bd4bf28d8c37c35c")]
    [InlineData(Key256, Block1 + Block2 + "30c81c46a35ce411", "aaf3d8f1de5640c232f5b169b9c911e6")]
    [InlineData(Key256, Block1 + Block2 + Block3 + Block4, "e1992190549f6ed5696a2c056c315410")]
    public void ComputeTag_ZgadzaSieZWektoramiNist(string keyHex, string messageHex, string expectedHex)
    {
        using var cmac = new AesCmac(Convert.FromHexString(keyHex));
        byte[] tag = new byte[16];

        cmac.ComputeTag(Convert.FromHexString(messageHex), tag);

        Assert.Equal(expectedHex.ToUpperInvariant(), Convert.ToHexString(tag));
    }

    /// <summary>Znacznik ma dokładnie jeden blok — inne długości to pomyłka wywołującego, nie cichy błąd.</summary>
    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public void ComputeTag_ZlaDlugoscZnacznika_RzucaWyjatek(int length)
    {
        using var cmac = new AesCmac(Convert.FromHexString(Key128));
        byte[] tag = new byte[length];

        Assert.Throws<ArgumentException>(() => cmac.ComputeTag([], tag));
    }
}
