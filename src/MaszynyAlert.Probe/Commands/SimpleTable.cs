using System.Text;

namespace MaszynyAlert.Probe.Commands;

/// <summary>Bardzo prosty tekstowy wypisywacz tabel (wyrównane kolumny, separator " | ") — bez zależności zewnętrznych.</summary>
internal sealed class SimpleTable(params string[] headers)
{
    private readonly List<string[]> _rows = [];

    public void AddRow(params string[] cells)
    {
        if (cells.Length != headers.Length)
        {
            throw new ArgumentException($"Wiersz ma {cells.Length} komórek, a nagłówek {headers.Length}.", nameof(cells));
        }

        _rows.Add(cells);
    }

    public IEnumerable<string> Render()
    {
        int[] widths = new int[headers.Length];
        for (int c = 0; c < headers.Length; c++)
        {
            widths[c] = headers[c].Length;
        }

        foreach (string[] row in _rows)
        {
            for (int c = 0; c < headers.Length; c++)
            {
                widths[c] = Math.Max(widths[c], row[c].Length);
            }
        }

        yield return RenderRow(headers, widths);
        yield return string.Join("-+-", widths.Select(w => new string('-', w)));
        foreach (string[] row in _rows)
        {
            yield return RenderRow(row, widths);
        }
    }

    private static string RenderRow(string[] cells, int[] widths)
    {
        var sb = new StringBuilder();
        for (int c = 0; c < cells.Length; c++)
        {
            if (c > 0)
            {
                sb.Append(" | ");
            }

            sb.Append(cells[c].PadRight(widths[c]));
        }

        return sb.ToString();
    }
}
