namespace Naudit.Tests.Fakes;

internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    // Alle Requests in Reihenfolge — nötig, weil PostReviewAsync mehrere Calls absetzt (GitLab: GET + N×POST).
    public List<(HttpMethod Method, Uri? Uri, string? Body)> Calls { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        string? body = null;
        if (request.Content is not null)
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        LastRequestBody = body;
        Calls.Add((request.Method, request.RequestUri, body));
        return responder(request);
    }
}
