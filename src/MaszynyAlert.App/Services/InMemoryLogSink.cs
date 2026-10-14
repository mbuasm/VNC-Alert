using System.IO;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace MaszynyAlert.App.Services;

public sealed class InMemoryLogSink : ILogEventSink
{
    private readonly MessageTemplateTextFormatter _formatter;
    private readonly Queue<string> _lines;
    private readonly int _capacity;
    private readonly object _lock = new();

    public InMemoryLogSink(int capacity = 500)
    {
        _capacity = capacity;
        _lines = new Queue<string>(capacity);
        _formatter = new MessageTemplateTextFormatter("{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}");
    }

    /// <summary>Jednolinijkowy opis wyjątku do Panelu: typ i pierwsza linia komunikatu (bez śladu stosu).</summary>
    public static string ExceptionSummary(Exception exception)
    {
        string message = exception.Message.Split('\n', 2)[0].TrimEnd('\r');
        return $"{exception.GetType().Name}: {message}";
    }

    /// <summary>Wysyłane po dodaniu nowej linii — okno statusu może się odświeżyć bez odpytywania.</summary>
    public event Action<string>? LineAdded;

    /// <summary>Migawka aktualnie przechowywanych linii, najstarsza pierwsza.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lock)
            {
                return _lines.ToArray();
            }
        }
    }

    public void Emit(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        _formatter.Format(logEvent, writer);
        string line = writer.ToString().TrimEnd('\r', '\n');
        if (logEvent.Exception is not null) line += " — " + ExceptionSummary(logEvent.Exception);

        lock (_lock)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _capacity)
            {
                _lines.Dequeue();
            }
        }

        LineAdded?.Invoke(line);
    }
}
