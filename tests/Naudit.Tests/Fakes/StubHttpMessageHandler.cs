namespace Naudit.Tests.Fakes;

internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    // Alle Requests in Reihenfolge — nötig, weil PostReviewAsync mehrere Calls absetzt (GitLab: GET + N×POST).
    public List<(HttpMethod Method, Uri? Uri, string? Body)> Calls { get; } = new();

    // Die vollständigen Request-Objekte (für Header-Prüfungen pro Call, z. B. PRIVATE-TOKEN/Authorization).
    public List<HttpRequestMessage> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        string? body = null;
        if (request.Content is not null)
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        LastRequestBody = body;
        Calls.Add((request.Method, request.RequestUri, body));
        Requests.Add(request);
        return responder(request);
    }
}
