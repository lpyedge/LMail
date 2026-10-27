using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MimeKit;

namespace LMail.Samples
{
    internal static class SelfTest
    {
        public static async Task<int> RunAsync()
        {
            var tests = new (string Name, Func<Task> Run)[]
            {
                ("validation", ValidationAsync),
                ("graph-mime", GraphMimeAsync),
                ("amazon-ses", AmazonSesAsync),
                ("cloudflare", CloudflareAsync),
                ("brevo", BrevoAsync),
                ("mailtrap", MailtrapAsync),
                ("resend", ResendAsync),
                ("maileroo", MailerooAsync),
                ("oauth2", OAuth2Async),
                ("http-transport", HttpTransportCheck.RunAsync),
                ("inline-without-html", InlineWithoutHtmlAsync),
                ("resend-quoted-from", ResendQuotedFromAsync),
                ("oauth2-access-only", AccessOnlyTokenAsync),
                ("oauth2-invalid-expiry", InvalidTokenExpiryAsync),
                ("mime-roundtrip", MimeCheck.RunAsync),
                ("smtp-loopback", SmtpSmokeCheck.RunAsync)
            };

            var failures = 0;

            foreach (var test in tests)
            {
                try
                {
                    await test.Run().ConfigureAwait(false);
                    Console.WriteLine("PASS " + test.Name);
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.WriteLine("FAIL " + test.Name + ": " + ex.Message);
                }
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "Self-test passed: " + tests.Length + " groups."
                : "Self-test failed: " + failures + " of " + tests.Length + " groups.");

            return failures == 0 ? 0 : 1;
        }

        private static async Task ValidationAsync()
        {
            var stub = new StubHttpHandler();
            var sender = new GraphEmailSender(
                new StaticAccessTokenProvider("token"),
                null,
                new HttpClient(stub));

            var missingFrom = NewMessage();
            missingFrom.From = null;
            await Expect<EmailValidationException>(() => sender.SendAsync(missingFrom), "missing From")
                .ConfigureAwait(false);

            var missingTo = NewMessage();
            missingTo.To.Clear();
            await Expect<EmailValidationException>(() => sender.SendAsync(missingTo), "missing To")
                .ConfigureAwait(false);

            var badAddress = NewMessage();
            badAddress.To.Clear();
            badAddress.To.Add(new EmailAddress("not-an-address"));
            await Expect<EmailValidationException>(() => sender.SendAsync(badAddress), "invalid address")
                .ConfigureAwait(false);

            var reservedHeader = NewMessage();
            reservedHeader.Headers["Subject"] = "duplicate";
            await Expect<EmailValidationException>(() => sender.SendAsync(reservedHeader), "reserved header")
                .ConfigureAwait(false);

            var badAttachment = NewMessage();
            badAttachment.Attachments.Add(new EmailAttachment("file.txt", new byte[] { 1 }, "not a mime type"));
            await Expect<EmailValidationException>(() => sender.SendAsync(badAttachment), "invalid content type")
                .ConfigureAwait(false);

            Assert(stub.Requests.Count == 0, "validation must not send HTTP requests");
        }

