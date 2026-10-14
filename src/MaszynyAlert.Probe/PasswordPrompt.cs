using System.Text;

namespace MaszynyAlert.Probe;

/// <summary>Czytanie haseł i potwierdzeń z konsoli — hasło jest maskowane gwiazdkami, nigdy nie trafia na dysk.</summary>
public static class PasswordPrompt
{
    /// <summary>
    /// Wypisuje <paramref name="prompt"/> i czyta hasło bez pokazywania go na ekranie (gwiazdki,
    /// Backspace kasuje ostatni znak). Gdy wejście jest przekierowane (testy, potok) — konsola nie
    /// obsługuje wtedy <see cref="Console.ReadKey(bool)"/> — czyta zwykłą linię przez
    /// <see cref="Console.ReadLine"/>.
    /// </summary>
    public static string ReadPassword(string prompt)
    {
        Console.Write(prompt);

        if (Console.IsInputRedirected)
        {
            // PS 5.1 potrafi poprzedzić potok znacznikiem BOM — nie jest on częścią hasła.
            return (Console.ReadLine() ?? string.Empty).TrimStart('﻿');
        }

        var buffer = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                    Console.Write("\b \b");
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
                Console.Write('*');
            }
        }

        return buffer.ToString();
    }

    /// <summary>
    /// Wypisuje <paramref name="prompt"/> i czyta zwykłą, WIDOCZNĄ linię — do danych, które nie są
    /// sekretem (nazwa użytkownika do logowania systemowego RSA-AES). Puste wejście zwraca pusty ciąg.
    /// </summary>
    public static string ReadText(string prompt)
    {
        Console.Write(prompt);
        return (Console.ReadLine() ?? string.Empty).Trim();
    }

    /// <summary>
    /// Pytanie tak/nie z domyślną odpowiedzią (Enter = domyślna). Akceptuje "t"/"tak"/"y"/"yes" jako
    /// tak i "n"/"nie"/"no" jako nie (bez rozróżniania wielkości liter); cokolwiek innego → domyślna.
    /// </summary>
    public static bool ReadYesNo(string prompt, bool defaultYes)
    {
        Console.Write(prompt);
        string? line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line))
        {
            return defaultYes;
        }

        char c = char.ToLowerInvariant(line.Trim()[0]);
        return c switch
        {
            't' or 'y' => true,
            'n' => false,
            _ => defaultYes,
        };
    }
}
