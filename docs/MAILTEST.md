# Mail test and token tools

`scripts/mailtest/` has one single-file tool in two flavours. Each file does the same two things, reads the
same `mailtest.config.yaml` and prints the same messages:

| Entry | What it does |
| --- | --- |
| **Send a test mail** | Sends one message with the profile you pick in the YAML (SMTP, Cloudflare REST, Microsoft Graph) and tells you whether the server accepted it. |
| **Authorize an OAuth2 provider** | Lists the OAuth2 providers in your YAML, lets you choose one, signs you in through a **local web server on this machine** and prints the **refresh token** to give to LMail. It sends nothing. |

| File | For | Needs |
| --- | --- | --- |
| `mailtest.sh` | Linux, macOS | bash 4+, `curl`, `openssl`, `awk`, `od`; `python3` is optional (it runs the local web server; without it you paste the redirected URL) |
| `mailtest.ps1` | Windows PowerShell 5.1, PowerShell 7+ | nothing to install |

Each file is self-contained: copy one anywhere. On the first run it creates `mailtest.config.yaml` **next to
itself** from a template it carries, prints where it put it, and carries on, so a fresh copy is usable at
once. The config is never overwritten, is git-ignored, and is loaded from the script's directory regardless of
the current directory.

They are connectivity and credential checks, not a bulk mailer. To send from your application use the `LMail`
library itself ([README](../README.md)).

## Quick start

Run the script with no arguments in a terminal to get the menu:

```text
mailtest - choose an action:
  1) Send a test mail (profile: smtp_starttls)
  2) Authorize an OAuth2 provider and get a refresh token
  q) Quit
```

or go straight to an entry:

| | `mailtest.sh` | `mailtest.ps1` |
| --- | --- | --- |
| Send with the default profile | `./mailtest.sh` | `.\mailtest.ps1` |
| Send with another profile | `--profile NAME` | `-Profile NAME` |
| Check the YAML offline | `--validate-only` | `-ValidateOnly` |
| Authorize (pick from the list) | `--init-oauth` | `-InitOAuth` |
| Authorize a specific profile | `--init-oauth --profile NAME` | `-InitOAuth -Profile NAME` |
| Override recipient, subject, text | `--to A,B` `--subject S` `--body T` | `-To A,B` `-Subject S` `-Body T` |
| Show the SMTP conversation (AUTH hidden) | `--verbose-protocol` | `-VerboseProtocol` |
| Use another config file | `--config PATH` | `-ConfigPath PATH` |

```bash
./mailtest.sh                              # first run: creates mailtest.config.yaml, then the menu
$EDITOR mailtest.config.yaml               # message.from / message.to and the profile you want
export SMTP_PASSWORD='app-password'
./mailtest.sh --validate-only
./mailtest.sh --profile smtp_starttls
```

Exit codes: **0** accepted (or validation passed), **1** configuration, connection, authentication or API
error. Sending with the untouched template (both `message.from` and `message.to` still the placeholders) is
refused with a pointer to the config file. "Accepted" means the server or API took the message; it does not
prove inbox delivery. If PowerShell's execution policy blocks the script, follow your organisation's policy,
for example `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\mailtest.ps1` for that one process.

## The configuration

```yaml
default_profile: smtp_starttls          # used by "send" when you pass no profile
message:
  from: "sender@example.com"            # plain ASCII addresses
  to: ["recipient@example.net"]         # also: cc, bcc, subject, text, html
profiles:
  smtp_starttls:                        # a profile = one way of sending
    type: smtp                          # smtp | cloudflare_api | microsoft_graph
    host: smtp.example.com
    port: 587
    security: starttls                  # starttls | ssl | none
    auth:
      mode: auto                        # auto | plain | login | xoauth2 | none
      username: sender@example.com
      password: env:SMTP_PASSWORD       # env:NAME reads an environment variable
```

Write secrets as `env:NAME` and export `NAME`; the YAML then holds no secrets. The generated file has a
ready-made, commented profile for each case; copy one and replace the placeholders:

