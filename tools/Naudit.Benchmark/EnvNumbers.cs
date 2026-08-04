using System.Globalization;

namespace Naudit.Benchmark;

/// <summary>Zahlen aus der Umgebung. Ein GESETZTER, aber unlesbarer Wert scheitert laut statt still
/// auf den Default zu fallen: ein Tippfehler in NAUDIT_BENCHMARK_LIMIT liefe sonst als "alle 50"
/// durch — ein Vollauf statt eines Smoke-Tests, Stunden Laufzeit und Abo-Kontingent.</summary>
public static class EnvNumbers
{
    public static int Read(string name, int fallback, Func<string, string?> read, int min = 0)
    {
        var raw = read(name);
        if (raw is null || raw.Trim().Length == 0)
            return fallback;

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException(
                $"{name} ist gesetzt, aber keine ganze Zahl: '{raw}'. Variable korrigieren oder ganz entfernen.");

        if (value < min)
            throw new InvalidOperationException(
                $"{name} ist gesetzt, aber kleiner als {min}: '{raw}'.");

        return value;
    }

    public static int Read(string name, int fallback, int min = 0)
        => Read(name, fallback, Environment.GetEnvironmentVariable, min);
}