        private static async Task GraphMimeAsync()
        {
            var stub = new StubHttpHandler(_ => StubHttpHandler.JsonResponse(string.Empty, HttpStatusCode.Accepted));
            var sender = new GraphEmailSender(
                new StaticAccessTokenProvider("graph-token"),
                null,
                new HttpClient(stub));

            var message = NewMessage();
            message.Bcc.Add(new EmailAddress("bcc@example.com"));
            message.Headers["X-Test"] = "graph";
            message.Attachments.Add(new EmailAttachment(
                "report.txt",
                new byte[] { 1, 2, 3 },
                "text/plain"));
            message.Attachments.Add(new EmailAttachment(
                "logo.png",
                new byte[] { 4, 5, 6 },
                "image/png",
                "logo"));

            await sender.SendAsync(message).ConfigureAwait(false);

            var request = Single(stub);
            Assert(request.Uri.ToString() == "https://graph.microsoft.com/v1.0/me/sendMail", "Graph URL");
            Assert(request.Header("Authorization") == "Bearer graph-token", "Graph bearer token");
            Assert(
                (request.Header("Content-Type") ?? string.Empty).StartsWith("text/plain", StringComparison.Ordinal),
                "Graph MIME content type");

            var mime = LoadMime(request.Body);
            var rawMime = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(request.Body));
            Assert(!rawMime.Replace("\r\n", "").Contains('\n'), "MIME uses CRLF on every platform");
            Assert(mime.Bcc.Count == 1, "Graph MIME keeps Bcc");
            Assert(mime.HtmlBody != null && mime.TextBody != null, "Graph MIME has both bodies");
            Assert(mime.Headers["X-Test"] == "graph", "Graph MIME keeps custom header");

            var parts = mime.BodyParts.OfType<MimePart>().ToList();
            Assert(parts.Any(p => p.ContentId == "logo"), "Graph MIME inline content ID");
            Assert(parts.Any(p => p.FileName == "report.txt"), "Graph MIME attachment name");
            Assert(parts.Any(p => p.ContentType.MimeType == "image/png"), "Graph MIME inline type");

            var usersStub = new StubHttpHandler(_ => StubHttpHandler.JsonResponse(string.Empty, HttpStatusCode.Accepted));
            var userSender = new GraphEmailSender(
                new StaticAccessTokenProvider("token"),
                "user@example.com",
                new HttpClient(usersStub));

            await userSender.SendAsync(NewMessage()).ConfigureAwait(false);
            Assert(
                Single(usersStub).Uri.ToString() ==
                "https://graph.microsoft.com/v1.0/users/user%40example.com/sendMail",
                "Graph user URL");
        }

        private static async Task AmazonSesAsync()
        {
            var stub = new StubHttpHandler();
            var sender = new AmazonSesEmailSender(
                "us-east-1",
                "AKIAEXAMPLE",
                "secret",
                null,
                new HttpClient(stub));

            var message = NewMessage();
            message.Bcc.Add(new EmailAddress("bcc@example.com"));

            await sender.SendAsync(message).ConfigureAwait(false);

            var request = Single(stub);
            Assert(
                request.Uri.ToString() == "https://email.us-east-1.amazonaws.com/v2/email/outbound-emails",
                "SES URL");
            Assert(request.Header("Content-Type") == "application/json", "SES content type");
            Assert(request.Header("x-amz-date") != null, "SES x-amz-date");

            var authorization = request.Header("Authorization") ?? string.Empty;
            Assert(
                authorization.StartsWith(
                    "AWS4-HMAC-SHA256 Credential=AKIAEXAMPLE/",
                    StringComparison.Ordinal),
                "SES authorization header");
            Assert(
                authorization.Contains("SignedHeaders=content-type;host;x-amz-date", StringComparison.Ordinal),
                "SES signed headers");
            Assert(authorization.Contains("Signature=", StringComparison.Ordinal), "SES signature");

            using (var document = JsonDocument.Parse(request.Body))
            {
                var root = document.RootElement;
                Assert(
                    root.GetProperty("FromEmailAddress").GetString()!.Contains("sender@example.com"),
                    "SES FromEmailAddress");
                Assert(
                    root.GetProperty("Destination").GetProperty("BccAddresses")[0].GetString() == "bcc@example.com",
                    "SES Bcc destination");

                var raw = root.GetProperty("Content").GetProperty("Raw").GetProperty("Data").GetString()!;
                var mime = LoadMime(raw);
                Assert(mime.Bcc.Count == 0, "SES raw MIME excludes Bcc header");
                Assert(mime.To.Count == 1, "SES raw MIME keeps To");
            }
        }