| Profile | Use it for |
| --- | --- |
| `smtp_starttls`, `smtp_ssl`, `smtp_relay` | any SMTP server (587 + STARTTLS, 465, unauthenticated relay) |
| `microsoft_user`, `microsoft_graph_user` | Microsoft 365 SMTP or Graph for a signed-in user (OAuth2 refresh token) |
| `microsoft_app`, `microsoft_graph_app` | Microsoft 365 SMTP or Graph with an application identity (client credentials) |
| `gmail_oauth` | Gmail SMTP with XOAUTH2 (OAuth2 refresh token) |
| `generic_oauth` | any other OAuth2 provider with the authorization code flow |
| `generic_xoauth2` | any SMTP server, with an access token you already have |
| `cloudflare_smtp`, `cloudflare_api` | Cloudflare Email Sending over SMTP or HTTPS |

Password and OAuth modes are refused on an unencrypted connection unless the profile sets
`allow_insecure_auth: true`. `auth.mode: auto` picks `PLAIN`, otherwise `LOGIN`, from what the server
advertises. TLS certificates are always verified; there is no switch to turn that off.

The `oauth2` block of an OAuth profile:

| Key | Meaning |
| --- | --- |
| `provider` | `microsoft`, `google` or `generic` |
| `grant` | `refresh_token` (sign in once, reuse the token), `client_credentials` (Microsoft application identity) or `access_token` |
| `client_id`, `client_secret` | from the provider's app registration; omit the secret for a public client |
| `refresh_token` | `env:NAME` with the token you get from the authorize entry |
| `tenant` | Microsoft only: `common` (default; work, school and personal accounts), `organizations` (work or school only), `consumers` (personal only), or a tenant GUID / domain |
| `scope` | override the default scope |
| `flow` | Microsoft only: `loopback` (default) or `device_code` |
| `redirect_port` | fixed port for the local web server (default: a free port) |
| `authorize_url`, `token_url` | `generic` provider endpoints |

Hosts for the other providers LMail supports (use `smtp_starttls` or `smtp_ssl` with these values):

| Provider | Host | Port / security | User / password |
| --- | --- | --- | --- |
| Brevo | `smtp-relay.brevo.com` | 587 `starttls` | login / SMTP key |
| Mailtrap live | `live.smtp.mailtrap.io` | 587 `starttls` | user / password |
| Mailtrap sandbox | `sandbox.smtp.mailtrap.io` | 2525 `starttls` | user / password |
| Maileroo | `smtp.maileroo.com` | 587 `starttls` | account e-mail / password |
| Amazon SES | `email-smtp.<region>.amazonaws.com` | 587 `starttls` | SES SMTP credentials (not IAM keys) |
| Gmail (app password) | `smtp.gmail.com` | 587 `starttls` or 465 `ssl` | address / app password |

Resend has no SMTP profile here; use it through `LMail` (`ResendEmailSender`).

## Getting an OAuth2 token

1. Register an application with the provider (below). Put its `client_id` (and secret) into the profile, or
   skip that: when the profile still has the template `client_id`, the authorize entry asks for it in the
   terminal (and for the client secret, hidden, and for a generic provider the endpoints and scope). Nothing
   typed there is saved; add the values to the YAML to reuse them. Without a terminal it stops and tells you
   to edit the file.
2. Start the authorize entry (`2` in the menu, or `--init-oauth` / `-InitOAuth`) and choose the provider.
3. The tool prints an **authorization URL**. It never opens a browser: open the URL yourself, sign in and
   approve.
4. The provider redirects the browser to `http://localhost:PORT`. A small web server on this machine, started
   by the tool, receives the code, checks the `state`, exchanges the code (PKCE) and prints:

   ```text
   REFRESH_TOKEN=...
   ```

5. Store the value in a secret manager or an environment variable (the tool names the variable your profile
   uses) and test it with the send entry. Hand the same value to LMail.

The refresh token is a long-lived credential. It is printed to the terminal only and stored nowhere; never
commit it or paste it into tickets or logs. It can stop working when access is revoked, a policy changes or
it goes unused for a long time; run the authorize entry again.

**On a remote or headless machine** the browser cannot reach `localhost` of that machine. Either forward the
port (`ssh -L PORT:localhost:PORT host`, with `redirect_port: PORT` in the profile) and open the URL in your
local browser, or let the tool fall back: when it cannot listen (no `python3`, port in use) it asks you to
paste the address-bar URL (or just its `code=` value) after approving. Microsoft profiles can also use
`flow: device_code`, which needs no redirect at all.

### Microsoft (Microsoft 365, Outlook.com, Graph)

Sign in as a user (`microsoft_user` for SMTP, `microsoft_graph_user` for Graph):

