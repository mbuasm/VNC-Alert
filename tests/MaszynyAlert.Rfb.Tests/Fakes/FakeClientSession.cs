using System.Collections.Concurrent;
using System.Net.Sockets;

namespace MaszynyAlert.Rfb.Tests.Fakes;

/// <summary>Jedno żądanie <c>FramebufferUpdateRequest</c> zarejestrowane przez fałszywy serwer.</summary>
public readonly record struct FakeUpdateRequest(int X, int Y, int Width, int Height, bool Incremental);

/// <summary>
/// Jedno połączenie klienta obsłużone przez <see cref="FakeRfbServer"/> wraz z pełnym zapisem tego,
/// co klient wysłał. Głównym „strażnikiem bezpieczeństwa” jest <see cref="ReceivedMessageTypes"/> —
/// po poprawnej sesji może zawierać wyłącznie 2 (SetEncodings) i 3 (FramebufferUpdateRequest).
/// </summary>
public sealed class FakeClientSession
{
    private readonly ConcurrentQueue<byte> _messageTypes = new();
    private readonly ConcurrentQueue<FakeUpdateRequest> _updateRequests = new();
    private readonly ConcurrentQueue<byte> _hextileSubencodings = new();
    private readonly TaskCompletionSource<int[]> _encodingsTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _closed;

    internal FakeClientSession(int index, TcpClient tcp)
    {
        Index = index;
        Tcp = tcp;
    }

    internal TcpClient Tcp { get; }

    /// <summary>Numer połączenia (od 0) w kolejności akceptacji przez serwer.</summary>
    public int Index { get; }

    /// <summary>Wersja protokołu zadeklarowana przez klienta, np. "RFB 003.008\n".</summary>
    public string? ClientProtocolVersion { get; internal set; }

    /// <summary>Typ zabezpieczeń wybrany przez klienta (3.7/3.8) lub narzucony przez serwer (3.3).</summary>
    public byte? ChosenSecurityType { get; internal set; }

    /// <summary>Flaga <c>shared</c> z <c>ClientInit</c>; null, gdy klient nie doszedł do tego etapu.</summary>
    public bool? Shared { get; internal set; }

    /// <summary>Lista kodowań z <c>SetEncodings</c> (pusta, gdy jeszcze nie odebrano).</summary>
    public IReadOnlyList<int> Encodings =>
        _encodingsTcs.Task.IsCompletedSuccessfully ? _encodingsTcs.Task.Result : Array.Empty<int>();

    /// <summary>Typy wszystkich wiadomości klienta odebranych w tej sesji, w kolejności odebrania.</summary>
    public IReadOnlyList<byte> ReceivedMessageTypes => _messageTypes.ToArray();

    /// <summary>Wszystkie żądania aktualizacji ekranu odebrane w tej sesji.</summary>
    public IReadOnlyList<FakeUpdateRequest> UpdateRequests => _updateRequests.ToArray();

    /// <summary>Bajty sub-encoding każdego wysłanego kafla Hextile (do sprawdzenia pokrycia wariantów).</summary>
    public IReadOnlyList<byte> HextileSubencodings => _hextileSubencodings.ToArray();

    /// <summary>Prawda, gdy połączenie zostało zamknięte (przez klienta, serwer albo tryb „wyrzuć poprzedniego”).</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Czeka na odebranie <c>SetEncodings</c> i zwraca zgłoszoną listę kodowań.</summary>
    public Task<int[]> WaitForEncodingsAsync(TimeSpan timeout) => _encodingsTcs.Task.WaitAsync(timeout);

    /// <summary>Czeka, aż to połączenie zostanie zamknięte.</summary>
    public Task WaitForCloseAsync(TimeSpan timeout) => _closedTcs.Task.WaitAsync(timeout);

    internal void RecordMessageType(byte type) => _messageTypes.Enqueue(type);

    internal void RecordUpdateRequest(FakeUpdateRequest request) => _updateRequests.Enqueue(request);

    internal void RecordHextileSubencoding(byte subencoding) => _hextileSubencodings.Enqueue(subencoding);

    internal void SetEncodings(int[] encodings) => _encodingsTcs.TrySetResult(encodings);

    internal int LastSentScreenVersion { get; set; } = -1;

    /// <summary>
    /// Serializuje zapisy do gniazda tej sesji: pętla wiadomości i zadanie „zaległej” aktualizacji
    /// (<c>DeliverPendingIncrementalOnScreenChange</c>) nie mogą pisać jednocześnie.
    /// </summary>
    internal SemaphoreSlim WriteGate { get; } = new(1, 1);

    /// <summary>Prawda, gdy dla tej sesji działa już zadanie czekające na zmianę ekranu (żeby nie było ich wiele).</summary>
    internal int PendingDeliveryStarted;

    internal bool DesktopSizeAnnounced { get; set; }

    internal void MarkClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _encodingsTcs.TrySetCanceled();
            _closedTcs.TrySetResult();
        }
    }

    internal void Close()
    {
        try
        {
            Tcp.Close();
        }
        catch (Exception)
        {
            // Zamknięcie gniazda w teście nigdy nie może wysadzić serwera.
        }

        MarkClosed();
    }
}