        private static async Task CloudflareAsync()
        {
            var stub = new StubHttpHandler();
            var sender = new CloudflareEmailSender("account-id", "cf-token", new HttpClient(stub));

            var message = NewMessage();
            message.Attachments.Add(new EmailAttachment("file.txt", new byte[] { 1 }, "text/plain"));
            message.Attachments.Add(new EmailAttachment(
                "logo.png",
                new byte[] { 2 },
                "image/png",
                "<logo>"));

            await sender.SendAsync(message).ConfigureAwait(false);

            var request = Single(stub);
            Assert(
                request.Uri.ToString() ==
                "https://api.cloudflare.com/client/v4/accounts/account-id/email/sending/send",
                "Cloudflare URL");
            Assert(request.Header("Authorization") == "Bearer cf-token", "Cloudflare auth");

            using (var document = JsonDocument.Parse(request.Body))
            {
                var root = document.RootElement;
                Assert(root.GetProperty("from").GetProperty("address").GetString() == "sender@example.com", "Cloudflare from");
                Assert(root.GetProperty("to")[0].GetProperty("address").GetString() == "to@example.com", "Cloudflare to");
                Assert(root.GetProperty("reply_to").GetProperty("address").GetString() == "reply@example.com", "Cloudflare reply_to");
                Assert(root.GetProperty("text").GetString() == "plain body", "Cloudflare text");
                Assert(root.GetProperty("html").GetString() == "<p>html body</p>", "Cloudflare html");
                Assert(root.GetProperty("headers").GetProperty("X-Test").GetString() == "value", "Cloudflare headers");

                var attachments = root.GetProperty("attachments");
                Assert(attachments[0].GetProperty("disposition").GetString() == "attachment", "Cloudflare attachment disposition");
                Assert(attachments[1].GetProperty("disposition").GetString() == "inline", "Cloudflare inline disposition");
                Assert(attachments[1].GetProperty("content_id").GetString() == "logo", "Cloudflare content ID");
            }

            var multiReply = NewMessage();
            multiReply.ReplyTo.Add(new EmailAddress("second@example.com"));
            await Expect<EmailValidationException>(
                () => sender.SendAsync(multiReply),
                "Cloudflare multiple reply-to")
                .ConfigureAwait(false);

            var failingStub = new StubHttpHandler(
                _ => StubHttpHandler.JsonResponse(
                    "{\"success\":false,\"errors\":[{\"code\":1000,\"message\":\"bad\"}]}"));
            var failing = new CloudflareEmailSender("account-id", "cf-token", new HttpClient(failingStub));
            await Expect<EmailSendException>(() => failing.SendAsync(NewMessage()), "Cloudflare error body")
                .ConfigureAwait(false);
        }

        private static async Task BrevoAsync()
        {
            var stub = new StubHttpHandler();
            var sender = new BrevoEmailSender("brevo-key", new HttpClient(stub));

            var message = NewMessage();
            message.HtmlBody = null;
            message.Attachments.Add(new EmailAttachment("file.txt", new byte[] { 1 }, "text/plain"));

            await sender.SendAsync(message).ConfigureAwait(false);

            var request = Single(stub);
            Assert(request.Uri.ToString() == "https://api.brevo.com/v3/smtp/email", "Brevo URL");
            Assert(request.Header("api-key") == "brevo-key", "Brevo api-key");

            using (var document = JsonDocument.Parse(request.Body))
            {
                var root = document.RootElement;
                Assert(root.GetProperty("sender").GetProperty("email").GetString() == "sender@example.com", "Brevo sender");
                Assert(root.GetProperty("replyTo").GetProperty("email").GetString() == "reply@example.com", "Brevo replyTo");
                Assert(root.GetProperty("textContent").GetString() == "plain body", "Brevo text content");
                Assert(
                    root.GetProperty("htmlContent").GetString()!.Contains("<p>plain body", StringComparison.Ordinal),
                    "Brevo generated HTML");
                Assert(root.GetProperty("attachment")[0].GetProperty("name").GetString() == "file.txt", "Brevo attachment");
            }

            var inline = NewMessage();
            inline.Attachments.Add(new EmailAttachment("logo.png", new byte[] { 1 }, "image/png", "logo"));
            await Expect<EmailValidationException>(() => sender.SendAsync(inline), "Brevo inline attachment")
                .ConfigureAwait(false);

            var multiReply = NewMessage();
            multiReply.ReplyTo.Add(new EmailAddress("second@example.com"));
            await Expect<EmailValidationException>(() => sender.SendAsync(multiReply), "Brevo multiple reply-to")
                .ConfigureAwait(false);
        }

