using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.Rfb.Tests.Fakes;

/// <summary>Sposób kodowania prostokątów w odpowiedziach <c>FramebufferUpdate</c> fałszywego serwera.</summary>
public enum FakeEncodingMode
{
    /// <summary>Jeden prostokąt Raw pokrywający żądany obszar.</summary>
    Raw,

    /// <summary>Jeden prostokąt Hextile pokrywający żądany obszar.</summary>
    Hextile,

    /// <summary>Dwa prostokąty: lewa połowa ekranu Raw + prawa połowa jako CopyRect ze źródła (0,0).</summary>
    CopyRectHalves,
}

/// <summary>
/// Konfiguracja <see cref="FakeRfbServer"/>. Każdy test tworzy własny serwer z własnymi opcjami,
/// dzięki czemu testy są od siebie niezależne i mogą działać równolegle.
/// </summary>
public sealed class FakeRfbServerOptions
{
    /// <summary>Wersja protokołu ogłaszana w nagłówku: "3.3", "3.7" albo "3.8".</summary>
    public string ProtocolVersion { get; set; } = "3.8";

    /// <summary>Oferowane typy zabezpieczeń. W 3.3 używany jest wyłącznie pierwszy element.</summary>
    public IReadOnlyList<byte> SecurityTypes { get; set; } = [1];

    /// <summary>Hasło VNC oczekiwane przy VncAuth (weryfikowane referencyjnym DES-em, nie kodem z Core).</summary>
    public string? Password { get; set; }

    /// <summary>Gdy true, VncAuth zawsze kończy się <c>SecurityResult = 1</c> i powodem „Authentication failed”.</summary>
    public bool RejectVncAuth { get; set; }

    /// <summary>Format piksela ogłaszany w <c>ServerInit</c> i używany do kodowania danych.</summary>
    public RfbPixelFormat PixelFormat { get; set; } = RfbPixelFormat.Bgra32LittleEndian;

    /// <summary>Paleta dla trybu 8 bpp (<c>TrueColour = false</c>); indeks w tej liście = wartość piksela.</summary>
    public IReadOnlyList<Rgba>? Palette { get; set; }

    /// <summary>Gdy true, tuż po <c>ServerInit</c> wysyłany jest <c>SetColourMapEntries</c> z <see cref="Palette"/>.</summary>
    public bool SendColourMap { get; set; }

    /// <summary>Obraz źródłowy „ekranu maszyny”. Można podmienić w trakcie testu (patrz <see cref="FakeRfbServer.Screen"/>).</summary>
    public RgbaImage Screen { get; set; } = CreateDefaultScreen();

    /// <summary>Szerokość ogłaszana w <c>ServerInit</c>; domyślnie szerokość <see cref="Screen"/>.</summary>
    public int? Width { get; set; }

    /// <summary>Wysokość ogłaszana w <c>ServerInit</c>; domyślnie wysokość <see cref="Screen"/>.</summary>
    public int? Height { get; set; }

    /// <summary>Nazwa pulpitu w <c>ServerInit</c>.</summary>
    public string DesktopName { get; set; } = "FakeRFB";

    /// <summary>Kodowanie używane w odpowiedziach na <c>FramebufferUpdateRequest</c>.</summary>
    public FakeEncodingMode Encoding { get; set; } = FakeEncodingMode.Raw;

    /// <summary>
    /// Tryb testowy Hextile: kolejne kafle o tym samym tle NIE ustawiają bitu BackgroundSpecified —
    /// poprawny obraz uzyska tylko klient pamiętający „sticky” tło między kaflami.
    /// </summary>
    public bool HextileUseStickyBackground { get; set; }

    /// <summary>Tryb testowy Hextile: kafle o 3–4 kolorach kodowane jako AnySubrects + SubrectsColoured.</summary>
    public bool HextileUseColouredSubrects { get; set; }

    /// <summary>Gdy true, podłączenie nowego klienta zamyka połączenie poprzedniego („wyrzuć poprzedniego”).</summary>
    public bool KickPreviousClientOnNewConnection { get; set; }

    /// <summary>Maksymalna liczba jednoczesnych połączeń; nadmiarowe są natychmiast zamykane.</summary>
    public int MaxClients { get; set; } = 8;

    /// <summary>Opcjonalne opóźnienie przed pierwszym bajtem nagłówka RFB — test anulowania podczas handshake.</summary>
    public TimeSpan DelayBeforeVersion { get; set; }

    /// <summary>
    /// Gdy true, serwer nigdy nie wysyła nagłówka RFB i tylko czeka, aż klient się rozłączy —
    /// test limitu czasu podczas handshake bez wyścigu z zegarem (klient RFB milczy do nagłówka serwera).
    /// </summary>
    public bool SilentUntilClientCloses { get; set; }

