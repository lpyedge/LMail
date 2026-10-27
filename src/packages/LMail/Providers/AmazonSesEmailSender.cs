using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LMail.Internal;

namespace LMail
{
    /// <summary>
    /// Sends raw MIME messages with the Amazon SES v2 API and AWS Signature Version 4.
    /// </summary>
    public sealed class AmazonSesEmailSender : IEmailSender
    {
        private readonly HttpClient _httpClient;
        private readonly string _accessKeyId;
        private readonly string _secretAccessKey;
        private readonly string? _sessionToken;
        private readonly string _region;
        private readonly string _requestUrl;

        /// <summary>
        /// Creates an Amazon SES sender.
        /// </summary>
        /// <param name="region">SES region, for example <c>us-east-1</c>.</param>
        /// <param name="accessKeyId">IAM access key ID with SES sending permission.</param>
        /// <param name="secretAccessKey">IAM secret access key.</param>
        /// <param name="sessionToken">Optional temporary-credential session token.</param>
        /// <param name="httpClient">Optional HTTP client.</param>
        public AmazonSesEmailSender(
            string region,
            string accessKeyId,
            string secretAccessKey,
            string? sessionToken = null,
            HttpClient? httpClient = null)
        {
            if (string.IsNullOrWhiteSpace(region))
            {
                throw new ArgumentException("Region must not be empty.", nameof(region));
            }

            if (string.IsNullOrWhiteSpace(accessKeyId))
            {
                throw new ArgumentException("Access key ID must not be empty.", nameof(accessKeyId));
            }

            if (string.IsNullOrWhiteSpace(secretAccessKey))
            {
                throw new ArgumentException("Secret access key must not be empty.", nameof(secretAccessKey));
            }

            _region = region;
            _accessKeyId = accessKeyId;
            _secretAccessKey = secretAccessKey;
            _sessionToken = sessionToken;
            _httpClient = ProviderHttp.Resolve(httpClient);
            _requestUrl = "https://email." + region + ".amazonaws.com/v2/email/outbound-emails";
        }

        /// <inheritdoc />
        public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            var mime = MimeMessageFactory.Create(message, includeBcc: false);

            var destination = ProviderPayloads.New();
            destination["ToAddresses"] = ProviderPayloads.AddressStrings(message.To);

            if (message.Cc.Count > 0)
            {
                destination["CcAddresses"] = ProviderPayloads.AddressStrings(message.Cc);
            }

            if (message.Bcc.Count > 0)
            {
                destination["BccAddresses"] = ProviderPayloads.AddressStrings(message.Bcc);
            }

            var raw = ProviderPayloads.New();
            raw["Data"] = Convert.ToBase64String(mime);

            var content = ProviderPayloads.New();
            content["Raw"] = raw;

            var payload = ProviderPayloads.New();
            payload["FromEmailAddress"] = AddressText.Mailbox(message.From!);
            payload["Destination"] = destination;
            payload["Content"] = content;

            var json = JsonSerializer.SerializeToUtf8Bytes(payload, ProviderHttp.JsonOptions);

            using (var request = new HttpRequestMessage(HttpMethod.Post, _requestUrl))
            {
                var body = new ByteArrayContent(json);
                body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Content = body;

                AwsSigV4Signer.Sign(
                    request,
                    json,
                    _accessKeyId,
                    _secretAccessKey,
                    _sessionToken,
                    _region,
                    "ses",
                    DateTimeOffset.UtcNow);

                using (var response = await ProviderHttp
                    .SendAsync(_httpClient, "Amazon SES", request, null, cancellationToken)
                    .ConfigureAwait(false))
                {
                    await ProviderHttp.EnsureSuccessAsync(response, "Amazon SES").ConfigureAwait(false);
                }
            }
        }
    }
}
