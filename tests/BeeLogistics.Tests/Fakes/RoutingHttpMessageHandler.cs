using System.Net;
using System.Text;

namespace BeeLogistics.Tests.Fakes;

/// <summary>
/// HttpMessageHandler that picks its response by matching the request URI, and records every
/// request it saw.
/// <para>
/// <see cref="CapturingHttpMessageHandler"/> returns one canned response, which cannot express a
/// call that fans out into several requests — PayMongo's <c>fields</c> parameter accepts only one
/// value per request, so reading a wallet's account number and balance is three round trips with
/// three different responses.
/// </para>
/// </summary>
public sealed class RoutingHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, HttpStatusCode Status, string Body)> _routes = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>
    /// Request bodies, captured at send time and index-aligned with <see cref="Requests"/>.
    /// <para>
    /// Read these rather than <c>Requests[i].Content</c>: callers that build the request with
    /// <c>using var message = new HttpRequestMessage(...)</c> dispose the content as soon as the
    /// call returns, so reading it afterwards throws ObjectDisposedException.
    /// </para>
    /// </summary>
    public List<string?> RequestBodies { get; } = new();

    /// <summary>Requests whose URI (path + query) contains <paramref name="uriContains"/>.</summary>
    public RoutingHttpMessageHandler When(string uriContains, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((r => (r.RequestUri?.PathAndQuery ?? "").Contains(uriContains, StringComparison.Ordinal), status, body));
        return this;
    }

    public RoutingHttpMessageHandler When(Func<HttpRequestMessage, bool> match, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((match, status, body));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken));

        // Last matching route wins, so a test can register a general case then override it.
        for (var i = _routes.Count - 1; i >= 0; i--)
        {
            if (!_routes[i].Match(request)) continue;
            return new HttpResponseMessage(_routes[i].Status)
            {
                Content = new StringContent(_routes[i].Body, Encoding.UTF8, "application/json")
            };
        }

        // An unmatched request is a test bug, not a 404 to be silently absorbed - say which one.
        throw new InvalidOperationException(
            $"No route registered for {request.Method} {request.RequestUri?.PathAndQuery}");
    }
}