1. In the [Microsoft Entra admin center](https://entra.microsoft.com) open *App registrations > New
   registration*
   ([guide](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app)).
2. Choose the **supported account types** and use the matching `tenant:` in the profile:

   | Account type to choose | `tenant:` | Who can sign in |
   | --- | --- | --- |
   | Any organizational directory **and personal Microsoft accounts** | `common` (default) | work, school and personal accounts |
   | Any organizational directory | `organizations` | work or school accounts |
   | Personal Microsoft accounts only | `consumers` | personal accounts |
   | This organizational directory only | tenant GUID or domain | one company |

3. **Callback / redirect URI**: platform *Public client/native (mobile & desktop)*, value
   `http://localhost`. Loopback addresses accept any port, so the random port the tool picks needs no extra
   registration. (Later: *Authentication > Add a platform > Mobile and desktop applications*.)
4. Copy **Application (client) ID** from *Overview* into `client_id`. A public client has no secret.
5. *API permissions > Add a permission*: for Graph **Microsoft Graph > Delegated > Mail.Send**; for SMTP
   **Office 365 Exchange Online > Delegated > SMTP.Send** (under *APIs my organization uses*). Consent as your
   organisation requires.
6. SMTP on a work or school mailbox also needs SMTP AUTH allowed for it
   ([enable or disable SMTP AUTH](https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/authenticated-client-smtp-submission)).
   For a personal Outlook.com mailbox use host `smtp-mail.outlook.com`.
7. Run the authorize entry, choose the profile, open the URL, sign in, export the printed token
   (`MS_SMTP_REFRESH_TOKEN` or `MS_GRAPH_REFRESH_TOKEN`). SMTP and Graph tokens are separate because their
   scopes differ.

For `flow: device_code` (no browser on the machine) allow **public client flows** under *Authentication*; no
redirect URI is used.

The default scopes are `https://outlook.office.com/SMTP.Send offline_access` (SMTP) and
`https://graph.microsoft.com/Mail.Send offline_access` (Graph), as documented in
[Authenticate an IMAP, POP or SMTP connection using OAuth](https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth);
set `scope` to override. The device code flow is described in
[OAuth 2.0 device authorization grant](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code).

**Application identity** (`microsoft_app`, `microsoft_graph_app`; work or school tenants only): no user signs in,
so there is **no callback / redirect URI** and no token to fetch; the tool exchanges the client id and secret
for an access token on every run.

1. *New registration*, account type *This organizational directory only*; leave the redirect URI empty.
2. From *Overview* copy **Application (client) ID** -> `client_id` and **Directory (tenant) ID** -> `tenant`
   (a GUID or domain, never `common`, `organizations` or `consumers`).
3. *Certificates & secrets > New client secret*: copy the secret **Value** (shown once) into the variable named
   by `client_secret: env:NAME`.
4. *API permissions > Add a permission > Application permissions*, then *Grant admin consent*: for Graph
   **Microsoft Graph > Mail.Send** (the `mailbox` must equal `message.from`,
   [sendMail](https://learn.microsoft.com/en-us/graph/api/user-sendmail)); for SMTP **Office 365 Exchange
   Online > SMTP.SendAsApp** plus an Exchange service principal and mailbox permission (Microsoft page above,
   section *Use client credentials grant flow*).
5. `Mail.Send` as an application permission can send as **any** mailbox: restrict it to the mailboxes you need
   with an Exchange Application Access Policy.

### Gmail

1. In the [Google Cloud console](https://console.cloud.google.com/apis/credentials) create or select a project.
2. Configure the *OAuth consent screen* (*Google Auth Platform*): *External*, or *Internal* for a Google
   Workspace organisation. While the app is in *Testing* status add your account under *Test users*; refresh
   tokens then expire after 7 days. Publish the app for longer-lived tokens.
3. *Credentials > Create credentials > OAuth client ID*, application type **Desktop app**. Copy the client id
   into `client_id` and the client secret into the variable named by `client_secret: env:NAME`.
4. **Callback / redirect URI**: nothing to register for a Desktop app client; it accepts `http://localhost` and
   `http://127.0.0.1` on any port. With a *Web application* client instead, add `http://localhost:PORT` under
   *Authorized redirect URIs* and set `redirect_port: PORT` in the profile.
5. Run the authorize entry, choose `gmail_oauth`, open the URL and approve access to `https://mail.google.com/`
   ([Gmail OAuth 2.0 mechanism](https://developers.google.com/workspace/gmail/imap/xoauth2-protocol); the
   sign-in is the authorization code flow with PKCE and a loopback redirect,
   [OAuth 2.0 for desktop apps](https://developers.google.com/identity/protocols/oauth2/native-app)). Export the
   printed token as `GOOGLE_REFRESH_TOKEN`.

### Any other OAuth2 provider

Use `generic_oauth`: take `authorize_url`, `token_url` and `scope` (include the scope that yields a refresh
token, often `offline_access`) from the provider's developer documentation, and `client_id` (plus
`client_secret` for a confidential client) from the app you register there. **Callback / redirect URI:**
register `http://localhost`; if the provider requires an exact URI, register `http://localhost:8765` and set
`redirect_port: 8765`. The provider must support the authorization code flow and return a refresh token.

### Cloudflare

Enable Email Sending and verify the sender domain first
([Send emails](https://developers.cloudflare.com/email-service/get-started/send-emails/)). Create an API token
with **Email Sending: Edit**. `cloudflare_smtp` uses `smtp.mx.cloudflare.net:465` (implicit TLS), the literal
user name `api_token` and the token as password; `cloudflare_api` uses HTTPS and needs your account id. No
OAuth is involved.

## Use the token in LMail

```csharp
using LMail;

// Gmail
var options = GoogleOAuth2.CreateOptions(clientId, clientSecret);
var provider = new OAuth2TokenProvider(
    new OAuth2TokenClient(options),
    OAuth2Token.FromRefreshToken(Environment.GetEnvironmentVariable("GOOGLE_REFRESH_TOKEN")!));
var sender = SmtpEmailSender.Gmail("you@gmail.com", provider);

// Microsoft 365 SMTP (MicrosoftOAuth2.CreateGraphOptions + GraphEmailSender for Graph)
var msOptions = MicrosoftOAuth2.CreateSmtpOptions(clientId, clientSecret: null, tenantId: "contoso.com");
var msProvider = new OAuth2TokenProvider(
    new OAuth2TokenClient(msOptions),
    OAuth2Token.FromRefreshToken(Environment.GetEnvironmentVariable("MS_SMTP_REFRESH_TOKEN")!));
var m365 = SmtpEmailSender.Microsoft365("you@contoso.com", msProvider);

// Any other provider: describe its endpoints yourself
var custom = new OAuth2ClientOptions
{
    AuthorizationEndpoint = "https://login.example.com/oauth2/authorize",
    TokenEndpoint = "https://login.example.com/oauth2/token",
    ClientId = "your-client-id",
    ClientSecret = Environment.GetEnvironmentVariable("OAUTH_CLIENT_SECRET"),
    Scope = "mail.send offline_access"
};
var customSender = new SmtpEmailSender(new SmtpOptions
{
    Host = "smtp.example.com",
    Port = 587,
    Security = SmtpSecurity.StartTls,
    UserName = "you@example.com",
    AccessTokenProvider = new OAuth2TokenProvider(
        new OAuth2TokenClient(custom),
        OAuth2Token.FromRefreshToken(Environment.GetEnvironmentVariable("OAUTH_REFRESH_TOKEN")!))
});

// Persist a rotated refresh token when the provider issues one:
provider.RefreshTokenUpdated += (_, e) => SaveRefreshToken(e.NewRefreshToken);
```

See [OAuth2 in the README](../README.md#oauth2) for the full flow and token caching rules.

## Security notes

* Prefer `env:NAME` for passwords, client secrets, API tokens and refresh tokens. `mailtest.sh` passes
  secrets to `curl` through a config on standard input, so they do not appear in `ps` output.
* The local web server listens on the loopback interface only (`127.0.0.1` or `::1`), serves one short page, accepts the first request that
  carries a `code` or `error`, and checks the `state` value before using the code.
* `--verbose-protocol` hides AUTH commands and challenges. Do not enable PowerShell transcripts or HTTP
  debugging and then share the raw log.
* Neither tool sends attachments or internationalised (SMTPUTF8) addresses; sender and recipient addresses
  must be plain ASCII `name@domain`.
* Sign-in flows, Graph, Cloudflare and the OAuth endpoints are exercised against stand-ins in development,
  not against the live services; try your own provider once before relying on it.
