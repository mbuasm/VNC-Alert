using System.Buffers.Binary;
using System.Diagnostics;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Rfb.Tests.Fakes;
using Xunit.Abstractions;

namespace MaszynyAlert.Rfb.Tests;

public sealed class QtCursorFuzzTests
{
    private const int W = CursorTestKit.W;
    private const int H = CursorTestKit.H;
    private static readonly RectInt Full = CursorTestKit.Full;

    private readonly ITestOutputHelper _out;

    public QtCursorFuzzTests(ITestOutputHelper output) => _out = output;

    // ---- losowe kursory, 8/16/32 bpp, hotspot poza ekranem, zapis w całości / po 1 B / losowymi kawałkami --------------

    public static TheoryData<int, int, WriteMode> SimCases()
    {
        var data = new TheoryData<int, int, WriteMode>();
        foreach (int seed in new[] { 1, 42, 20261012 })
        {
            foreach (int bpp in new[] { 16, 32, 8 })
            {
                foreach (WriteMode mode in Enum.GetValues<WriteMode>())
                {
                    data.Add(seed, bpp, mode);
                }
            }
        }

        return data;
    }

    private static Queue<(int W, int H)> SpecialSizes(WriteMode mode) => new(mode switch
    {
        WriteMode.Whole =>
            [(0, 0), (1, 1), (7, 3), (8, 1), (9, 2), (15, 15), (16, 16), (17, 20), (1, 1024), (1024, 1), (1024, 1024), (63, 64),
             (64, 64), (0, 7), (7, 0)],
        WriteMode.OneByte => [(0, 0), (1, 1), (7, 3), (8, 1), (9, 2), (17, 5), (16, 16), (0, 3)],
        _ => [(0, 0), (1, 1), (7, 3), (9, 9), (31, 33), (64, 64), (1024, 2), (2, 1024), (5, 0)],
    });

    private static (int W, int H, int HotX, int HotY) NextCursor(Random rng, WriteMode mode, Queue<(int W, int H)> specials)
    {
        (int w, int h) = specials.Count > 0
            ? specials.Dequeue()
            : mode switch
            {
                WriteMode.OneByte => (rng.Next(0, 21), rng.Next(0, 21)),
                WriteMode.RandomChunks => (rng.Next(0, 65), rng.Next(0, 65)),
                _ => (rng.Next(0, 97), rng.Next(0, 97)),
            };
        int[] hotX = [0, Math.Max(0, w - 1), W - 1, W, W + 50, 65535, rng.Next(0, 65536)];
        int[] hotY = [0, Math.Max(0, h - 1), H - 1, H, H + 50, 65535, rng.Next(0, 65536)];
        return (w, h, hotX[rng.Next(hotX.Length)], hotY[rng.Next(hotY.Length)]);
    }

