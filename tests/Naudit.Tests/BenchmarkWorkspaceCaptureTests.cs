using Naudit.Benchmark;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests;

/// <summary>Der ausgecheckte Commit ist die Milderung zu einer Abweichung, die in der Arbeit steht:
/// die Vorlage ist nicht eingefroren. RepoCheckoutInfo.HeadRef ist immer "refs/pull/N/head" und
/// sagt nichts über den Stand — .git/HEAD des Workspace schon.</summary>
public class BenchmarkWorkspaceCaptureTests
{
    private static ReviewRequest Request() => new("getsentry/sentry", 93824, "Titel");

    private sealed class FakeWorkspace(string root) : IReviewWorkspace
    {
        public string RootPath { get; } = root;
        public string ProjectId => "getsentry/sentry";
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeWorkspaceProvider(string? root, Exception? error = null) : IWorkspaceProvider
    {
        public Task<IReviewWorkspace> CheckoutAsync(ReviewRequest request, CancellationToken ct = default)
            => error is not null
                ? Task.FromException<IReviewWorkspace>(error)
                : Task.FromResult<IReviewWorkspace>(new FakeWorkspace(root!));
    }

    private static DirectoryInfo RepoWith(string headContent)
    {
        var dir = Directory.CreateTempSubdirectory("naudit-ws-");
        Directory.CreateDirectory(Path.Combine(dir.FullName, ".git"));
        File.WriteAllText(Path.Combine(dir.FullName, ".git", "HEAD"), headContent);
        return dir;
    }

    [Fact]
    public async Task Detached_HEAD_liefert_die_ausgecheckte_Commit_SHA()
    {
        // Genau der Zustand nach GitWorkspaceProvider: init → fetch → checkout FETCH_HEAD.
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var dir = RepoWith(sha + "\n");
        try
        {
            var capture = new ReviewCapture();
            var sut = new CapturingWorkspaceProvider(new FakeWorkspaceProvider(dir.FullName), capture);

            await using var ws = await sut.CheckoutAsync(Request());

            Assert.Equal(sha, capture.HeadSha);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task Symbolischer_Ref_liefert_keine_SHA()
    {
        var dir = RepoWith("ref: refs/heads/main\n");
        try
        {
            var capture = new ReviewCapture();
            var sut = new CapturingWorkspaceProvider(new FakeWorkspaceProvider(dir.FullName), capture);

            await using var ws = await sut.CheckoutAsync(Request());

            Assert.Null(capture.HeadSha);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task Fehlende_Git_Datei_kippt_das_Review_nicht()
    {
        var dir = Directory.CreateTempSubdirectory("naudit-ws-");
        try
        {
            var capture = new ReviewCapture();
            var sut = new CapturingWorkspaceProvider(new FakeWorkspaceProvider(dir.FullName), capture);

            await using var ws = await sut.CheckoutAsync(Request());

            Assert.Null(capture.HeadSha);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task Gescheiterter_Klon_gilt_als_gescheiterter_Checkout()
    {
        // GitWorkspaceProvider wirft, wenn git init/fetch/checkout scheitert. ReviewService
        // schluckt das still und reviewt diff-only weiter — hier wird es sichtbar.
        var boom = new InvalidOperationException("git fetch schlug fehl (Exit 128).");
        var capture = new ReviewCapture();
        var sut = new CapturingWorkspaceProvider(new FakeWorkspaceProvider(null, boom), capture);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.CheckoutAsync(Request()));

        Assert.Same(boom, thrown);
        Assert.Equal(1, capture.CheckoutFailures);
        Assert.Null(capture.HeadSha);
    }
}
