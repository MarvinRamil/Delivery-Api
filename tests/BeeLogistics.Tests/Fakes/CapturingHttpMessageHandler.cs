using System.Net;
using System.Text;

namespace BeeLogistics.Tests.Fakes;

/// <summary>
/// HttpMessageHandler for gateway adapter tests: returns a canned response and
/// captures the request (method, URL, headers, body) for assertions.
/// </summary>
public sealed class CapturingHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _responseBody;

    public HttpRequestMessage? Request { get; private set; }
    public string? RequestBody { get; private set; }

    public CapturingHttpMessageHandler(HttpStatusCode statusCode, string responseBody)
    {
        _statusCode = statusCode;
        _responseBody = responseBody;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = request;
        RequestBody = request.Content != null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : null;

        return new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
        };
    }
}