        private static async Task MailtrapAsync()
        {
            var stub = new StubHttpHandler();
            var sender = new MailtrapEmailSender("mt-token", true, "inbox-1", new HttpClient(stub));

            await sender.SendAsync(NewMessage()).ConfigureAwait(false);

            var request = Single(stub);
            Assert(
                request.Uri.ToString() == "https://sandbox.api.mailtrap.io/api/send/inbox-1",
                "Mailtrap sandbox URL");
            Assert(request.Header("Api-Token") == "mt-token", "Mailtrap API token");

            using (var document = JsonDocument.Parse(request.Body))
            {
                var root = document.RootElement;
                Assert(root.GetProperty("from").GetProperty("email").GetString() == "sender@example.com", "Mailtrap from");
                Assert(root.GetProperty("reply_to").GetProperty("email").GetString() == "reply@example.com", "Mailtrap reply_to");
            }

            var multiReply = NewMessage();
            multiReply.ReplyTo.Add(new EmailAddress("second@example.com"));
            await Expect<EmailValidationException>(() => sender.SendAsync(multiReply), "Mailtrap multiple reply-to")
                .ConfigureAwait(false);

            ExpectArgument<ArgumentException>(
                () => new MailtrapEmailSender("token", true, null),
                "Mailtrap sandbox without inbox");
        }

        private static async Task ResendAsync()
        {
            var stub = new StubHttpHandler();
            var sender = new ResendEmailSender("resend-key", new HttpClient(stub));

            var message = NewMessage();
            message.From = new EmailAddress("sender@example.com", "Sender Name");
            message.To.Clear();
            message.To.Add(new EmailAddress("to@example.com", "Recipient Name"));
            message.ReplyTo.Add(new EmailAddress("second@example.com"));
            message.Attachments.Add(new EmailAttachment("logo.png", new byte[] { 1 }, "image/png", "logo"));

            await sender.SendAsync(message).ConfigureAwait(false);

            var request = Single(stub);
            Assert(request.Uri.ToString() == "https://api.resend.com/emails", "Resend URL");
            Assert(request.Header("Authorization") == "Bearer resend-key", "Resend auth");

            using (var document = JsonDocument.Parse(request.Body))
            {
                var root = document.RootElement;
                var from = MailboxAddress.Parse(root.GetProperty("from").GetString()!);
                Assert(from.Name == "Sender Name" && from.Address == "sender@example.com", "Resend from");
                var to = MailboxAddress.Parse(root.GetProperty("to")[0].GetString()!);
                Assert(to.Name == "Recipient Name" && to.Address == "to@example.com", "Resend recipient display name");
                Assert(root.GetProperty("reply_to").GetArrayLength() == 2, "Resend multiple reply-to");
                Assert(root.GetProperty("attachments")[0].GetProperty("content_id").GetString() == "logo", "Resend content ID");
                Assert(root.GetProperty("attachments")[0].GetProperty("content_type").GetString() == "image/png", "Resend content type");
            }
        }