    [Theory]
    [MemberData(nameof(SimCases))]
    public async Task Fuzz_SymulatorQt48_LosoweKursory_StrumienSpojny_KlatkaZgodna_KursorNigdyNaKlatce(int seed, int bpp, WriteMode mode)
    {
        string ctx = $"ziarno={seed} bpp={bpp} zapis={mode}";
        _out.WriteLine(ctx);
        var rng = new Random(seed);
        RfbPixelFormat format = CursorTestKit.FormatFor(bpp);
        await using var sim = new QtQwsCursorSimServer
        {
            Width = W,
            Height = H,
            ServerInitFormat = format,
            LeftoverClientFormat = null, // format niezainicjowany: bez SetPixelFormat klienta strumień by się rozjechał
            Writes = mode,
            WriteSeed = seed,
            MaxChunk = 1 + rng.Next(200),
        };
        sim.Start();
        await using var client = new RfbClient(CursorTestKit.Options(sim.Port));
        await client.ConnectAsync(CancellationToken.None).WaitAsync(CursorTestKit.Limit);

        RgbaImage first = await client.CaptureAsync(Full, incremental: false, CancellationToken.None).WaitAsync(CursorTestKit.Limit);
        CursorTestKit.AssertSame(sim.LastFrameScreen!, first, ctx + " klatka 0");
        int expectedRequests = 2, expectedCursors = 1, expectedFrames = 1;

        Queue<(int W, int H)> specials = SpecialSizes(mode);
        int steps = mode switch { WriteMode.OneByte => 10, WriteMode.RandomChunks => 14, _ => 17 };
        for (int i = 0; i < steps; i++)
        {
            (int cw, int ch, int hotX, int hotY) = NextCursor(rng, mode, specials);
            await sim.ChangeCursorAsync(cw, ch, hotX, hotY);
            if (rng.Next(2) == 0)
            {
                await sim.ChangeScreenAsync(CursorTestKit.RandomRect(rng), CursorTestKit.Palette[rng.Next(CursorTestKit.Palette.Length)]);
            }

            int op = rng.Next(3);
            string step = $"{ctx} krok={i} op={op} kursor={cw}x{ch}@({hotX},{hotY})";
            RgbaImage before = client.Framebuffer.Snapshot();
            if (op < 2)
            {
                // Nieprzyrostowe (cały ekran albo losowy ROI): „sam kursor” → dokładnie jedno ponowienie tego samego żądania → klatka.
                RectInt roi = op == 0 ? Full : CursorTestKit.RandomRect(rng);
                RgbaImage image = await client.CaptureAsync(roi, incremental: false, CancellationToken.None).WaitAsync(CursorTestKit.Limit);
                expectedRequests += 2;
                expectedCursors++;
                expectedFrames++;
                CursorTestKit.AssertSame(sim.LastFrameScreen!.Crop(roi), image, step + " wycinek");
                CursorTestKit.AssertSame(sim.LastFrameScreen!, client.Framebuffer.Snapshot(), step + " bufor");
                SimRequest[] last = [.. sim.Requests.TakeLast(2)];
                Assert.All(last, r =>
                {
                    Assert.False(r.Incremental, step);
                    Assert.Equal((roi.X, roi.Y, roi.Width, roi.Height), (r.X, r.Y, r.Width, r.Height));
                });
            }
            else
            {
                // Przyrostowe: zmiana ekranu + brudny kursor → najpierw „sam kursor” (zero ponowień, bufor bajt w bajt bez zmian),
                // potem — następne żądanie — kafle zmiany.
                RectInt changed = CursorTestKit.RandomRect(rng);
                RgbaImage screen = await sim.ScreenAsync();
                Rgba existing = screen.GetPixel(changed.X, changed.Y);
                Rgba[] others = [.. CursorTestKit.Palette.Where(c => c != existing)];
                await sim.ChangeScreenAsync(changed, others[rng.Next(others.Length)]);

                RgbaImage? afterCursor = await client.TryCaptureAsync(Full, incremental: true, TimeSpan.FromSeconds(5), CancellationToken.None)
                    .WaitAsync(CursorTestKit.Limit);
                expectedRequests++;
                expectedCursors++;
                Assert.NotNull(afterCursor);
                CursorTestKit.AssertSame(before, client.Framebuffer.Snapshot(), step + " bufor po samym kursorze");
                Assert.Equal(expectedRequests, sim.RequestCount);

                RgbaImage? tiles = await client.TryCaptureAsync(Full, incremental: true, TimeSpan.FromSeconds(5), CancellationToken.None)
                    .WaitAsync(CursorTestKit.Limit);
                expectedRequests++;
                expectedFrames++;
                Assert.NotNull(tiles);
                CursorTestKit.AssertSame(sim.LastFrameScreen!, tiles!, step + " po kaflach");
            }

            CursorTestKit.AssertNoCursor(client.Framebuffer.Image, step);
            Assert.Equal(expectedRequests, sim.RequestCount);
            Assert.Equal(expectedCursors, sim.CursorResponses);
            Assert.Equal(expectedFrames, sim.FrameResponses);
        }

        await Task.Delay(200);
        _out.WriteLine(
            $"{ctx}: kroki={steps}, żądania={sim.RequestCount}, „sam kursor”={sim.CursorResponses} ({sim.CursorBytes} B), " +
            $"klatki={sim.FrameResponses}, zapisy do gniazda={sim.ChunkWrites}, opóźnienia={sim.ChunkDelays}, nakładki={sim.Overlaps}");
        Assert.Empty(sim.Problems);
        Assert.Equal(expectedRequests, sim.RequestCount);
        Assert.Equal(0, sim.Overlaps);
        Assert.True(client.IsConnected, ctx);
        byte[] types = [.. sim.ClientMessageTypes];
        Assert.Equal(new byte[] { 0, 2 }, types.Take(2).ToArray());
        Assert.All(types.Skip(2), t => Assert.Equal(3, t));
    }

