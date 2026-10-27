using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LMail.Samples
{
    internal sealed class CapturedRequest
    {
        public CapturedRequest(
            HttpMethod method,
            Uri uri,
            Dictionary<string, string> headers,
            string body)
        {
            Method = method;
            Uri = uri;
            Headers = headers;
            Body = body;
        }

        public HttpMethod Method { get; }

        public Uri Uri { get; }

        public Dictionary<string, string> Headers { get; }

        public string Body { get; }

        public string? Header(string name)
        {
            return Headers.TryGetValue(name, out var value) ? value : null;
        }
    }

    internal sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<CapturedRequest, HttpResponseMessage> _responder;

        public StubHttpHandler(Func<CapturedRequest, HttpResponseMessage>? responder = null)
        {
            _responder = responder ?? (_ => JsonResponse("{}"));
        }

        public List<CapturedRequest> Requests { get; } = new List<CapturedRequest>();

        public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json)
            };
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var header in request.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }

            if (request.Content != null)
            {
                foreach (var header in request.Content.Headers)
                {
                    headers[header.Key] = string.Join(",", header.Value);
                }
            }

            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var captured = new CapturedRequest(request.Method, request.RequestUri!, headers, body);
            Requests.Add(captured);

            return _responder(captured);
        }
    }
}