        private static async Task MailerooAsync()
        {
            var stub = new StubHttpHandler();
            var sender = new MailerooEmailSender("maileroo-key", new HttpClient(stub));

            var message = NewMessage();
            message.From = new EmailAddress("sender@example.com", "Sender Name");
            message.ReplyTo.Add(new EmailAddress("second@example.com"));
            message.Attachments.Add(new EmailAttachment("logo.png", new byte[] { 1 }, "image/png", "logo.png"));

            await sender.SendAsync(message).ConfigureAwait(false);

            var request = Single(stub);
            Assert(request.Uri.ToString() == "https://smtp.maileroo.com/api/v2/emails", "Maileroo URL");
            Assert(request.Header("Authorization") == "Bearer maileroo-key", "Maileroo auth");

            using (var document = JsonDocument.Parse(request.Body))
            {
                var root = document.RootElement;
                Assert(root.GetProperty("from").GetProperty("display_name").GetString() == "Sender Name", "Maileroo from name");
                Assert(root.GetProperty("reply_to").GetArrayLength() == 2, "Maileroo reply_to");
                Assert(root.GetProperty("plain").GetString() == "plain body", "Maileroo plain");
                Assert(root.GetProperty("attachments")[0].GetProperty("inline").GetBoolean(), "Maileroo inline flag");
                Assert(root.GetProperty("headers").GetProperty("X-Test").GetString() == "value", "Maileroo headers");
            }

            var failingStub = new StubHttpHandler(
                _ => StubHttpHandler.JsonResponse("{\"success\":false,\"message\":\"bad\"}"));
            var failing = new MailerooEmailSender("key", new HttpClient(failingStub));
            await Expect<EmailSendException>(() => failing.SendAsync(NewMessage()), "Maileroo error body")
                .ConfigureAwait(false);

            var longSubject = NewMessage();
            longSubject.Subject = new string('a', 256);
            await Expect<EmailValidationException>(() => sender.SendAsync(longSubject), "Maileroo subject limit")
                .ConfigureAwait(false);

            var mismatchedCid = NewMessage();
            mismatchedCid.Attachments.Add(new EmailAttachment("logo.png", new byte[] { 1 }, "image/png", "logo"));
            await Expect<EmailValidationException>(() => sender.SendAsync(mismatchedCid), "Maileroo content ID must equal file name")
                .ConfigureAwait(false);
        }

