using System;
using System.Net.Http;
using System.Reflection;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LMail.Internal
{
    internal static class ProviderHttp
    {
        internal static readonly string UserAgent = "LMail/" + (
            typeof(ProviderHttp).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                .Split('+')[0] ?? "0");

        private const int MaxResponseBodyLength = 4000;

        private static readonly HttpClient SharedClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(100)
        };

        internal static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static HttpClient Resolve(HttpClient? httpClient)
        {
            return httpClient ?? SharedClient;
        }

        public static async Task<HttpResponseMessage> SendJsonAsync(
            HttpClient client,
            string provider,
            string url,
            object payload,
            Action<HttpRequestMessage>? configure,
            CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                return await SendAsync(client, provider, request, configure, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public static async Task<HttpResponseMessage> SendAsync(
            HttpClient client,
            string provider,
            HttpRequestMessage request,
            Action<HttpRequestMessage>? configure,
            CancellationToken cancellationToken)
        {
            request.Headers.UserAgent.ParseAdd(UserAgent);
            configure?.Invoke(request);

            try
            {
                return await client
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new EmailSendException(provider + " request timed out.", provider, null, null, ex);
            }
            catch (HttpRequestException ex)
            {
                throw new EmailSendException(
                    provider + " request failed: " + ex.Message,
                    provider,
                    null,
                    null,
                    ex);
            }
        }

        public static async Task<string> EnsureSuccessAsync(HttpResponseMessage response, string provider)
        {
            var body = response.Content == null
                ? string.Empty
                : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new EmailSendException(
                    provider + " send failed with HTTP " + (int)response.StatusCode +
                    " (" + response.StatusCode + ").",
                    provider,
                    (int)response.StatusCode,
                    Truncate(body));
            }

            return body;
        }

        public static string Truncate(string value)
        {
            if (value.Length <= MaxResponseBodyLength)
            {
                return value;
            }

            return value.Substring(0, MaxResponseBodyLength) + "...";
        }
    }
}
