using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LMail.Internal;

namespace LMail
{
    /// <summary>
    /// Sends messages with the Brevo transactional email API.
    /// </summary>
    public sealed class BrevoEmailSender : IEmailSender
    {
        private const string RequestUrl = "https://api.brevo.com/v3/smtp/email";

        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        /// <summary>
        /// Creates a Brevo sender.
        /// </summary>
        /// <param name="apiKey">Brevo API key.</param>
        /// <param name="httpClient">Optional HTTP client.</param>
        public BrevoEmailSender(string apiKey, HttpClient? httpClient = null)
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

            foreach (var attachment in message.Attachments)
            {
                if (attachment.ContentId != null)
                {
                    throw new EmailValidationException(
                        "Brevo does not support inline attachments. Remove ContentId from '" +
                        attachment.FileName + "'.");
                }
            }

            var replyTo = PayloadHelpers.SingleReplyTo(message, "Brevo");

            var payload = ProviderPayloads.New();
            payload["sender"] = ProviderPayloads.Address(message.From!);
            payload["to"] = ProviderPayloads.AddressList(message.To);

            ProviderPayloads.AddCopies(payload, message, a => ProviderPayloads.AddressList(a));

            if (replyTo != null)
            {
                payload["replyTo"] = ProviderPayloads.Address(replyTo);
            }

            payload["subject"] = message.Subject!;

            payload["htmlContent"] = !string.IsNullOrEmpty(message.HtmlBody)
                ? message.HtmlBody!
                : PayloadHelpers.PlainTextToHtml(message.TextBody!);

            if (!string.IsNullOrEmpty(message.TextBody))
            {
                payload["textContent"] = message.TextBody;
            }

            ProviderPayloads.AddHeaders(payload, message);

            if (message.Attachments.Count > 0)
            {
                var attachments = new List<Dictionary<string, object?>>();

                foreach (var attachment in message.Attachments)
                {
                    var item = ProviderPayloads.New();
                    item["content"] = Convert.ToBase64String(attachment.Content);
                    item["name"] = attachment.FileName;
                    attachments.Add(item);
                }

                payload["attachment"] = attachments;
            }

            using (var response = await ProviderHttp
                .SendJsonAsync(
                    _httpClient,
                    "Brevo",
                    RequestUrl,
                    payload,
                    r => r.Headers.TryAddWithoutValidation("api-key", _apiKey),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                await ProviderHttp.EnsureSuccessAsync(response, "Brevo").ConfigureAwait(false);
            }
        }
    }
}
