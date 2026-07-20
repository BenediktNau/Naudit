namespace Naudit.Tests.Fakes;

// Stellbare Uhr für Idle-/LRU-Tests (TimeProvider ist in-box, kein Testing-NuGet nötig).
internal sealed class FakeTime : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => UtcNow;
}
