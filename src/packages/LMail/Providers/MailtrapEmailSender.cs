using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LMail.Internal;

namespace LMail
{
    /// <summary>
    /// Sends messages with the Mailtrap transactional API, either live or to a sandbox inbox.
    /// </summary>
    public sealed class MailtrapEmailSender : IEmailSender
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiToken;
        private readonly string _requestUrl;

        /// <summary>
        /// Creates a Mailtrap sender.
        /// </summary>
        /// <param name="apiToken">Mailtrap API token.</param>
        /// <param name="sandbox">Send to a sandbox inbox instead of real recipients.</param>
        /// <param name="inboxId">Sandbox inbox ID. Required when <paramref name="sandbox"/> is <c>true</c>.</param>
        /// <param name="httpClient">Optional HTTP client.</param>
        public MailtrapEmailSender(
            string apiToken,
            bool sandbox = false,
            string? inboxId = null,
            HttpClient? httpClient = null)
        {
            if (string.IsNullOrWhiteSpace(apiToken))
            {
                throw new ArgumentException("API token must not be empty.", nameof(apiToken));
            }

            if (sandbox && string.IsNullOrWhiteSpace(inboxId))
            {
                throw new ArgumentException("Inbox ID is required for the Mailtrap sandbox.", nameof(inboxId));
            }

            _apiToken = apiToken;
            _httpClient = ProviderHttp.Resolve(httpClient);
            _requestUrl = sandbox
                ? "https://sandbox.api.mailtrap.io/api/send/" + Uri.EscapeDataString(inboxId!)
                : "https://send.api.mailtrap.io/api/send";
        }

        /// <inheritdoc />
        public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            EmailValidator.Validate(message);

            var replyTo = PayloadHelpers.SingleReplyTo(message, "Mailtrap");

            var payload = ProviderPayloads.New();
            payload["from"] = ProviderPayloads.Address(message.From!);
            payload["to"] = ProviderPayloads.AddressList(message.To);

            ProviderPayloads.AddCopies(payload, message, a => ProviderPayloads.AddressList(a));

            if (replyTo != null)
            {
                payload["reply_to"] = ProviderPayloads.Address(replyTo);
            }

            payload["subject"] = message.Subject!;

            if (!string.IsNullOrEmpty(message.TextBody))
            {
                payload["text"] = message.TextBody;
            }

            if (!string.IsNullOrEmpty(message.HtmlBody))
            {
                payload["html"] = message.HtmlBody;
            }

            ProviderPayloads.AddHeaders(payload, message);

            if (message.Attachments.Count > 0)
            {
                payload["attachments"] = ProviderPayloads.Attachments(message);
            }

            using (var response = await ProviderHttp
                .SendJsonAsync(
                    _httpClient,
                    "Mailtrap",
                    _requestUrl,
                    payload,
                    r => r.Headers.TryAddWithoutValidation("Api-Token", _apiToken),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                await ProviderHttp.EnsureSuccessAsync(response, "Mailtrap").ConfigureAwait(false);
            }
        }
    }
}
