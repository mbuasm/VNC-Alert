using System.Text.Json;
using MaszynyAlert.Core.Alerts;
using MaszynyAlert.Core.Cluster;
using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Tests.Alerts;

public sealed class AlertKeyTests
{
    [Fact]
    public void ForRule_BudujeKluczZPrefiksemRule()
    {
        AlertKey key = AlertKey.ForRule("aidite1", "tray-grab");

        Assert.Equal("aidite1", key.MachineId);
        Assert.Equal("rule:tray-grab", key.Discriminator);
    }

    [Fact]
    public void ForText_DlaTegoSamegoTekstu_DajeTenSamKlucz()
    {
        AlertKey a = AlertKey.ForText("aidite1", "FAILED TO GRAB A TRAY");
        AlertKey b = AlertKey.ForText("aidite1", "FAILED TO GRAB A TRAY");
        AlertKey c = AlertKey.ForText("aidite1", "INNY TEKST");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith("text:", a.Discriminator, StringComparison.Ordinal);
        Assert.Equal(17, a.Discriminator.Length);
    }

    [Fact]
    public void ForText_DlaPustegoTekstu_ZwracaTextEmpty()
    {
        Assert.Equal("text:empty", AlertKey.ForText("aidite1", "").Discriminator);
        Assert.Equal("text:empty", AlertKey.ForText("aidite1", null!).Discriminator);
    }

    [Fact]
    public void ForMessage_UzywaDyskryminatoraZDopasowanejWiadomosci()
    {
        var matched = new MappedMessage("spindle", "Błąd wrzeciona", "restart", "Spindle error", "SPINDLE ERROR", true);
        var unmatched = new MappedMessage(null, "Błąd: cos", "inny", "cos", "COS", false);

        Assert.Equal("rule:spindle", AlertKey.ForMessage("amm580", matched).Discriminator);
        Assert.Equal(AlertKey.ForText("amm580", "COS"), AlertKey.ForMessage("amm580", unmatched));
    }

    [Fact]
    public void Offline_BudujeKluczZDyskryminatoremOffline()
    {
        Assert.Equal(new AlertKey("cameo1", "offline"), AlertKey.Offline("cameo1"));
    }

    [Fact]
    public void ToStringIParse_TworzaParęOdwracalną()
    {
        AlertKey key = AlertKey.ForRule("amm520-1", "tool-broken");

        string text = key.ToString();

        Assert.Equal("amm520-1|rule:tool-broken", text);
        Assert.Equal(key, AlertKey.Parse(text));
    }

    [Fact]
    public void Parse_GdyBrakSeparatora_RzucaFormatException()
    {
        Assert.Throws<FormatException>(() => AlertKey.Parse("bez-separatora"));
    }

    [Fact]
    public void Parse_GdyDyskryminatorZawieraSeparator_DzieliTylkoNaPierwszym()
    {
        AlertKey key = AlertKey.Parse("m1|text:a|b");

        Assert.Equal("m1", key.MachineId);
        Assert.Equal("text:a|b", key.Discriminator);
    }

    [Fact]
    public void SerializacjaJson_ZapisujeCamelCaseIDajeSieOdczytac()
    {
        AlertKey key = AlertKey.ForRule("aidite3", "door");

        string json = JsonSerializer.Serialize(key, MessageCodec.JsonOptions);
        AlertKey roundTripped = JsonSerializer.Deserialize<AlertKey>(json, MessageCodec.JsonOptions);

        Assert.Contains("\"machineId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"discriminator\"", json, StringComparison.Ordinal);
        Assert.Equal(key, roundTripped);
    }
}
