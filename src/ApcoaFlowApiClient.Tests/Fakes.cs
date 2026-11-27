using System.Net;
using System.Text;
using ApcoaFlowApiClient.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace ApcoaFlowApiClient.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// The fake network under both pipelines (API and auth server): answers by "METHOD path", records every request.
/// </summary>
internal sealed class FakeNetwork : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<Func<HttpRequestMessage, HttpResponseMessage>>> _routes = new();
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    /// <summary>Queue responses for a route; the last one repeats once the queue is down to it.</summary>
    public FakeNetwork On(string methodAndPath, params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        _routes[methodAndPath] = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
        return this;
    }

    public int Count(string methodAndPath) =>
        Requests.Count(r => $"{r.Request.Method} {r.Request.RequestUri!.AbsolutePath}" == methodAndPath);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        lock (Requests) Requests.Add((request, body));
        var key = $"{request.Method} {request.RequestUri!.AbsolutePath}";
        if (!_routes.TryGetValue(key, out var queue) || queue.Count == 0)
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = Json("""{"code":"NoFakeRoute"}"""), RequestMessage = request };
        var respond = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        var response = respond(request);
        response.RequestMessage = request; // Refit needs it to build an ApiException
        return response;
    }

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public static Func<HttpRequestMessage, HttpResponseMessage> Reply(HttpStatusCode status, string? json = null) =>
        _ => new HttpResponseMessage(status) { Content = json is null ? new StringContent("") : Json(json) };

    public static Func<HttpRequestMessage, HttpResponseMessage> Token(string access, string refresh, int expiresIn = 3600) =>
        Reply(HttpStatusCode.OK, $$"""{"access_token":"{{access}}","token_type":"Bearer","expires_in":{{expiresIn}},"refresh_token":"{{refresh}}"}""");

    public static readonly Func<HttpRequestMessage, HttpResponseMessage> InvalidGrant =
        Reply(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"The username/password couple is invalid."}""");

    public static readonly Func<HttpRequestMessage, HttpResponseMessage> Unauthenticated =
        Reply(HttpStatusCode.Forbidden, """{"code":"Unauthenticated","message":"An error has occurred."}""");
}

internal static class TestClient
{
    public static readonly ApcoaFlowCredentials Account = new("test@example.com", "secret");

    /// <summary>The real client wiring with the network replaced by <paramref name="network"/>.</summary>
    public static ServiceProvider Build(FakeNetwork network, TimeProvider time)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(time);
        services.AddSingleton<IApcoaFlowCredentialProvider>(new Static(Account));
        services.AddApcoaFlowApiClient(b => b.ConfigurePrimaryHttpMessageHandler(() => network));
        return services.BuildServiceProvider();
    }

    private sealed class Static(ApcoaFlowCredentials c) : IApcoaFlowCredentialProvider
    {
        public ApcoaFlowCredentials GetCredentials() => c;
    }
}
