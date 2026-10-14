namespace MaszynyAlert.Core.Vision;

/// <summary>
/// Filtr "N z M" — potwierdza epizod czerwieni dopiero, gdy w przesuwnym oknie ostatnich
/// <c>window</c> odczytów co najmniej <c>required</c> było czerwonych. Działa symetrycznie: gdy
/// czerwień znika, potwierdzenie wraca do <see langword="false"/>, gdy liczba trafień w oknie
/// spadnie poniżej <c>required</c> (nie trzeba osobno wołać <see cref="Reset"/>).
/// </summary>
public sealed class RedEpisodeFilter
{
    private readonly bool[] _window;
    private readonly int _required;
    private int _nextIndex;
    private int _filledCount;

    public RedEpisodeFilter(int window = 3, int required = 2)
    {
        if (window <= 0)
        {
            throw new ArgumentException("Rozmiar okna musi być dodatni.", nameof(window));
        }

        if (required <= 0 || required > window)
        {
            throw new ArgumentException("Wymagana liczba trafień musi być w zakresie 1..window.", nameof(required));
        }

        _window = new bool[window];
        _required = required;
    }

    /// <summary>Stan potwierdzenia po ostatnim <see cref="Push"/> ("2 z 3" itp.).</summary>
    public bool Confirmed { get; private set; }

    /// <summary>Liczba odczytów w oknie od ostatniego <see cref="Reset"/> (najwyżej rozmiar okna).</summary>
    public int SampleCount => _filledCount;

    /// <summary>Liczba trafień w oknie wymagana do potwierdzenia.</summary>
    public int Required => _required;

    /// <summary>Dodaje kolejny odczyt (czerwień wykryta w tym cyklu / nie) i przelicza <see cref="Confirmed"/>.</summary>
    public bool Push(bool isRed)
    {
        _window[_nextIndex] = isRed;
        _nextIndex = (_nextIndex + 1) % _window.Length;
        if (_filledCount < _window.Length)
        {
            _filledCount++;
        }

        int trueCount = 0;
        for (int i = 0; i < _filledCount; i++)
        {
            if (_window[i])
            {
                trueCount++;
            }
        }

        Confirmed = trueCount >= _required;
        return Confirmed;
    }

    /// <summary>Czyści okno i wraca do stanu początkowego (bez potwierdzenia).</summary>
    public void Reset()
    {
        Array.Clear(_window);
        _nextIndex = 0;
        _filledCount = 0;
        Confirmed = false;
    }
}
