using Naudit.Benchmark;
using Naudit.Core.Models;
using Naudit.Tests.Fakes;

namespace Naudit.Tests;

public class BenchmarkCaptureTests
{
    private static ReviewRequest Request() => new("getsentry/sentry", 93824, "Titel");

    [Fact]
    public async Task GetChangesAsync_delegiert_an_die_innere_Plattform()
    {
        var inner = new FakeGitPlatform([new CodeChange("a.cs", "@@ -1 +1 @@")]);
        var sut = new CapturingGitPlatform(inner, new ReviewCapture());

        var changes = await sut.GetChangesAsync(Request());

        Assert.Single(changes);
        Assert.Equal("a.cs", changes[0].FilePath);
    }

    [Fact]
    public async Task GetCheckoutAsync_delegiert_und_wird_mitgezaehlt()
    {
        var inner = new FakeGitPlatform([]);
        var capture = new ReviewCapture();
        var sut = new CapturingGitPlatform(inner, capture);

        var info = await sut.GetCheckoutAsync(Request());

        Assert.Equal("refs/test/head", info.HeadRef);
        // Der Zähler ist die einzige von außen sichtbare Spur, dass ein Checkout überhaupt
        // versucht wurde — Naudit schluckt Checkout-Fehler bewusst (fail-open).
        Assert.Equal(1, capture.CheckoutCalls);
    }

    [Fact]
    public void Reset_setzt_Aufzeichnung_und_Checkout_Zaehler_zurueck()
    {
        var capture = new ReviewCapture();
        capture.RecordCheckout();
        capture.Record(Request(), "s", [], ReviewVerdict.Approve);

        capture.Reset();

        Assert.Null(capture.Last);
        Assert.Equal(0, capture.CheckoutCalls);
    }

    [Fact]
    public async Task PostReviewAsync_postet_nicht_und_zeichnet_stattdessen_auf()
    {
        var inner = new FakeGitPlatform([]);
        var capture = new ReviewCapture();
        var sut = new CapturingGitPlatform(inner, capture);
        var comments = new[]
        {
            new InlineComment("a.cs", 12, null, "Fund A", FindingSeverity.High, ReviewConfidence.Medium),
        };

        await sut.PostReviewAsync(Request(), "Zusammenfassung", comments, ReviewVerdict.RequestChanges);

        // Nichts an die echte Plattform durchgereicht.
        Assert.Equal(0, inner.PostCallCount);

        var captured = capture.Last;
        Assert.NotNull(captured);
        Assert.Equal("getsentry/sentry", captured.ProjectId);
        Assert.Equal(93824, captured.MergeRequestIid);
        Assert.Equal("Zusammenfassung", captured.Summary);
        Assert.Equal("RequestChanges", captured.Verdict);
        var only = Assert.Single(captured.Comments);
        Assert.Equal("a.cs", only.FilePath);
        Assert.Equal(12, only.NewLine);
        Assert.Equal("Fund A", only.Body);
        Assert.Equal("High", only.Severity);
        Assert.Equal("Medium", only.Confidence);
    }

    [Fact]
    public async Task PostReviewAsync_liefert_indexgleiche_leere_Ids_zurueck()
    {
        // Vertrag von IGitPlatform: je Eingabe-Kommentar ein PostedComment, Ids dürfen null sein.
        var sut = new CapturingGitPlatform(new FakeGitPlatform([]), new ReviewCapture());
        var comments = new[]
        {
            new InlineComment("a.cs", 1, null, "A"),
            new InlineComment("b.cs", 2, null, "B"),
        };

        var posted = await sut.PostReviewAsync(Request(), "s", comments, ReviewVerdict.Approve);

        Assert.Equal(2, posted.Count);
        Assert.All(posted, p => Assert.Null(p.CommentId));
        Assert.All(posted, p => Assert.Null(p.NoteId));
    }
}
