using Naudit.Core.Abstractions;

namespace Naudit.Tests.Fakes;

/// <summary>Test-Redactor: ersetzt jedes Vorkommen eines Sentinels durch «red». So lässt sich
/// beweisen, dass <c>ReviewService</c> den Redactor auf Diff, Finding-Message und Titel anwendet.</summary>
internal sealed class FakePromptRedactor(string sentinel) : IPromptRedactor
{
    public int Calls { get; private set; }

    public Task<string> RedactAsync(string text, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(text.Replace(sentinel, "«red»"));
    }
}
