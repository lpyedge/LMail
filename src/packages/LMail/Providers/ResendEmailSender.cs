using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using LMail.Internal;

namespace LMail
{
    /// <summary>
    /// Sends messages with the Resend email API.
    /// </summary>
    public sealed class ResendEmailSender : IEmailSender
    {
        private const string RequestUrl = "https://api.resend.com/emails";

        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        /// <summary>
        /// Creates a Resend sender.
        /// </summary>
        /// <param name="apiKey">Resend API key.</param>
        /// <param name="httpClient">Optional HTTP client.</param>
        public ResendEmailSender(string apiKey, HttpClient? httpClient = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("API key must not be empty.", nameof(apiKey));
            }

            _apiKey = apiKey;
            _httpClient = ProviderHttp.Resolve(httpClient);
        }

        /// <inheritdoc />
        public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            EmailValidator.Validate(message);

            var payload = ProviderPayloads.New();
            payload["from"] = AddressText.Mailbox(message.From!);
            payload["to"] = ProviderPayloads.MailboxStrings(message.To);

            ProviderPayloads.AddCopies(payload, message, ProviderPayloads.MailboxStrings);

            if (message.ReplyTo.Count > 0)
            {
                payload["reply_to"] = ProviderPayloads.MailboxStrings(message.ReplyTo);
            }

            payload["subject"] = message.Subject!;

            if (!string.IsNullOrEmpty(message.HtmlBody))
            {
                payload["html"] = message.HtmlBody;
            }

            if (!string.IsNullOrEmpty(message.TextBody))
            {
                payload["text"] = message.TextBody;
            }

            ProviderPayloads.AddHeaders(payload, message);

            if (message.Attachments.Count > 0)
            {
                var attachments = new List<Dictionary<string, object?>>();

                foreach (var attachment in message.Attachments)
                {
                    var item = ProviderPayloads.New();
                    item["content"] = Convert.ToBase64String(attachment.Content);
                    item["filename"] = attachment.FileName;
                    item["content_type"] = attachment.ContentType;

                    if (attachment.ContentId != null)
                    {
                        item["content_id"] = PayloadHelpers.NormalizeContentId(attachment.ContentId);
                    }

                    attachments.Add(item);
                }

                payload["attachments"] = attachments;
            }

            using (var response = await ProviderHttp
                .SendJsonAsync(
                    _httpClient,
                    "Resend",
                    RequestUrl,
                    payload,
                    r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                await ProviderHttp.EnsureSuccessAsync(response, "Resend").ConfigureAwait(false);
            }
        }
    }
}
