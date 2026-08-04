using Naudit.Benchmark;

namespace Naudit.Tests;

public class BenchmarkEnvNumbersTests
{
    private static Func<string, string?> Env(string? value) => _ => value;

    [Fact]
    public void Nicht_gesetzt_liefert_den_Default()
        => Assert.Equal(int.MaxValue, EnvNumbers.Read("NAUDIT_BENCHMARK_LIMIT", int.MaxValue, Env(null)));

    [Fact]
    public void Leer_liefert_den_Default()
        => Assert.Equal(20, EnvNumbers.Read("NAUDIT_BENCHMARK_PAUSE_SECONDS", 20, Env("   ")));

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData(" 50 ", 50)]
    public void Gueltige_Zahl_wird_gelesen(string raw, int expected)
        => Assert.Equal(expected, EnvNumbers.Read("NAUDIT_BENCHMARK_LIMIT", int.MaxValue, Env(raw)));

    [Theory]
    [InlineData("l")]      // Tippfehler statt 1
    [InlineData("eins")]
    [InlineData("1,5")]
    public void Gesetzter_aber_unlesbarer_Wert_scheitert_laut(string raw)
    {
        // Still auf "alle 50" zu fallen hieße: statt eines Smoke-Tests läuft der Vollauf —
        // Stunden Laufzeit und Abo-Kontingent, ohne dass es jemand merkt.
        var ex = Assert.Throws<InvalidOperationException>(
            () => EnvNumbers.Read("NAUDIT_BENCHMARK_LIMIT", int.MaxValue, Env(raw)));
        Assert.Contains("NAUDIT_BENCHMARK_LIMIT", ex.Message);
        Assert.Contains(raw, ex.Message);
    }

    [Fact]
    public void Negativer_Wert_scheitert_laut()
        => Assert.Throws<InvalidOperationException>(
            () => EnvNumbers.Read("NAUDIT_BENCHMARK_LIMIT", int.MaxValue, Env("-1")));
}
