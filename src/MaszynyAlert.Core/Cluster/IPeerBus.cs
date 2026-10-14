namespace MaszynyAlert.Core.Cluster;

public interface IPeerBus : IAsyncDisposable
{
    /// <summary>Nazwa hosta tego komputera — wpisywana jako <see cref="PeerMessage.Sender"/> i używana do odrzucania własnych pakietów.</summary>
    string LocalHost { get; }

    /// <summary>Uruchamia nasłuch. Idempotentne — kolejne wywołania nic nie robią.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Rozgłasza wiadomość do wszystkich znanych celów (broadcast/peery).</summary>
    Task SendAsync(PeerMessage msg, CancellationToken ct);

    /// <summary>Odebrana i zweryfikowana (podpis HMAC + okno czasowe) wiadomość od innego komputera.</summary>
    event Action<PeerMessage>? Received;

    /// <summary>Odrzucony pakiet — powód po polsku (do logu/diagnostyki).</summary>
    event Action<string>? Rejected;
}
