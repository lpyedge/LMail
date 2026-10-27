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
    /// Sends messages with the Maileroo email API.
    /// </summary>
    /// <remarks>
    /// Maileroo has no content-ID field for attachments. Inline attachments are marked inline and
    /// must be referenced in HTML as <c>cid:&lt;file name&gt;</c>, so <c>ContentId</c> must equal the
    /// file name.
    /// </remarks>
    public sealed class MailerooEmailSender : IEmailSender
    {
        private const string RequestUrl = "https://smtp.maileroo.com/api/v2/emails";
        private const int MaxSubjectLength = 255;

        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        /// <summary>
        /// Creates a Maileroo sender.
        /// </summary>
        /// <param name="apiKey">Maileroo sending API key.</param>
        /// <param name="httpClient">Optional HTTP client.</param>
        public MailerooEmailSender(string apiKey, HttpClient? httpClient = null)
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

            if (message.Subject!.Length > MaxSubjectLength)
            {
                throw new EmailValidationException(
                    "Maileroo subjects must not exceed " + MaxSubjectLength + " characters.");
            }

            foreach (var attachment in message.Attachments)
            {
                if (attachment.ContentId != null &&
                    PayloadHelpers.NormalizeContentId(attachment.ContentId) != attachment.FileName)
                {
                    throw new EmailValidationException(
                        "Maileroo references inline attachments by file name. Set ContentId of '" +
                        attachment.FileName + "' to its file name.");
                }
            }

            var payload = ProviderPayloads.New();
            payload["from"] = ProviderPayloads.Address(message.From!, "address", "display_name");
            payload["to"] = ProviderPayloads.AddressList(message.To, "address", "display_name");

            ProviderPayloads.AddCopies(payload, message, a => ProviderPayloads.AddressList(a, "address", "display_name"));

            if (message.ReplyTo.Count > 0)
            {
                payload["reply_to"] = ProviderPayloads.AddressList(
                    message.ReplyTo,
                    "address",
                    "display_name");
            }

            payload["subject"] = message.Subject!;

            if (!string.IsNullOrEmpty(message.HtmlBody))
            {
                payload["html"] = message.HtmlBody;
            }

            if (!string.IsNullOrEmpty(message.TextBody))
            {
                payload["plain"] = message.TextBody;
            }

            ProviderPayloads.AddHeaders(payload, message);

            if (message.Attachments.Count > 0)
            {
                var attachments = new List<Dictionary<string, object?>>();

                foreach (var attachment in message.Attachments)
                {
                    var item = ProviderPayloads.New();
                    item["file_name"] = attachment.FileName;
                    item["content_type"] = attachment.ContentType;
                    item["content"] = Convert.ToBase64String(attachment.Content);

                    if (attachment.ContentId != null)
                    {
                        item["inline"] = true;
                    }

                    attachments.Add(item);
                }

                payload["attachments"] = attachments;
            }

            using (var response = await ProviderHttp
                .SendJsonAsync(
                    _httpClient,
                    "Maileroo",
                    RequestUrl,
                    payload,
                    r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                var body = await ProviderHttp.EnsureSuccessAsync(response, "Maileroo").ConfigureAwait(false);
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
                if (document.RootElement.TryGetProperty("success", out var success) &&
                    success.ValueKind == JsonValueKind.False)
                {
                    throw new EmailSendException(
                        "Maileroo rejected the message.",
                        "Maileroo",
                        200,
                        ProviderHttp.Truncate(body));
                }
            }
        }
    }
}