        private static async Task OAuth2Async()
        {
            var google = GoogleOAuth2.CreateOptions("client-id", "client-secret", "https://localhost/callback");
            var authorization = OAuth2AuthorizationRequest.Create(google, "state-123");
            Assert(authorization.AuthorizationUrl.Contains("code_challenge_method=S256", StringComparison.Ordinal), "PKCE method");
            Assert(authorization.AuthorizationUrl.Contains("state=state-123", StringComparison.Ordinal), "OAuth state");
            Assert(authorization.AuthorizationUrl.Contains("prompt=consent", StringComparison.Ordinal), "Google consent");
            Assert(authorization.CodeVerifier.Length == 43, "PKCE verifier length");

            var stub = new StubHttpHandler(
                _ => StubHttpHandler.JsonResponse(TokenJson("access-1", "refresh-1", 3600)));
            var client = new OAuth2TokenClient(google, new HttpClient(stub));
            var provider = new OAuth2TokenProvider(client, OAuth2Token.FromRefreshToken("refresh-0"));

            Assert(await provider.GetAccessTokenAsync().ConfigureAwait(false) == "access-1", "first refresh");
            Assert(await provider.GetAccessTokenAsync().ConfigureAwait(false) == "access-1", "cached access token");
            Assert(stub.Requests.Count == 1, "token cache uses one request");
            Assert(
                stub.Requests[0].Body.Contains("grant_type=refresh_token", StringComparison.Ordinal) &&
                stub.Requests[0].Body.Contains("refresh_token=refresh-0", StringComparison.Ordinal) &&
                stub.Requests[0].Body.Contains("client_secret=client-secret", StringComparison.Ordinal),
                "refresh form");

            var exchangeStub = new StubHttpHandler(
                _ => StubHttpHandler.JsonResponse(TokenJson("code-access", "code-refresh", 3600)));
            var exchangeClient = new OAuth2TokenClient(google, new HttpClient(exchangeStub));
            var exchanged = await exchangeClient
                .ExchangeAuthorizationCodeAsync("code-1", authorization.CodeVerifier)
                .ConfigureAwait(false);

            Assert(exchanged.AccessToken == "code-access", "authorization code token");
            Assert(
                exchangeStub.Requests[0].Body.Contains("grant_type=authorization_code", StringComparison.Ordinal) &&
                exchangeStub.Requests[0].Body.Contains("code=code-1", StringComparison.Ordinal) &&
                exchangeStub.Requests[0].Body.Contains(
                    "code_verifier=" + authorization.CodeVerifier,
                    StringComparison.Ordinal) &&
                exchangeStub.Requests[0].Body.Contains("redirect_uri=https%3A%2F%2Flocalhost%2Fcallback", StringComparison.Ordinal),
                "authorization code form");

            var rotationStub = new StubHttpHandler(
                _ => StubHttpHandler.JsonResponse(TokenJson("access-2", "refresh-2", 3600)));
            var rotationClient = new OAuth2TokenClient(google, new HttpClient(rotationStub));
            var rotationProvider = new OAuth2TokenProvider(
                rotationClient,
                new OAuth2Token("expired", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(-1)));

            string? rotated = null;
            rotationProvider.RefreshTokenUpdated += (_, e) => rotated = e.NewRefreshToken;

            Assert(await rotationProvider.GetAccessTokenAsync().ConfigureAwait(false) == "access-2", "rotated access token");
            Assert(rotated == "refresh-2", "refresh token rotation event");
            Assert(rotationProvider.CurrentToken!.RefreshToken == "refresh-2", "rotated token cached");

            var credentialsOptions = MicrosoftOAuth2.CreateGraphClientCredentialsOptions(
                "client-id",
                "client-secret",
                "tenant-id");
            var credentialsStub = new StubHttpHandler(
                _ => StubHttpHandler.JsonResponse(TokenJson("app-access", "ignored", 3600)));
            var credentialsClient = new OAuth2TokenClient(credentialsOptions, new HttpClient(credentialsStub));
            var credentialsProvider = OAuth2TokenProvider.CreateClientCredentials(credentialsClient);

            Assert(
                await credentialsProvider.GetAccessTokenAsync().ConfigureAwait(false) == "app-access",
                "client credentials token");
            Assert(
                await credentialsProvider.GetAccessTokenAsync().ConfigureAwait(false) == "app-access",
                "client credentials cached");
            Assert(credentialsStub.Requests.Count == 1, "client credentials cache");
            Assert(
                credentialsStub.Requests[0].Body.Contains("grant_type=client_credentials", StringComparison.Ordinal),
                "client credentials form");
        }

        private static async Task InlineWithoutHtmlAsync()
        {
            var stub = new StubHttpHandler();
            var sender = new GraphEmailSender(new StaticAccessTokenProvider("token"), null, new HttpClient(stub));
            var message = NewMessage();
            message.HtmlBody = null;
            message.Attachments.Add(new EmailAttachment("logo.png", new byte[] { 1, 2, 3 }, "image/png", "logo"));
            await sender.SendAsync(message).ConfigureAwait(false);
            using var mime = LoadMime(Single(stub).Body);
            Assert(mime.BodyParts.OfType<MimePart>().Any(p => p.FileName == "logo.png"),
                "text-only MIME must retain inline attachment bytes");
        }

        private static async Task ResendQuotedFromAsync()
        {
            var stub = new StubHttpHandler();
            var sender = new ResendEmailSender("key", new HttpClient(stub));
            var message = NewMessage();
            message.From = new EmailAddress("sender@example.com", "Doe, Jane \"Support\"");
            await sender.SendAsync(message).ConfigureAwait(false);
            using var document = JsonDocument.Parse(Single(stub).Body);
            var from = document.RootElement.GetProperty("from").GetString()!;
            Assert(MailboxAddress.TryParse(from, out var mailbox), "Resend from must be a valid single mailbox");
            Assert(mailbox!.Name == message.From.DisplayName && mailbox.Address == message.From.Address,
                "Resend from must preserve the quoted display name");
        }

