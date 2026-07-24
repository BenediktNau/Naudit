using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

internal sealed class FakeWorkspaceProvider(string rootPath = "/tmp/ws") : IWorkspaceProvider
{
    public bool CheckoutCalled { get; private set; }
    public bool ThrowOnCheckout { get; set; }

    public Task<IReviewWorkspace> CheckoutAsync(ReviewRequest request, CancellationToken ct = default)
    {
        CheckoutCalled = true;
        if (ThrowOnCheckout)
            throw new InvalidOperationException("checkout failed");
        return Task.FromResult<IReviewWorkspace>(new FakeWorkspace(rootPath, request.ProjectId));
    }

    private sealed class FakeWorkspace(string root, string projectId) : IReviewWorkspace
    {
        public string RootPath { get; } = root;
        public string ProjectId { get; } = projectId;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
