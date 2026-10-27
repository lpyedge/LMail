using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LMail.Internal;

namespace LMail
{
    /// <summary>
    /// Sends MIME messages with Microsoft Graph <c>sendMail</c>. The whole message, including
    /// inline images and headers, is sent as base64 MIME in one request.
    /// </summary>
    public sealed class GraphEmailSender : IEmailSender
    {
        private const string DefaultBaseUrl = "https://graph.microsoft.com/v1.0";

        private readonly IAccessTokenProvider _tokenProvider;
        private readonly HttpClient _httpClient;
        private readonly string _requestUrl;

        /// <summary>
        /// Creates a Graph sender.
        /// </summary>
        /// <param name="tokenProvider">
        /// Token source. Use <see cref="MicrosoftOAuth2.CreateGraphOptions"/> or
        /// <see cref="MicrosoftOAuth2.CreateGraphClientCredentialsOptions"/> to create it.
        /// </param>
        /// <param name="userId">
        /// User ID or UPN for application-style sending (<c>/users/{id}/sendMail</c>). When omitted,
        /// <c>/me/sendMail</c> is used and the token must be for a delegated user.
        /// </param>
        /// <param name="httpClient">Optional HTTP client.</param>
        public GraphEmailSender(
            IAccessTokenProvider tokenProvider,
            string? userId = null,
            HttpClient? httpClient = null)
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _httpClient = ProviderHttp.Resolve(httpClient);

            _requestUrl = string.IsNullOrWhiteSpace(userId)
                ? DefaultBaseUrl + "/me/sendMail"
                : DefaultBaseUrl + "/users/" + Uri.EscapeDataString(userId!) + "/sendMail";
        }

        /// <inheritdoc />
        public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            var mime = MimeMessageFactory.Create(message, includeBcc: true);
            var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(Convert.ToBase64String(mime)));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

            using (var request = new HttpRequestMessage(HttpMethod.Post, _requestUrl))
            {
                request.Content = content;

                using (var response = await ProviderHttp
                    .SendAsync(
                        _httpClient,
                        "Microsoft Graph",
                        request,
                        r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token),
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    await ProviderHttp.EnsureSuccessAsync(response, "Microsoft Graph").ConfigureAwait(false);
                }
            }
        }
    }
}