    /// <summary>Opcjonalne opóźnienie odpowiedzi na każde żądanie obrazu — test anulowania podczas odczytu.</summary>
    public TimeSpan DelayBeforeUpdate { get; set; }

    /// <summary>Gdy true, przed każdym <c>FramebufferUpdate</c> wysyłany jest <c>Bell</c> (typ 2).</summary>
    public bool SendBellBeforeUpdate { get; set; }

    /// <summary>Gdy true, przed każdym <c>FramebufferUpdate</c> wysyłany jest <c>ServerCutText</c> (typ 3).</summary>
    public bool SendServerCutTextBeforeUpdate { get; set; }

    /// <summary>Treść <c>ServerCutText</c> wstrzykiwanego przed aktualizacją.</summary>
    public string ServerCutText { get; set; } = "zawartosc schowka serwera";

    /// <summary>Gdy true, żądanie przyrostowe bez zmiany ekranu dostaje <c>FramebufferUpdate</c> z 0 prostokątami.</summary>
    public bool IncrementalSendsNothingWhenUnchanged { get; set; }

    public bool IncrementalNoResponseWhenUnchanged { get; set; }

    /// <summary>
    /// Uzupełnienie <see cref="IncrementalNoResponseWhenUnchanged"/>: żądanie, na które serwer nie
    /// odpowiedział, ZOSTAJE U SERWERA (RFC 6143 §7.5.3 — serwer odpowiada, gdy tylko pojawi się zmiana),
    /// więc podmiana <see cref="FakeRfbServer.Screen"/> sama z siebie wysyła zaległą aktualizację —
    /// bez kolejnego <c>FramebufferUpdateRequest</c> od klienta. Tak zachowuje się prawdziwy serwer i
    /// na tym opiera się <c>RfbClient.TryCaptureAsync</c> (nie wysyła drugiego żądania, gdy poprzednie
    /// wciąż czeka).
    /// </summary>
    public bool DeliverPendingIncrementalOnScreenChange { get; set; }

    /// <summary>Tryb awarii: serwer zamyka gniazdo zaraz po nagłówku <c>FramebufferUpdate</c>, przed danymi pikseli.</summary>
    public bool CloseAfterUpdateHeader { get; set; }

    /// <summary>Stałe 16-bajtowe wyzwanie VncAuth (dla determinizmu testów); null → wyzwanie domyślne.</summary>
    public byte[]? FixedChallenge { get; set; }

    /// <summary>Ustawienia strony serwerowej uwierzytelnienia RSA-AES (typy 5, 6, 129, 130).</summary>
    internal FakeRsaAesOptions RsaAes { get; set; } = new();

    /// <summary>
    /// Dosłowny 12-bajtowy nagłówek wersji wysyłany zamiast wyliczonego z <see cref="ProtocolVersion"/> —
    /// do testów nietypowych serwerów (np. "RFB 003.889\n" UltraVNC) i nagłówków uszkodzonych.
    /// </summary>
    public string? RawVersionHeader { get; set; }

    /// <summary>Tryb testowy: wysyła prostokąt z tym numerem kodowania i bez danych (np. ZRLE = 16).</summary>
    public int? OverrideRectEncoding { get; set; }

    /// <summary>Tryb testowy: przed aktualizacją wysyła wiadomość serwera o tym (nieznanym) typie.</summary>
    public byte? SendUnknownServerMessageType { get; set; }

    /// <summary>
    /// Tryb testowy: w pierwszej aktualizacji dla każdego połączenia wysyła pseudo-kodowanie
    /// DesktopSize (-223) z rozmiarem aktualnego <see cref="Screen"/>, zanim wyśle właściwy prostokąt.
    /// </summary>
    public bool AnnounceDesktopSize { get; set; }

    /// <summary>
    /// Rozmiar ogłaszany w pseudo-kodowaniu DesktopSize zamiast rozmiaru <see cref="Screen"/> — pozwala
    /// przetestować reakcję klienta na nierealistyczne wymiary bez alokowania takiego ekranu.
    /// </summary>
    public int? AnnouncedDesktopWidth { get; set; }

    /// <summary>Wysokość ogłaszana w DesktopSize; patrz <see cref="AnnouncedDesktopWidth"/>.</summary>
    public int? AnnouncedDesktopHeight { get; set; }

    private static RgbaImage CreateDefaultScreen()
    {
        var img = new RgbaImage(16, 16);
        img.Fill(new Rgba(32, 32, 32));
        return img;
    }
}
