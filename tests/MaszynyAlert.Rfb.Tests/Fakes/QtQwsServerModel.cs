using System.Buffers.Binary;

namespace MaszynyAlert.Rfb.Tests.Fakes;

internal sealed class QtQwsServerModel
{
    public enum ClientState { Unconnected, Protocol, Init, Connected }

    public sealed record PixelFormat(
        int BitsPerPixel, int Depth, bool BigEndian, bool TrueColor,
        int RedBits, int GreenBits, int BlueBits, int RedShift, int GreenShift, int BlueShift)
    {
        public static PixelFormat Read(ReadOnlySpan<byte> buf)
        {
            static int Bits(ReadOnlySpan<byte> two)
            {
                int a = BinaryPrimitives.ReadUInt16BigEndian(two);
                int bits = 0;
                while (a != 0) { a >>= 1; bits++; }
                return bits;
            }

            return new PixelFormat(
                buf[0], buf[1], buf[2] != 0, buf[3] != 0,
                Bits(buf.Slice(4, 2)), Bits(buf.Slice(6, 2)), Bits(buf.Slice(8, 2)),
                buf[10], buf[11], buf[12]);
        }

        public byte[] Write(ReadOnlySpan<byte> stackGarbage)
        {
            static ushort Max(int bits)
            {
                ushort a = 0;
                for (int i = 0; i < bits; i++) a = (ushort)((a << 1) | 1);
                return a;
            }

            byte[] buf = new byte[16];
            buf[0] = (byte)BitsPerPixel;
            buf[1] = (byte)Depth;
            buf[2] = (byte)(BigEndian ? 1 : 0);
            buf[3] = (byte)(TrueColor ? 1 : 0);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), Max(RedBits));
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6), Max(GreenBits));
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8), Max(BlueBits));
            buf[10] = (byte)RedShift;
            buf[11] = (byte)GreenShift;
            buf[12] = (byte)BlueShift;
            stackGarbage[..3].CopyTo(buf.AsSpan(13));
            return buf;
        }
    }

    public static PixelFormat? ServerInitFormat(int depth) => depth switch
    {
        32 => new(32, 32, false, true, 8, 8, 8, 16, 8, 0),
        24 => new(24, 24, false, true, 8, 8, 8, 16, 8, 0),
        18 => new(24, 18, false, true, 6, 6, 6, 12, 6, 0),
        16 => new(16, 16, false, true, 5, 6, 5, 11, 5, 0),
        15 => new(16, 15, false, true, 5, 5, 5, 10, 5, 0),
        12 => new(16, 12, false, true, 4, 4, 4, 8, 4, 0),
        8 or 4 => new(8, 8, false, false, 0, 0, 0, 0, 0, 0),
        _ => null,
    };

    public static byte[] ServerInitBytes(int depth, int width, int height, ReadOnlySpan<byte> stackGarbage)
    {
        byte[] name = "Qt for Embedded Linux VNC Server"u8.ToArray();
        byte[] m = new byte[24 + name.Length];
        BinaryPrimitives.WriteUInt16BigEndian(m, (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), (ushort)height);
        ServerInitFormat(depth)!.Write(stackGarbage).CopyTo(m, 4);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(20), (uint)name.Length);
        name.CopyTo(m, 24);
        return m;
    }

    private readonly List<byte> _rx = [];
    private byte _msgType;
    private bool _handleMsg;
    private int _encodingsPending;

    public QtQwsServerModel(int screenDepth, PixelFormat? leftoverFormat, bool leftoverDirtyCursor, ClientState state = ClientState.Connected)
    {
        ScreenDepth = screenDepth;
        ClientFormat = leftoverFormat;
        DirtyCursor = leftoverDirtyCursor;
        State = state;
    }

    public int ScreenDepth { get; }

    public bool CursorCondition { get; init; } = true;

    public ClientState State { get; private set; }

    public PixelFormat? ClientFormat { get; private set; }

    public bool SameEndian { get; private set; }

    public bool NeedConversion { get; private set; }

    public bool SupportCopyRect { get; private set; }

    public bool SupportHextile { get; private set; }

    public bool SupportCursor { get; private set; }

    public bool SupportDesktopSize { get; private set; }

    public string? Encoder { get; private set; }

    public bool HasClientCursor { get; private set; }

    public bool DirtyCursor { get; private set; }

    public bool WantUpdate { get; private set; }

    public int NumDirty { get; private set; }

    public bool Discarded { get; private set; }

    public bool Crashed { get; private set; }

    public int BytesAvailable => _rx.Count;

    public int ClientBytesPerPixel => (ClientFormat?.BitsPerPixel ?? 0) / 8;

    public List<string> Events { get; } = [];

    /// <summary>Porcja danych z gniazda = jeden sygnał <c>readyRead</c> → <c>readClient()</c>.</summary>
    public void ReadyRead(ReadOnlySpan<byte> chunk)
    {
        _rx.AddRange(chunk.ToArray());
        ReadClient();
    }

    private byte[] Read(int n)
    {
        byte[] b = _rx.GetRange(0, n).ToArray();
        _rx.RemoveRange(0, n);
        return b;
    }

    private void ReadClient()
    {
        switch (State)
        {
            case ClientState.Protocol:
                if (BytesAvailable >= 12)
                {
                    Read(12);
                    Events.Add("Protocol: wersja klienta, auth = None");
                    State = ClientState.Init;
                }

                break;
            case ClientState.Init:
                if (BytesAvailable >= 1)
                {
                    Read(1);
                    Events.Add("Init: ServerInit");
                    State = ClientState.Connected;
                }

                break;
            case ClientState.Connected:
                do
                {
                    if (!_handleMsg)
                    {
                        if (BytesAvailable > 0) _msgType = Read(1)[0]; // client->read(&msgType, 1)
                        _handleMsg = true;
                    }

                    if (_handleMsg)
                    {
                        switch (_msgType)
                        {
                            case 0: SetPixelFormat(); break;
                            case 1: Events.Add("Not supported: FixColourMapEntries"); _handleMsg = false; break;
                            case 2: SetEncodings(); break;
                            case 3: FrameBufferUpdateRequest(); break;
                            case 4: Forbidden("KeyEvent", 7); break;
                            case 5: Forbidden("PointerEvent", 5); break;
                            case 6: Forbidden("ClientCutText", 7); break;
                            default: Events.Add($"Unknown message type: {_msgType}"); _handleMsg = false; break;
                        }
                    }
                }
                while (!_handleMsg && BytesAvailable > 0);
                break;
            default:
                break;
        }
    }

    private void Forbidden(string name, int bytes)
    {
        if (BytesAvailable < bytes) return;
        Read(bytes);
        Events.Add("WEJŚCIE " + name);
        _handleMsg = false;
    }

    private void SetPixelFormat()
    {
        if (BytesAvailable >= 19)
        {
            Read(3); // just padding
            ClientFormat = PixelFormat.Read(Read(16));
            Events.Add($"SetPixelFormat {ClientFormat.BitsPerPixel} bpp");
            if (!ClientFormat.TrueColor)
            {
                Events.Add("Can only handle true color clients → discardClient");
                DiscardClient();
            }

            _handleMsg = false;
            SameEndian = (/* QSysInfo::ByteOrder == BigEndian */ false) == ClientFormat.BigEndian;
            NeedConversion = PixelConversionNeeded();
        }
    }

    private void SetEncodings()
    {
        if (_encodingsPending == 0 && BytesAvailable >= 3)
        {
            Read(1);
            _encodingsPending = BinaryPrimitives.ReadUInt16BigEndian(Read(2));
            if (_encodingsPending == 0) _handleMsg = false;
        }

        Encoder = null; // if (encoder) { delete encoder; encoder = 0; }

        if (_encodingsPending != 0 && BytesAvailable >= _encodingsPending * 4)
        {
            var list = new List<int>();
            for (int i = 0; i < _encodingsPending; ++i)
            {
                int enc = BinaryPrimitives.ReadInt32BigEndian(Read(4));
                list.Add(enc);
                switch (enc)
                {
                    case 0: Encoder ??= "Raw"; break;
                    case 1: SupportCopyRect = true; break;
                    case 5:
                        SupportHextile = true;
                        if (Encoder is not null) break;
                        Encoder = ScreenDepth is 8 or 12 or 15 or 16 or 18 or 24 or 32 ? $"Hextile<{ScreenDepth}>" : null;
                        break;
                    case -239:
                        SupportCursor = true;
                        if (CursorCondition)
                        {
                            HasClientCursor = true;
                            DirtyCursor = true;
                        }

                        break;
                    case -223: SupportDesktopSize = true; break;
                    default: break;
                }
            }

            Events.Add("SetEncodings [" + string.Join(",", list) + "]");
            _handleMsg = false;
            _encodingsPending = 0;
        }

        Encoder ??= "Raw"; // fallback using raw
    }

    private void FrameBufferUpdateRequest()
    {
        if (BytesAvailable >= 9)
        {
            byte[] b = Read(9);
            bool incremental = b[0] != 0;
            int w = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(5));
            int h = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(7));
            Events.Add($"FramebufferUpdateRequest {(incremental ? "przyrostowe" : "pełne")}");
            if (!incremental && w > 0 && h > 0) NumDirty++; // setDirty(r, true)
            WantUpdate = true;
            CheckUpdate();
            _handleMsg = false;
        }
    }

    private void CheckUpdate()
    {
        if (!WantUpdate) return;
        if (DirtyCursor)
        {
            if (!HasClientCursor)
            {
                Events.Add("AWARIA: qvnc_cursor->write() przy qvnc_cursor == 0");
                Crashed = true;
                return;
            }

            Events.Add($"wysłano kursor −239 ({ClientBytesPerPixel} B/piksel)");
            DirtyCursor = false;
            WantUpdate = false;
            return;
        }

        if (NumDirty > 0)
        {
            if (Encoder is not null) Events.Add($"wysłano klatkę {Encoder} ({ClientBytesPerPixel} B/piksel)");
            NumDirty = 0;
            WantUpdate = false;
        }
    }

    private void DiscardClient()
    {
        State = ClientState.Unconnected;
        Encoder = null;
        HasClientCursor = false;
        Discarded = true;
    }

    private bool PixelConversionNeeded()
    {
        if (!SameEndian) return true;
        int screendepth = ScreenDepth;
        if (screendepth != ClientFormat!.BitsPerPixel) return true;
        switch (screendepth)
        {
            case 32:
            case 24:
                return false;
            case 18:
                return ClientFormat.RedBits == 6 && ClientFormat.GreenBits == 6 && ClientFormat.BlueBits == 6;
            case 16:
                return ClientFormat.RedBits == 5 && ClientFormat.GreenBits == 6 && ClientFormat.BlueBits == 5;
            case 15:
                return ClientFormat.RedBits == 5 && ClientFormat.GreenBits == 5 && ClientFormat.BlueBits == 5;
            case 12:
                return ClientFormat.RedBits == 4 && ClientFormat.GreenBits == 4 && ClientFormat.BlueBits == 4;
        }

        return true;
    }

    /// <summary>
    /// Bajty jednego piksela ekranu w pamięci (głębia <see cref="ScreenDepth"/>, host LE): 32 → xRGB (B, G, R, 0xFF),
    /// 16 → RGB565, 24 → qrgb888 (B, G, R), 15/12/18 — tylko składowe (patrz <see cref="ConvertPixel"/>).
    /// </summary>
    public byte[] ScreenBytes(int r8, int g8, int b8) => ScreenDepth switch
    {
        32 => [(byte)b8, (byte)g8, (byte)r8, 0xFF],
        24 => [(byte)b8, (byte)g8, (byte)r8],
        16 => BitConverter.GetBytes((ushort)(((r8 >> 3) << 11) | ((g8 >> 2) << 5) | (b8 >> 3))),
        _ => [],
    };

    public byte[] ConvertPixel(int r8, int g8, int b8)
    {
        PixelFormat pf = ClientFormat!;
        int screendepth = ScreenDepth;
        byte[] src = ScreenBytes(r8, g8, b8);
        if (SameEndian && screendepth == pf.BitsPerPixel)
        {
            switch (screendepth)
            {
                case 32:
                    return src[..4];
                case 16 when pf.RedBits == 5 && pf.GreenBits == 6 && pf.BlueBits == 5:
                    return src[..2];
            }
        }

        int bytesPerPixel = (pf.BitsPerPixel + 7) / 8;
        int r, g, b;
        switch (screendepth)
        {
            case 16:
            {
                int p = BitConverter.ToUInt16(src);
                r = ((p >> 11) & 0x1f) << 3;
                g = ((p >> 5) & 0x3f) << 2;
                b = (p & 0x1f) << 3;
                break;
            }
            case 32:
            {
                int p = BitConverter.ToInt32(src);
                r = (p >> 16) & 0xff;
                g = (p >> 8) & 0xff;
                b = p & 0xff;
                break;
            }
            default:
                (r, g, b) = (r8, g8, b8);
                break;
        }

        r >>= 8 - pf.RedBits;
        g >>= 8 - pf.GreenBits;
        b >>= 8 - pf.BlueBits;
        int pixel = (r << pf.RedShift) | (g << pf.GreenShift) | (b << pf.BlueShift);
        if (SameEndian || pf.BitsPerPixel == 8)
        {
            return BitConverter.GetBytes(pixel)[..bytesPerPixel];
        }

        pixel = pf.BitsPerPixel switch
        {
            16 => (int)((((uint)pixel & 0xff000000) >> 8) | (((uint)pixel & 0x00ff0000) << 8)),
            32 => (int)((((uint)pixel & 0xff000000) >> 24) | (((uint)pixel & 0x00ff0000) >> 8)
                | (((uint)pixel & 0x0000ff00) << 8) | (((uint)pixel & 0x000000ff) << 24)),
            _ => pixel,
        };
        return BitConverter.GetBytes(pixel)[..bytesPerPixel];
    }
}
