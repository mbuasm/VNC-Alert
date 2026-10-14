using System.Text;

namespace MaszynyAlert.Core.History;

/// <summary>Rozpoznaje koniec history.csv bez zmieniania pliku. Po błędzie odczytu nie ma wyniku do przycięcia.</summary>
internal static class HistoryCsvTail
{
    internal readonly record struct AppendPoint(long Offset, string Separator, bool WriteHeader = false);
    private enum FieldState { Start, Unquoted, Quoted, AfterQuote }

    internal static AppendPoint Analyze(FileStream stream, string header)
    {
        long length = LengthWithoutTrailingZeros(stream);
        stream.Position = 0;
        if (length == 0) return new(0, "", WriteHeader: true);

        // BOM może być urwany tak samo jak pierwszy nagłówek. Rozpoznajemy tylko jego dokładny prefiks.
        ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
        Span<byte> beginning = stackalloc byte[(int)Math.Min(3, length)];
        stream.ReadExactly(beginning);
        if (beginning.SequenceEqual(bom[..beginning.Length]))
        {
            if (beginning.Length < 3) return new(0, "", WriteHeader: true);
        }
        else
        {
            stream.Position = 0; // istniejący UTF-8 bez BOM też jest obsługiwany
        }

        foreach (char expected in header) // stały nagłówek history.csv ma wyłącznie znaki ASCII
        {
            int value = ReadByteBefore(stream, length);
            if (value == -1) return new(0, "", WriteHeader: true);
            if (value != expected) throw new InvalidDataException("Nieznany nagłówek history.csv.");
        }

        int terminator = ReadByteBefore(stream, length);
        if (terminator == -1) return new(length, Environment.NewLine);
        if (terminator is not ('\r' or '\n')) throw new InvalidDataException("Niepoprawny koniec nagłówka history.csv.");

        long position = stream.Position;
        long completeEnd = position;
        bool completeEndsWithCr = terminator == '\r';
        bool afterCr = completeEndsWithCr;
        bool hasFields = false;
        int fields = 1;
        var state = FieldState.Start;
        var decoder = new UTF8Encoding(false, throwOnInvalidBytes: true).GetDecoder();
        byte[] buffer = new byte[4096];
        char[] characters = new char[buffer.Length + 1];
        int read;
        while (position < length && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - position))) > 0)
        {
            // CSV rozpoznajemy po bajtach ASCII, więc nie przeliczamy ani nie przepisujemy poprawnego prefiksu.
            // Dekoder wykrywa uszkodzenie UTF-8 oraz odróżnia urwany ostatni znak od kompletnego pola na EOF.
            decoder.Convert(buffer.AsSpan(0, read), characters, false, out _, out _, out _);
            for (int i = 0; i < read; i++)
            {
                byte value = buffer[i];
                position++;
                if (afterCr)
                {
                    afterCr = false;
                    if (value == '\n')
                    {
                        completeEnd = position;
                        completeEndsWithCr = false;
                        continue;
                    }
                }
                if (state == FieldState.Quoted)
                {
                    if (value == '"') state = FieldState.AfterQuote;
                    continue;
                }
                if (value == '"')
                {
                    if (state is not (FieldState.Start or FieldState.AfterQuote))
                        throw new InvalidDataException("Niepoprawne cytowanie w history.csv.");
                    state = FieldState.Quoted; // po AfterQuote to druga połowa pary ""
                    hasFields = true;
                }
                else if (value == ';')
                {
                    if (++fields > 8) throw new InvalidDataException("Za dużo kolumn w history.csv.");
                    state = FieldState.Start;
                    hasFields = true;
                }
                else if (value is (byte)'\r' or (byte)'\n')
                {
                    if (hasFields && fields != 8) throw new InvalidDataException("Niepełny rekord wewnątrz history.csv.");
                    completeEnd = position;
                    completeEndsWithCr = afterCr = value == '\r';
                    fields = 1;
                    state = FieldState.Start;
                    hasFields = false;
                }
                else
                {
                    if (state == FieldState.AfterQuote) throw new InvalidDataException("Dane po zamknięciu cytowania w history.csv.");
                    state = FieldState.Unquoted;
                    hasFields = true;
                }
            }
        }
        if (position != length) throw new EndOfStreamException("Nie odczytano całego history.csv.");

        bool incompleteUtf8 = false;
        try { decoder.Convert(ReadOnlySpan<byte>.Empty, characters, true, out _, out _, out _); }
        catch (DecoderFallbackException) { incompleteUtf8 = true; }

        // Pełne osiem pól na EOF jest poprawnym CSV także bez terminatora. Nie można odróżnić ucięcia
        // samej treści ostatniego pola od krótszej poprawnej wartości — takie rekordy zachowujemy.
        if (hasFields && fields == 8 && state != FieldState.Quoted && !incompleteUtf8)
            return new(length, Environment.NewLine);

        if (completeEnd < length && ContainsLaterRecordStart(stream, completeEnd, length))
            throw new InvalidDataException("Urwany rekord history.csv sklejony z późniejszymi wpisami.");

        // Tylko niedokończony końcowy rekord odpada; '\n' wewnątrz cytowania nie jest granicą.
        return new(completeEnd, completeEndsWithCr ? "\n" : "");
    }

    /// <summary>
    /// Długość pliku bez końcowego ciągu bajtów NUL (wypełnienie zerami po utracie zasilania). Czyta od końca — zwykle
    /// jeden blok; błąd odczytu przerywa analizę jak każdy inny (zapis czeka w kolejce, plik zostaje nietknięty).
    /// </summary>
    private static long LengthWithoutTrailingZeros(FileStream stream)
    {
        byte[] buffer = new byte[4096];
        long end = stream.Length;
        while (end > 0)
        {
            int count = (int)Math.Min(buffer.Length, end);
            stream.Position = end - count;
            stream.ReadExactly(buffer, 0, count);
            int last = buffer.AsSpan(0, count).LastIndexOfAnyExcept((byte)0);
            if (last >= 0) return end - count + last + 1;
            end -= count;
        }

        return 0;
    }

    /// <summary>Kolejny bajt albo -1 na granicy <paramref name="end"/> (końcowe zera są już poza nią).</summary>
    private static int ReadByteBefore(FileStream stream, long end) => stream.Position < end ? stream.ReadByte() : -1;

    /// <summary>
    /// Czy w zakresie [<paramref name="start"/>, <paramref name="end"/>) poza jego pierwszym bajtem zaczyna się rekord
    /// historii — znacznik czasu <c>yyyy-MM-dd HH:mm:ss</c> zakończony średnikiem (pierwsze pole każdego wiersza).
    /// </summary>
    private static bool ContainsLaterRecordStart(FileStream stream, long start, long end)
    {
        const string Pattern = "0000-00-00 00:00:00;"; // '0' = dowolna cyfra
        byte[] buffer = new byte[4096 + Pattern.Length];
        int carried = 0;
        long position = start;
        stream.Position = start;
        while (position < end)
        {
            int read = stream.Read(buffer, carried, (int)Math.Min(buffer.Length - carried, end - position));
            if (read <= 0) throw new EndOfStreamException("Nie odczytano całego history.csv.");
            long bufferStart = position - carried;
            position += read;
            int available = carried + read;
            for (int i = 0; i + Pattern.Length <= available; i++)
            {
                if (bufferStart + i == start) continue;
                bool match = true;
                for (int j = 0; j < Pattern.Length && match; j++)
                {
                    byte value = buffer[i + j];
                    match = Pattern[j] == '0' ? value is >= (byte)'0' and <= (byte)'9' : value == Pattern[j];
                }
                if (match) return true;
            }
            carried = Math.Min(Pattern.Length - 1, available);
            Array.Copy(buffer, available - carried, buffer, 0, carried);
        }
        return false;
    }

    /// <summary>
    /// Granica dopisania za treścią, której <see cref="Analyze"/> nie rozpoznał: koniec pliku, bez przycinania.
    /// Separator tylko domyka ostatnią linię, żeby nowy rekord nie skleił się z nieznaną treścią. Końcowe zera
    /// nierozpoznanego pliku też zostają — w obcym formacie mogą być treścią (np. UTF-16 kończy się bajtem 0).
    /// </summary>
    internal static AppendPoint AfterUnrecognizedContent(FileStream stream)
    {
        long length = stream.Length;
        if (length == 0) return new(0, "", WriteHeader: true);
        stream.Position = length - 1;
        return stream.ReadByte() switch
        {
            '\n' => new(length, ""),
            '\r' => new(length, "\n"),
            _ => new(length, Environment.NewLine),
        };
    }
}