    // ---- limit 1024×1024 -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1025, 1)]
    [InlineData(1, 1025)]
    [InlineData(1025, 1025)]
    [InlineData(65535, 65535)]
    [InlineData(0, 1025)]
    [InlineData(1024, 1025)]
    public async Task KursorPonadLimit1024_BladProtokolu_BezCzekaniaNaDane(int w, int h)
    {
        await using var server = new ScriptedRfbServer
        {
            Width = W, Height = H, Rfb33 = true,
            PixelFormat = RfbPixelFormat.Rgb565LittleEndian,
            DesktopName = QtEmbeddedVncServer.DesktopName,
            OnRequest = async (s, number, ct) =>
            {
                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(1), ct);
                await s.WriteAsync(ScriptedRfbServer.RectangleHeader(0, 0, w, h, -239), ct); // bez danych pikseli
            },
        };
        server.Start();
        await using var client = new RfbClient(CursorTestKit.Options(server.Port, TimeSpan.FromSeconds(5)));
        await client.ConnectAsync(CancellationToken.None);

        var watch = Stopwatch.StartNew();
        Exception? ex = await Record.ExceptionAsync(() => client.CaptureAsync(Full, incremental: false, CancellationToken.None));
        watch.Stop();

        RfbProtocolException protocol = Assert.IsType<RfbProtocolException>(ex);
        Assert.Contains($"kursor ({w}×{h})", protocol.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"błąd po {watch.ElapsedMilliseconds} ms (czekał na dane?)");
        Assert.False(client.IsConnected);
    }

    [Theory]
    [InlineData(1024, 1024, 32)]
    [InlineData(1024, 1024, 16)]
    [InlineData(1024, 1, 16)]
    [InlineData(1, 1024, 8)]
    public async Task KursorNaGranicyLimitu_PominietyDokladnie_KolejnaKlatkaPoprawna(int w, int h, int bpp)
    {
        RfbPixelFormat format = CursorTestKit.FormatFor(bpp);
        var red = new Rgba(255, 0, 0);
        await using var server = new ScriptedRfbServer
        {
            Width = W, Height = H, Rfb33 = true, PixelFormat = format,
            DesktopName = QtEmbeddedVncServer.DesktopName,
            OnRequest = async (s, number, ct) =>
            {
                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(1), ct);
                await s.WriteAsync(number == 0
                    ? CursorTestKit.CursorRect(w, h, 3, 3, bpp / 8, format)
                    : CursorTestKit.RawRect(Full, red, format), ct);
            },
        };
        server.Start();
        await using var client = new RfbClient(CursorTestKit.Options(server.Port));
        await client.ConnectAsync(CancellationToken.None);

        RgbaImage image = await client.CaptureAsync(Full, incremental: false, CancellationToken.None).WaitAsync(CursorTestKit.Limit);

        var expected = new RgbaImage(W, H);
        expected.Fill(red);
        CursorTestKit.AssertSame(expected, image, $"{w}x{h} {bpp} bpp");
        await Task.Delay(100);
        Assert.Equal(2, server.ClientMessageTypes.Count(t => t == 3));
    }

    // ---- kursor w jednej aktualizacji z Raw/CopyRect/Hextile (początek/środek/koniec/kilka), Bell i ServerCutText ------

    private sealed record ScriptedUpdate(byte[] Bytes, RgbaImage Expected, int RealRects, int Cursors, string Description);

    public static TheoryData<int, int, WriteMode, bool> MixedCases()
    {
        var data = new TheoryData<int, int, WriteMode, bool>();
        foreach (int seed in new[] { 3, 77, 156239 })
        {
            foreach (int bpp in new[] { 16, 32 })
            {
                foreach (WriteMode mode in Enum.GetValues<WriteMode>())
                {
                    data.Add(seed, bpp, mode, true);
                    data.Add(seed, bpp, mode, false);
                }
            }
        }

        return data;
    }

    private static List<ScriptedUpdate> BuildMixedScript(int seed, RfbPixelFormat format, int count, bool incremental)
    {
        var rng = new Random(seed * 31 + (incremental ? 1 : 0));
        var pixels = new PixelEncoder(format, null);
        int bpp = pixels.BytesPerPixel;
        var model = new RgbaImage(W, H);
        var result = new List<ScriptedUpdate>();
        for (int k = 0; k < count; k++)
        {
            bool needFull = k == 0 || !incremental; // nieprzyrostowe: prostokąty muszą pokryć cały ekran
            bool cursorOnly = !needFull && rng.Next(5) == 0;
            int extra = cursorOnly ? 0 : rng.Next(0, 4);
            int realCount = extra + (needFull ? 1 : 0);
            int fullIndex = needFull ? rng.Next(realCount) : -1;
            var items = new List<(byte[] Bytes, string Name)>();
            for (int i = 0; i < realCount; i++)
            {
                int kind = rng.Next(3);
                RectInt r = i == fullIndex ? Full : CursorTestKit.RandomRect(rng);
                if (i == fullIndex && kind == 1)
                {
                    kind = 0; // CopyRect nie pokrywa ekranu nową treścią — pełny prostokąt to Raw albo Hextile
                }

                switch (kind)
                {
                    case 0:
                        {
                            var ms = new MemoryStream();
                            ms.Write(ScriptedRfbServer.RectangleHeader(r.X, r.Y, r.Width, r.Height, RfbEncodings.Raw));
                            for (int y = r.Y; y < r.Bottom; y++)
                            {
                                for (int x = r.X; x < r.Right; x++)
                                {
                                    Rgba c = CursorTestKit.Palette[rng.Next(CursorTestKit.Palette.Length)];
                                    pixels.WritePixel(c, ms);
                                    model.SetPixel(x, y, c);
                                }
                            }

                            items.Add((ms.ToArray(), $"Raw{r}"));
                            break;
                        }

                    case 1:
                        {
                            int sx = rng.Next(W - r.Width + 1);
                            int sy = rng.Next(H - r.Height + 1);
                            byte[] bytes = new byte[16];
                            ScriptedRfbServer.RectangleHeader(r.X, r.Y, r.Width, r.Height, RfbEncodings.CopyRect).CopyTo(bytes, 0);
                            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), (ushort)sx);
                            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(14), (ushort)sy);
                            RgbaImage copy = model.Crop(new RectInt(sx, sy, r.Width, r.Height));
                            model.Blit(copy, r.X, r.Y);
                            items.Add((bytes, $"CopyRect{r}<-({sx},{sy})"));
                            break;
                        }

                    default:
                        {
                            RgbaImage next = model.Clone();
                            Rgba background = CursorTestKit.Palette[rng.Next(CursorTestKit.Palette.Length)];
                            for (int y = r.Y; y < r.Bottom; y++)
                            {
                                for (int x = r.X; x < r.Right; x++)
                                {
                                    next.SetPixel(x, y, background);
                                }
                            }

                            for (int s = rng.Next(0, 5); s > 0; s--)
                            {
                                RectInt sub = new RectInt(r.X + rng.Next(r.Width), r.Y + rng.Next(r.Height), 1 + rng.Next(6), 1 + rng.Next(6))
                                    .Intersect(r);
                                Rgba c = CursorTestKit.Palette[rng.Next(CursorTestKit.Palette.Length)];
                                for (int y = sub.Y; y < sub.Bottom; y++)
                                {
                                    for (int x = sub.X; x < sub.Right; x++)
                                    {
                                        next.SetPixel(x, y, c);
                                    }
                                }
                            }

                            if (rng.Next(4) == 0)
                            {
                                for (int n = 0; n < 20; n++)
                                {
                                    next.SetPixel(r.X + rng.Next(r.Width), r.Y + rng.Next(r.Height),
                                        CursorTestKit.Palette[rng.Next(CursorTestKit.Palette.Length)]);
                                }
                            }

                            var ms = new MemoryStream();
                            ms.Write(ScriptedRfbServer.RectangleHeader(r.X, r.Y, r.Width, r.Height, RfbEncodings.Hextile));
                            new HextileEncoder(pixels, stickyBackground: rng.Next(2) == 0, colouredSubrects: rng.Next(2) == 0)
                                .Encode(next, r, ms, _ => { });
                            model = next;
                            items.Add((ms.ToArray(), $"Hextile{r}"));
                            break;
                        }
                }
            }

            int cursors = cursorOnly ? 1 + rng.Next(4) : rng.Next(0, 4);
            for (int c = 0; c < cursors; c++)
            {
                int cw = rng.Next(0, 33);
                int ch = rng.Next(0, 33);
                int hx = rng.Next(3) == 0 ? 65535 : rng.Next(0, W + 20);
                int hy = rng.Next(3) == 0 ? 65535 : rng.Next(0, H + 20);
                int position = rng.Next(items.Count + 1); // początek, środek, koniec; kilka kursorów może stać obok siebie
                items.Insert(position, (CursorTestKit.CursorRect(cw, ch, hx, hy, bpp, format), $"Kursor{cw}x{ch}@({hx},{hy})"));
            }

            var message = new List<byte>();
            int prefix = rng.Next(4);
            if (prefix is 1 or 3)
            {
                message.Add(2); // Bell
            }

            if (prefix is 2 or 3)
            {
                byte[] text = new byte[rng.Next(0, 300)];
                rng.NextBytes(text);
                message.AddRange([3, 0, 0, 0]);
                byte[] length = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(length, (uint)text.Length);
                message.AddRange(length);
                message.AddRange(text);
            }

            message.AddRange(ScriptedRfbServer.UpdateHeader(items.Count));
            foreach ((byte[] bytes, _) in items)
            {
                message.AddRange(bytes);
            }

            string description = $"prefiks={prefix} [{string.Join(", ", items.Select(i => i.Name))}]";
            result.Add(new ScriptedUpdate([.. message], model.Clone(), realCount, cursors, description));
        }

        return result;
    }

    [Theory]
    [MemberData(nameof(MixedCases))]
    public async Task Fuzz_KursoryWsrodRawCopyRectHextile_StrumienSpojny_BuforZgodnyZModelem(int seed, int bpp, WriteMode mode, bool incremental)
    {
        string ctx = $"ziarno={seed} bpp={bpp} zapis={mode} przyrostowe={incremental}";
        _out.WriteLine(ctx);
        RfbPixelFormat format = CursorTestKit.FormatFor(bpp);
        List<ScriptedUpdate> script = BuildMixedScript(seed, format, count: 12, incremental);
        var writer = new ChunkedWriter(mode, seed, maxChunk: 1 + (seed % 61));
        await using var server = new ScriptedRfbServer
        {
            Width = W, Height = H, Rfb33 = true, PixelFormat = format,
            DesktopName = QtEmbeddedVncServer.DesktopName,
            OnRequest = async (s, number, ct) =>
            {
                if (number < script.Count)
                {
                    await writer.WriteAsync(s, script[number].Bytes, ct);
                }
            },
        };
        server.Start();
        await using var client = new RfbClient(CursorTestKit.Options(server.Port));
        await client.ConnectAsync(CancellationToken.None);

        for (int k = 0; k < script.Count; k++)
        {
            string step = $"{ctx} aktualizacja {k}: {script[k].Description}";
            FramebufferUpdate update = await client.UpdateAsync(Full, incremental, CancellationToken.None).WaitAsync(CursorTestKit.Limit);
            Assert.True(script[k].RealRects == update.Rectangles.Count, $"{step}: prostokąty {update.Rectangles.Count}");
            Assert.True(script[k].Cursors == update.EncodingsUsed.Count(e => e == -239), $"{step}: kursory");
            CursorTestKit.AssertSame(script[k].Expected, client.Framebuffer.Snapshot(), step);
            CursorTestKit.AssertNoCursor(client.Framebuffer.Image, step);
        }

        await Task.Delay(200);
        int requests = server.ClientMessageTypes.Count(t => t == 3);
        _out.WriteLine($"{ctx}: aktualizacje={script.Count}, bajty={script.Sum(u => u.Bytes.Length)}, kursory={script.Sum(u => u.Cursors)}, " +
                       $"zapisy={writer.Writes}, opóźnienia={writer.Delays}, żądania={requests}");
        Assert.Equal(script.Count, requests); // ani jednego ponowienia: każda aktualizacja ma prostokąty albo żądanie było przyrostowe
        Assert.True(client.IsConnected);
    }

    // ---- kursor nie liczy się do pokrycia klatki, nie zmienia bufora, hotspot przy krawędzi i poza ekranem ------------------

    [Fact]
    public async Task KursorRozmiaruEkranu_NieLiczySieDoPokrycia_KlientPonawia_KlatkaBezKursora()
    {
        RfbPixelFormat format = RfbPixelFormat.Rgb565LittleEndian;
        var red = new Rgba(255, 0, 0);
        await using var server = new ScriptedRfbServer
        {
            Width = W, Height = H, Rfb33 = true, PixelFormat = format,
            DesktopName = QtEmbeddedVncServer.DesktopName,
            OnRequest = async (s, number, ct) =>
            {
                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(1), ct);
                await s.WriteAsync(number == 0
                    ? CursorTestKit.CursorRect(W, H, 0, 0, 2, format) // kursor dokładnie na cały ekran
                    : CursorTestKit.RawRect(Full, red, format), ct);
            },
        };
        server.Start();
        await using var client = new RfbClient(CursorTestKit.Options(server.Port));
        await client.ConnectAsync(CancellationToken.None);

        RgbaImage image = await client.CaptureAsync(Full, incremental: false, CancellationToken.None).WaitAsync(CursorTestKit.Limit);

        var expected = new RgbaImage(W, H);
        expected.Fill(red);
        CursorTestKit.AssertSame(expected, image, "kursor W×H");
        await Task.Delay(100);
        Assert.Equal(2, server.ClientMessageTypes.Count(t => t == 3));
    }

    [Fact]
    public async Task KursorICzesciowyRaw_KursorNieUzupelniaPokrycia_KlientCzekaNaBrakujacaCzesc()
    {
        // Gdyby kursor (0,0,W,H) liczył się do pokrycia, klient zakończyłby po pierwszej aktualizacji z niedekodowaną dolną połową.
        RfbPixelFormat format = RfbPixelFormat.Rgb565LittleEndian;
        var red = new Rgba(255, 0, 0);
        var blue = new Rgba(0, 0, 255);
        await using var server = new ScriptedRfbServer
        {
            Width = W, Height = H, Rfb33 = true, PixelFormat = format,
            DesktopName = QtEmbeddedVncServer.DesktopName,
            OnRequest = async (s, number, ct) =>
            {
                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(2), ct);
                await s.WriteAsync(CursorTestKit.CursorRect(W, H, 0, 0, 2, format), ct);
                await s.WriteAsync(CursorTestKit.RawRect(new RectInt(0, 0, W, H / 2), red, format), ct);
                await Task.Delay(300, ct);
                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(1), ct);
                await s.WriteAsync(CursorTestKit.RawRect(new RectInt(0, H / 2, W, H / 2), blue, format), ct);
            },
        };
        server.Start();
        await using var client = new RfbClient(CursorTestKit.Options(server.Port));
        await client.ConnectAsync(CancellationToken.None);

        RgbaImage image = await client.CaptureAsync(Full, incremental: false, CancellationToken.None).WaitAsync(CursorTestKit.Limit);

        Assert.Equal(red, image.GetPixel(5, 2));
        Assert.Equal(blue, image.GetPixel(5, H - 1));
        CursorTestKit.AssertNoCursor(image, "kursor + połowa Raw");
        await Task.Delay(100);
        Assert.Equal(1, server.ClientMessageTypes.Count(t => t == 3)); // prostokąty były → bez ponowienia
    }

    [Theory]
    [InlineData(W - 1, H - 1)]
    [InlineData(W, H)]
    [InlineData(W + 1, 0)]
    [InlineData(0, 65535)]
    [InlineData(65535, 0)]
    [InlineData(65535, 65535)]
    public async Task KursorZHotspotemNaKrawedziIPozaEkranem_NieOdrzucony_SamIRazemZKlatka(int hotX, int hotY)
    {
        RfbPixelFormat format = RfbPixelFormat.Rgb565LittleEndian;
        var red = new Rgba(255, 0, 0);
        await using var server = new ScriptedRfbServer
        {
            Width = W, Height = H, Rfb33 = true, PixelFormat = format,
            DesktopName = QtEmbeddedVncServer.DesktopName,
            OnRequest = async (s, number, ct) =>
            {
                if (number == 0)
                {
                    await s.WriteAsync(ScriptedRfbServer.UpdateHeader(1), ct);
                    await s.WriteAsync(CursorTestKit.CursorRect(16, 16, hotX, hotY, 2, format), ct);
                    return;
                }

                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(2), ct);
                await s.WriteAsync(CursorTestKit.RawRect(Full, red, format), ct);
                await s.WriteAsync(CursorTestKit.CursorRect(16, 16, hotX, hotY, 2, format), ct);
            },
        };
        server.Start();
        await using var client = new RfbClient(CursorTestKit.Options(server.Port));
        await client.ConnectAsync(CancellationToken.None);

        RgbaImage image = await client.CaptureAsync(Full, incremental: false, CancellationToken.None).WaitAsync(CursorTestKit.Limit);
        FramebufferUpdate second = await client.UpdateAsync(Full, incremental: false, CancellationToken.None).WaitAsync(CursorTestKit.Limit);

        var expected = new RgbaImage(W, H);
        expected.Fill(red);
        CursorTestKit.AssertSame(expected, image, $"hotspot ({hotX},{hotY})");
        Assert.Single(second.Rectangles);
        Assert.Equal(new[] { RfbEncodings.Raw, -239 }, second.EncodingsUsed);
        await Task.Delay(100);
        Assert.Equal(3, server.ClientMessageTypes.Count(t => t == 3));
    }


    [Theory]
    [InlineData("Qt for Embedded Linux VNC")]
    [InlineData("Qt for Embedded Linux VNC Serve")]
    [InlineData("QtforEmbeddedLinuxVNCServer")]
    [InlineData("Qt for Embedded Linux VNC​Server")]
    [InlineData("Qt VNC Server")]
    [InlineData("68EDA4721759")]
    [InlineData("WinCE")]
    [InlineData("OPENCNC2")]
    [InlineData("")]
    public async Task SerwerNieQt_NazwaPodobna_BezKursoraIFormatu_NieproszonyKursorToBladProtokolu(string name)
    {
        await using var server = new ScriptedRfbServer
        {
            Width = W, Height = H, DesktopName = name,
            OnRequest = async (s, number, ct) =>
            {
                await s.WriteAsync(ScriptedRfbServer.UpdateHeader(2), ct);
                await s.WriteAsync(CursorTestKit.RawRect(Full, new Rgba(255, 0, 0), RfbPixelFormat.Bgra32LittleEndian), ct);
                await s.WriteAsync(CursorTestKit.CursorRect(4, 4, 0, 0, 4, RfbPixelFormat.Bgra32LittleEndian), ct);
            },
        };
        server.Start();
        await using var client = new RfbClient(CursorTestKit.Options(server.Port, TimeSpan.FromSeconds(5)));
        await client.ConnectAsync(CancellationToken.None);

        Exception? ex = await Record.ExceptionAsync(() => client.CaptureAsync(Full, incremental: false, CancellationToken.None));

        RfbProtocolException protocol = Assert.IsType<RfbProtocolException>(ex);
        Assert.Contains("kodowanie prostokąta aktualizacji: -239 ", protocol.Message);
        Assert.Equal(new[] { RfbEncodings.Hextile, RfbEncodings.CopyRect, RfbEncodings.Raw }, server.ReceivedEncodings);
        Assert.DoesNotContain((byte)0, server.ClientMessageTypes);
    }
}
