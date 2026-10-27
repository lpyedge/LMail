# Architecture

`LMail` is a single `netstandard2.1` library that exposes one send contract for SMTP and several
hosted providers. The package is intentionally a monolith: every sender shares the same message
model, validation, MIME builder, HTTP helper, and error types. It has no mail-library dependency
(`System.Text.Json` only): MIME and SMTP are implemented in the package.

## Modules

| Path | Responsibility |
| --- | --- |
| `EmailMessage.cs` | Public `IEmailSender`, `EmailMessage`, `EmailAddress`, and `EmailAttachment` models |
| `Exceptions.cs` | `LMailException`, `EmailValidationException`, `OAuth2Exception`, `EmailSendException` |
| `Internal/EmailValidator.cs` | Message, address, attachment, and custom-header validation |
| `Internal/MimeMessageFactory.cs` | Builds a 7-bit ASCII, CRLF MIME message (`byte[]`) for SMTP, Graph, and SES; optionally includes `Bcc` |
| `Internal/MimeHeaders.cs` | Header folding, RFC 2047 encoded words, RFC 2231 parameters, mailbox lists |
| `Internal/AddressText.cs` | IDN-to-ASCII conversion and `Name <address>` formatting for JSON payloads |
| `Internal/PayloadHelpers.cs` | Content-ID normalization, single reply-to enforcement, address formatting |
| `Internal/ProviderHttp.cs` | Shared `HttpClient`, JSON options, user agent, timeouts, and provider error mapping |
| `Internal/ProviderPayloads.cs` | Shared JSON address objects/lists, address/mailbox strings, cc/bcc, headers, and attachments |
| `Internal/AwsSigV4Signer.cs` | AWS Signature Version 4 for SES v2 JSON requests |
| `Smtp/SmtpOptions.cs` | Public SMTP options and `SmtpSecurity` |
| `Smtp/SmtpConnection.cs` | SMTP wire protocol on `TcpClient`/`SslStream`: replies, EHLO, STARTTLS, AUTH, DATA, per-operation timeout and cancellation |
| `Smtp/SmtpEmailSender.cs` | SMTP send flow (security mode, auth choice, envelope) plus Gmail, Microsoft 365, Cloudflare, Brevo, Mailtrap, Maileroo, and SES factories |
| `OAuth2/` | Authorization URL/PKCE, token exchange, refresh, client credentials, caching, and provider helpers |
| `Providers/` | Graph, Cloudflare, Brevo, Mailtrap, Resend, Maileroo, and Amazon SES senders |

## Public contract

```csharp
Task IEmailSender.SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
```

`EmailMessage` is provider independent. SMTP, Graph, and SES use the full MIME message, so custom
headers and inline attachments work without per-provider translation. REST providers map the model
to their JSON schemas.

SMTP security is exposed through `SmtpSecurity`, and OAuth2 providers are exposed through
`IAccessTokenProvider`.

## Send flow

1. The sender validates `EmailMessage` with `EmailValidator`. Provider-specific constraints are
   checked next; no network call occurs before validation succeeds.
2. SMTP builds MIME without a `Bcc` header and passes the de-duplicated `To`, `Cc`, and `Bcc` as
   envelope recipients. `Auto` security means implicit TLS on port 465, otherwise STARTTLS when the
   server offers it. Passwords use AUTH PLAIN (or LOGIN); OAuth2 uses XOAUTH2. Certificates are
   validated by the platform; there is no option to disable validation. Credentials are refused on a
   connection that is not encrypted unless `SmtpSecurity.None` is chosen explicitly. An SMTP reply code is kept
   as `EmailSendException.StatusCode`.
3. Graph builds MIME with `Bcc`, encodes it as base64, and posts it to `sendMail`.
4. SES builds MIME without `Bcc`, encodes it as base64, adds explicit SES destinations, signs the
   JSON request with SigV4, and posts to the SES v2 endpoint.
5. REST senders map addresses, bodies, headers, and attachments to provider JSON, then use
   `ProviderHttp` for the request.
6. HTTP failures become `EmailSendException` with the provider name, status code, and truncated
   response body. SMTP failures become `EmailSendException` with provider `SMTP`. OAuth2 failures
   become `OAuth2Exception`.

## OAuth2 flow

- `OAuth2AuthorizationRequest.Create` builds an authorization URL with an S256 PKCE challenge and a
  random state value.
- `OAuth2TokenClient` exchanges an authorization code, refreshes a token, or requests a
  client-credentials token. It stores no state.
- `OAuth2TokenProvider` owns the cached token. Refreshes are protected by a semaphore; a token is
  reused until it expires within `RefreshAhead`.
- When a refresh returns a different refresh token, `RefreshTokenUpdated` lets the application
  persist it. A missing refresh token in the response keeps the previous value.
- Access-only tokens cannot refresh, so they remain usable until actual expiry.
- An explicitly supplied `expires_in` must be finite, positive and representable; malformed
  token responses raise `OAuth2Exception` and retain the HTTP status.
- `MicrosoftOAuth2` and `GoogleOAuth2` provide endpoints and scopes. Microsoft has separate
  delegated and client-credentials scopes for SMTP and Graph.

There is no background refresh timer. Refresh happens on the next send after the cached token
enters the refresh window.

## Provider constraints

| Provider | Constraint |
| --- | --- |
| Brevo | No inline attachments; one reply-to; `htmlContent` is always generated |
| Cloudflare | One reply-to; REST and SMTP |
| Mailtrap | One reply-to; live and sandbox endpoints |
| Resend | Multiple reply-to; inline attachments use `content_id` |
| Maileroo | 255-character subject; no attachment content ID, inline uses `cid:<file name>` so `ContentId` must equal the file name |
| Graph | MIME `sendMail` limited to about 4 MB |
| SES | Raw MIME via v2 API; 40 MB limit after MIME encoding |
| SMTP | Envelope recipients include `Bcc` while the MIME header excludes it |

## Build and output

- Package project: `src/packages/LMail/LMail.csproj`, target `netstandard2.1`.
- Sample project: `src/samples/LMail.Samples`, target `net10.0`, safe to exclude from packaging.
  It references MimeKit only to parse the library's MIME output in the self-test.
- Build intermediates: `temp/build/dotnet` through `Directory.Build.props`.
- NuGet cache: `temp/deps/nuget` through `NuGet.Config`.
- Packages: `dist/packages`.
- Offline verification: the sample `--self-test` mode.
- HTTP responses are buffered within `HttpClient.SendAsync` so its timeout and the caller's
  cancellation token cover response bodies as well as headers.
- HTTP MIME serialization explicitly uses CRLF. For text-only messages, inline parts stay in
  the MIME attachment collection so their bytes are retained.
- CI validates and packs on main pushes and pull requests. Release/manual publishing uses the
  same scripts, uploads `.nupkg`/`.snupkg` artifacts and pushes the exact package version to NuGet.org.
  See [PUBLISH.md](PUBLISH.md).
