# LMail

One small email-sending API for SMTP and hosted providers. Targets `netstandard2.1` with no
mail-library dependency: MIME and SMTP are implemented in the package, and REST providers use
plain `HttpClient`. The only package dependency is `System.Text.Json`.

Every sender implements `IEmailSender.SendAsync(EmailMessage, CancellationToken)`. Select a
provider when constructing the sender; message creation and sending stay the same. REST senders
accept an optional `HttpClient`, and SMTP/Graph share `IAccessTokenProvider` for OAuth2.

## Install

Release and manual preview publishing: [NuGet publishing guide](docs/PUBLISH.md).

```bash
dotnet add package LMail
```

## Quick start

```csharp
using System.IO;
using LMail;

var message = new EmailMessage
{
    From = new EmailAddress("sender@example.com", "Example Sender"),
    Subject = "Hello",
    TextBody = "Plain text version.",
    HtmlBody = "<p>HTML version.</p>"
};

message.To.Add(new EmailAddress("recipient@example.com", "Recipient"));
message.Cc.Add(new EmailAddress("cc@example.com"));
message.Bcc.Add(new EmailAddress("bcc@example.com"));
message.ReplyTo.Add(new EmailAddress("support@example.com"));

message.Headers["X-Campaign"] = "welcome";

message.Attachments.Add(new EmailAttachment(
    "report.pdf",
    File.ReadAllBytes("report.pdf"),
    "application/pdf"));
message.Attachments.Add(new EmailAttachment(
    "logo.png",
    File.ReadAllBytes("logo.png"),
    "image/png",
    contentId: "logo"));

IEmailSender sender = new SmtpEmailSender(new SmtpOptions
{
    Host = "smtp.example.com",
    Port = 587,
    Security = SmtpSecurity.StartTls,
    UserName = "smtp-user",
    Password = "smtp-password"
});

await sender.SendAsync(message);
```

Validation also bounds header-related values so the MIME output stays within the 998-character
line limit: subject and custom header values up to 900 characters, display names up to 255,
addresses up to 254, and attachment file names whose encoded form fits in 900. Addresses need an
ASCII local part (internationalized domains are converted to punycode); text containing control
characters is rejected. SMTP never sends credentials over an unencrypted connection unless
`SmtpSecurity.None` is set explicitly.

`To` must contain at least one recipient, even when only `Cc` or `Bcc` is used; this keeps the
same message valid across every supported provider.

## Senders

| Provider | SMTP | REST / API |
| --- | --- | --- |
| Generic SMTP | `new SmtpEmailSender(SmtpOptions)` | - |
| Gmail app password | `SmtpEmailSender.Gmail(address, appPassword)` | - |
| Gmail OAuth2 | `SmtpEmailSender.Gmail(address, tokenProvider)` | - |
| Microsoft 365 OAuth2 | `SmtpEmailSender.Microsoft365(address, tokenProvider)` | `new GraphEmailSender(tokenProvider, userId)` |
| Cloudflare Email Sending | `SmtpEmailSender.Cloudflare(apiToken)` | `new CloudflareEmailSender(accountId, apiToken)` |
| Brevo | `SmtpEmailSender.Brevo(login, smtpKey)` | `new BrevoEmailSender(apiKey)` |
| Mailtrap | `SmtpEmailSender.Mailtrap(user, password, sandbox)` | `new MailtrapEmailSender(token, sandbox, inboxId)` |
| Maileroo | `SmtpEmailSender.Maileroo(user, password)` | `new MailerooEmailSender(apiKey)` |
| Amazon SES | `SmtpEmailSender.AmazonSes(region, user, password)` | `new AmazonSesEmailSender(region, accessKeyId, secretAccessKey, sessionToken)` |
| Resend | - | `new ResendEmailSender(apiKey)` |

REST senders accept an optional `HttpClient` as the last constructor argument for proxies,
custom handlers, or tests.

### Cloudflare

Use an API token with email-sending permission. SMTP uses `smtp.mx.cloudflare.net:465` with
`api_token` as the user name.

### Brevo

Brevo accepts only one `ReplyTo` address and does not support inline attachments. An attachment
with `ContentId` set is rejected with `EmailValidationException`. Text-only messages are converted
to escaped HTML for Brevo's required `htmlContent` field.

### Mailtrap

Without an inbox ID, `MailtrapEmailSender` sends through the live stream. With `sandbox: true`,
the inbox ID is required and mail goes to that sandbox.

### Resend

Supports multiple reply-to addresses and inline attachments through `content_id`.

### Maileroo

Maileroo supports multiple reply-to addresses and custom headers, and limits subjects to 255
characters. Its API has no attachment content-ID field: inline attachments are sent with
`inline: true`, and HTML must reference them as `cid:<file name>`. An inline attachment whose
`ContentId` differs from its file name is rejected with `EmailValidationException`.

### Microsoft Graph

`GraphEmailSender` sends the whole MIME message as base64 in one `sendMail` request. Without a
`userId`, it uses `/me/sendMail` with a delegated token. With a `userId`, it uses
`/users/{id}/sendMail` and can be used with application permissions. Graph limits this request to
roughly 4 MB; use SMTP for larger Microsoft 365 attachments.

### Amazon SES

