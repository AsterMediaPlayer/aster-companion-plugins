using System.Net;
using System.Text;

namespace Aster.Plex.Tests;

/// <summary>A Plex server and plex.tv answering from canned replies, by path (and query, when given).</summary>
internal sealed class FakePlex : HttpMessageHandler, IHttpClientFactory
{
    public Dictionary<string, string> Replies { get; } = new();

    public List<string> Asked { get; } = new();

    /// <summary>What was sent, by path.</summary>
    public Dictionary<string, string> Sent { get; } = new();

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var full = uri.Host + uri.AbsolutePath + uri.Query;
        var path = uri.Host + uri.AbsolutePath;
        lock (Asked)
        {
            Asked.Add(full);
        }

        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Sent)
            {
                Sent[path] = body;
            }
        }

        var reply = Replies.TryGetValue(full, out var exact) ? exact : Replies.GetValueOrDefault(path);
        return reply is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, reply.StartsWith('<') ? "application/xml" : "application/json") };
    }
}
