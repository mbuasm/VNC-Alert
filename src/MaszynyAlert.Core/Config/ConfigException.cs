namespace MaszynyAlert.Core.Config;

/// <summary>
/// Błąd konfiguracji — wiadomość jest po polsku i sformułowana tak, żeby zrozumiała ją osoba
/// niebędąca programistą (np. wskazuje, który plik i które pole poprawić).
/// </summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message) : base(message)
    {
    }

    public ConfigException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