`AmazonSesEmailSender` sends raw MIME through the SES v2 API and signs requests with SigV4. It
accepts temporary credentials through `sessionToken`. Use SES SMTP credentials, not IAM access
keys, for `SmtpEmailSender.AmazonSes`.

## OAuth2

`OAuth2TokenProvider` caches tokens in memory, refreshes lazily before expiry, serializes
concurrent refreshes, and raises `RefreshTokenUpdated` when the provider rotates the refresh
token. Persist the new value from that event.

### Authorization code flow

```csharp
var options = MicrosoftOAuth2.CreateSmtpOptions(
    clientId: Environment.GetEnvironmentVariable("CLIENT_ID")!,
    clientSecret: Environment.GetEnvironmentVariable("CLIENT_SECRET"),
    redirectUri: "http://localhost:8080/callback",
    tenantId: "common");

var authorization = OAuth2AuthorizationRequest.Create(options);

// Open authorization.AuthorizationUrl in a browser, then verify that the callback
// returns the same state value before exchanging the code.
Console.WriteLine(authorization.AuthorizationUrl);

var code = "code-from-callback";
var tokenClient = new OAuth2TokenClient(options);
var token = await tokenClient.ExchangeAuthorizationCodeAsync(code, authorization.CodeVerifier);

var tokenProvider = new OAuth2TokenProvider(tokenClient, token);
tokenProvider.RefreshTokenUpdated += (_, e) => SaveRefreshToken(e.NewRefreshToken);

var sender = SmtpEmailSender.Microsoft365("sender@contoso.com", tokenProvider);
```

After a restart, start from the saved refresh token:

```csharp
var tokenProvider = new OAuth2TokenProvider(
    new OAuth2TokenClient(options),
    OAuth2Token.FromRefreshToken(savedRefreshToken));

var sender = SmtpEmailSender.Microsoft365("sender@contoso.com", tokenProvider);
```

Google uses `GoogleOAuth2.CreateOptions(...)` with the `https://mail.google.com/` scope. The
helper requests `access_type=offline` and `prompt=consent` so Google issues a refresh token.

### Client credentials

```csharp
var options = MicrosoftOAuth2.CreateGraphClientCredentialsOptions(
    clientId,
    clientSecret,
    tenantId);

var tokenProvider = OAuth2TokenProvider.CreateClientCredentials(
    new OAuth2TokenClient(options));

var graph = new GraphEmailSender(tokenProvider, "sender@contoso.com");
```

For SMTP application authentication, use `MicrosoftOAuth2.CreateSmtpClientCredentialsOptions`.
The tenant must allow the application permission and SMTP AUTH for the mailbox.

### Token caching

- A token is reused until it expires inside `RefreshAhead` (default five minutes).
- An access-only token without a refresh token remains usable until its actual expiry.
- A refresh response without `refresh_token` keeps the previous refresh token.
- `OAuth2TokenProvider` has no background timer; refresh happens on the next send.
- `GetAccessTokenAsync` is safe to call concurrently.

## Errors

- `EmailValidationException`: invalid message or a provider-specific limitation. No request is sent.
- `OAuth2Exception`: token endpoint failure. Exposes `Error`, `ErrorDescription`, and `StatusCode`.
- `EmailSendException`: HTTP or SMTP failure. Exposes `Provider`, `StatusCode` (HTTP status or SMTP
  reply code), and a truncated `ResponseBody` (provider response or SMTP reply text) when available.

All senders validate the message before any network call.

## Provider limits

| Limit | Behavior |
| --- | --- |
| Brevo inline attachments | `EmailValidationException` |
| Brevo, Cloudflare, Mailtrap multiple reply-to | `EmailValidationException` |
| Maileroo subject over 255 characters | `EmailValidationException` |
| Maileroo content ID differing from file name | `EmailValidationException` |
| Graph MIME body | About 4 MB; larger messages need SMTP |
| Resend attachments | About 40 MB after base64 encoding |
| Cloudflare attachments | 5 MiB total |
| SES v2 raw message | 40 MB total after MIME encoding |

## Test and token tools

`scripts/mailtest/` has a bash and a PowerShell tool that read the same YAML, send one test message through
SMTP, Graph or Cloudflare, and obtain OAuth2 refresh tokens for Microsoft 365 and Gmail. See
[Mail test tools](docs/MAILTEST.md).

## Sample and verification

```bash
dotnet run --project src/samples/LMail.Samples -- --self-test
dotnet run --project src/samples/LMail.Samples -- --live resend
```

`--self-test` runs offline checks for validation, MIME output, provider JSON payloads, SigV4
signing, OAuth2 exchange, caching and rotation, HTTP body timeout/cancellation, and a real local
SMTP exchange (STARTTLS, implicit TLS, PLAIN/LOGIN/XOAUTH2, envelope recipients, Bcc privacy and
timeouts). MIME output is parsed back with MimeKit, which the sample project references only as an
independent reader. `--live <provider>` reads credentials from
environment variables; see [LiveSend.cs](src/samples/LMail.Samples/LiveSend.cs) for the exact
names.

Live provider sends are **NOT VERIFIED** in this repository because no provider credentials are
available. Verify each provider in an environment with real credentials before production use.

The size limits above describe provider limits; the library forwards provider rejections as
`EmailSendException`. SES v2's limit follows the [AWS attachment documentation](https://docs.aws.amazon.com/ses/latest/dg/attachments.html).

## License

MIT