        private static async Task AccessOnlyTokenAsync()
        {
            var stub = new StubHttpHandler();
            var client = new OAuth2TokenClient(GoogleOAuth2.CreateOptions("client", null), new HttpClient(stub));
            using var provider = new OAuth2TokenProvider(client,
                new OAuth2Token("still-valid", expiresAt: DateTimeOffset.UtcNow.AddMinutes(2)));
            Assert(await provider.GetAccessTokenAsync().ConfigureAwait(false) == "still-valid",
                "an access-only token is usable until actual expiry");
            Assert(stub.Requests.Count == 0, "access-only token must not attempt refresh");
        }

        private static async Task InvalidTokenExpiryAsync()
        {
            foreach (var expiresIn in new[] { "\"NaN\"", "1e100", "\"invalid\"", "null", "-1" })
            {
                var stub = new StubHttpHandler(_ => StubHttpHandler.JsonResponse(
                    "{\"access_token\":\"token\",\"expires_in\":" + expiresIn + "}"));
                var client = new OAuth2TokenClient(GoogleOAuth2.CreateOptions("client", null), new HttpClient(stub));
                await Expect<OAuth2Exception>(() => client.RequestClientCredentialsAsync(),
                    "invalid expires_in " + expiresIn).ConfigureAwait(false);
            }

            var nonJson = new StubHttpHandler(_ => StubHttpHandler.JsonResponse("Bad Gateway", HttpStatusCode.BadGateway));
            var failing = new OAuth2TokenClient(GoogleOAuth2.CreateOptions("client", null), new HttpClient(nonJson));
            try
            {
                await failing.RequestClientCredentialsAsync().ConfigureAwait(false);
                throw new InvalidOperationException("non-JSON token response must fail");
            }
            catch (OAuth2Exception ex)
            {
                Assert(ex.StatusCode == 502, "non-JSON OAuth2 errors must retain HTTP status");
            }
        }

        private static EmailMessage NewMessage()
        {
            var message = new EmailMessage
            {
                From = new EmailAddress("sender@example.com", "Sender"),
                Subject = "Subject",
                TextBody = "plain body",
                HtmlBody = "<p>html body</p>"
            };

            message.To.Add(new EmailAddress("to@example.com"));
            message.ReplyTo.Add(new EmailAddress("reply@example.com"));
            message.Headers["X-Test"] = "value";

            return message;
        }

        private static MimeMessage LoadMime(string base64)
        {
            using (var stream = new MemoryStream(Convert.FromBase64String(base64)))
            {
                return MimeMessage.Load(stream);
            }
        }

        private static string TokenJson(string accessToken, string refreshToken, int expiresIn)
        {
            return "{\"access_token\":\"" + accessToken +
                   "\",\"refresh_token\":\"" + refreshToken +
                   "\",\"expires_in\":" + expiresIn +
                   ",\"token_type\":\"Bearer\"}";
        }

        private static CapturedRequest Single(StubHttpHandler stub)
        {
            Assert(stub.Requests.Count == 1, "expected exactly one HTTP request, got " + stub.Requests.Count);
            return stub.Requests[0];
        }

        internal static async Task Expect<T>(Func<Task> action, string description)
            where T : Exception
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (T)
            {
                return;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    description + " threw " + ex.GetType().Name + " instead of " + typeof(T).Name,
                    ex);
            }

            throw new InvalidOperationException(description + " did not throw " + typeof(T).Name);
        }

        private static void ExpectArgument<T>(Action action, string description)
            where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    description + " threw " + ex.GetType().Name + " instead of " + typeof(T).Name,
                    ex);
            }

            throw new InvalidOperationException(description + " did not throw " + typeof(T).Name);
        }

        internal static void Assert(bool condition, string description)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + description);
            }
        }
    }
}
