using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LMail.Internal;

namespace LMail
{
    /// <summary>
    /// Sends messages with the Cloudflare Email Sending REST API.
    /// </summary>
    public sealed class CloudflareEmailSender : IEmailSender
    {
        private const string BaseUrl = "https://api.cloudflare.com/client/v4/accounts/";

        private readonly HttpClient _httpClient;
        private readonly string _apiToken;
        private readonly string _requestUrl;

        /// <summary>
        /// Creates a Cloudflare sender.
        /// </summary>
        /// <param name="accountId">Cloudflare account ID.</param>
        /// <param name="apiToken">API token with email sending permission.</param>
        /// <param name="httpClient">Optional HTTP client.</param>
        public CloudflareEmailSender(string accountId, string apiToken, HttpClient? httpClient = null)
        {
            if (string.IsNullOrWhiteSpace(accountId))
            {
                throw new ArgumentException("Account ID must not be empty.", nameof(accountId));
            }

            if (string.IsNullOrWhiteSpace(apiToken))
            {
                throw new ArgumentException("API token must not be empty.", nameof(apiToken));
            }

            _apiToken = apiToken;
            _httpClient = ProviderHttp.Resolve(httpClient);
            _requestUrl = BaseUrl + Uri.EscapeDataString(accountId) + "/email/sending/send";
        }

        /// <inheritdoc />
        public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            EmailValidator.Validate(message);

            var replyTo = PayloadHelpers.SingleReplyTo(message, "Cloudflare");

            var payload = ProviderPayloads.New();
            payload["from"] = ProviderPayloads.Address(message.From!, "address", "name");
            payload["to"] = ProviderPayloads.AddressList(message.To, "address", "name");

            ProviderPayloads.AddCopies(payload, message, a => ProviderPayloads.AddressList(a, "address", "name"));

            payload["subject"] = message.Subject!;

            if (!string.IsNullOrEmpty(message.TextBody))
            {
                payload["text"] = message.TextBody;
            }

            if (!string.IsNullOrEmpty(message.HtmlBody))
            {
                payload["html"] = message.HtmlBody;
            }

            if (replyTo != null)
            {
                payload["reply_to"] = ProviderPayloads.Address(replyTo, "address", "name");
            }

            ProviderPayloads.AddHeaders(payload, message);

            if (message.Attachments.Count > 0)
            {
                payload["attachments"] = ProviderPayloads.Attachments(message);
            }

            using (var response = await ProviderHttp
                .SendJsonAsync(
                    _httpClient,
                    "Cloudflare",
                    _requestUrl,
                    payload,
                    r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                var body = await ProviderHttp.EnsureSuccessAsync(response, "Cloudflare").ConfigureAwait(false);
                EnsureNoErrors(body);
            }
        }

        private static void EnsureNoErrors(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                var failed = root.TryGetProperty("success", out var success) &&
                             success.ValueKind == JsonValueKind.False;

                if (!failed &&
                    root.TryGetProperty("errors", out var errors) &&
                    errors.ValueKind == JsonValueKind.Array &&
                    errors.GetArrayLength() > 0)
                {
                    failed = true;
                }

                if (failed)
                {
                    throw new EmailSendException(
                        "Cloudflare rejected the message.",
                        "Cloudflare",
                        200,
                        ProviderHttp.Truncate(body));
                }
            }
        }
    }
}
