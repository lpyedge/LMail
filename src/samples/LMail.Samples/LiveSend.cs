using System;
using System.IO;
using System.Threading.Tasks;

namespace LMail.Samples
{
    internal static class LiveSend
    {
        public static async Task<int> RunAsync(string provider)
        {
            var message = CreateMessage();
            var sender = CreateSender(provider);

            Console.WriteLine("Sending through " + provider + " to " + message.To[0].Address + "...");
            await sender.SendAsync(message).ConfigureAwait(false);
            Console.WriteLine("Provider accepted the message. Check the recipient inbox.");
            return 0;
        }

        private static IEmailSender CreateSender(string provider)
        {
            switch (provider.ToLowerInvariant())
            {
                case "smtp":
                {
                    var portText = Env.Optional("LMAIL_SMTP_PORT");
                    var securityText = Env.Optional("LMAIL_SMTP_SECURITY");

                    return new SmtpEmailSender(new SmtpOptions
                    {
                        Host = Env.Required("LMAIL_SMTP_HOST"),
                        Port = portText == null ? 587 : int.Parse(portText),
                        Security = securityText == null
                            ? SmtpSecurity.Auto
                            : (SmtpSecurity)Enum.Parse(typeof(SmtpSecurity), securityText, true),
                        UserName = Env.Optional("LMAIL_SMTP_USER"),
                        Password = Env.Optional("LMAIL_SMTP_PASSWORD"),
                        AccessTokenProvider = Env.Optional("LMAIL_ACCESS_TOKEN") is { } token
                            ? new StaticAccessTokenProvider(token)
                            : null
                    });
                }

                case "gmail":
                {
                    var address = Env.Required("LMAIL_GMAIL_ADDRESS");
                    var accessToken = Env.Optional("LMAIL_ACCESS_TOKEN");

                    return accessToken != null
                        ? SmtpEmailSender.Gmail(address, new StaticAccessTokenProvider(accessToken))
                        : SmtpEmailSender.Gmail(address, Env.Required("LMAIL_GMAIL_APP_PASSWORD"));
                }

                case "m365":
                    return SmtpEmailSender.Microsoft365(
                        Env.Required("LMAIL_M365_ADDRESS"),
                        MicrosoftTokenProvider(useGraph: false));

                case "graph":
                    return new GraphEmailSender(
                        MicrosoftTokenProvider(useGraph: true),
                        Env.Optional("LMAIL_GRAPH_USER_ID"));

                case "cloudflare":
                    return new CloudflareEmailSender(
                        Env.Required("LMAIL_CLOUDFLARE_ACCOUNT_ID"),
                        Env.Required("LMAIL_CLOUDFLARE_API_TOKEN"));

                case "brevo":
                    return new BrevoEmailSender(Env.Required("LMAIL_BREVO_API_KEY"));

                case "mailtrap":
                {
                    var inboxId = Env.Optional("LMAIL_MAILTRAP_INBOX_ID");
                    return new MailtrapEmailSender(
                        Env.Required("LMAIL_MAILTRAP_API_TOKEN"),
                        inboxId != null,
                        inboxId);
                }

                case "resend":
                    return new ResendEmailSender(Env.Required("LMAIL_RESEND_API_KEY"));

                case "maileroo":
                    return new MailerooEmailSender(Env.Required("LMAIL_MAILEROO_API_KEY"));

                case "ses":
                    return new AmazonSesEmailSender(
                        Env.Required("LMAIL_AWS_REGION"),
                        Env.Required("LMAIL_AWS_ACCESS_KEY_ID"),
                        Env.Required("LMAIL_AWS_SECRET_ACCESS_KEY"),
                        Env.Optional("LMAIL_AWS_SESSION_TOKEN"));

                default:
                    throw new ArgumentException("Unknown provider: " + provider);
            }
        }

        private static IAccessTokenProvider MicrosoftTokenProvider(bool useGraph)
        {
            var refreshToken = Env.Optional("LMAIL_M365_REFRESH_TOKEN");

            if (refreshToken == null)
            {
                return new StaticAccessTokenProvider(Env.Required("LMAIL_ACCESS_TOKEN"));
            }

            var options = useGraph
                ? MicrosoftOAuth2.CreateGraphOptions(
                    Env.Required("LMAIL_M365_CLIENT_ID"),
                    Env.Optional("LMAIL_M365_CLIENT_SECRET"),
                    null,
                    Env.Optional("LMAIL_M365_TENANT_ID") ?? "common")
                : MicrosoftOAuth2.CreateSmtpOptions(
                    Env.Required("LMAIL_M365_CLIENT_ID"),
                    Env.Optional("LMAIL_M365_CLIENT_SECRET"),
                    null,
                    Env.Optional("LMAIL_M365_TENANT_ID") ?? "common");

            var provider = new OAuth2TokenProvider(
                new OAuth2TokenClient(options),
                OAuth2Token.FromRefreshToken(refreshToken));

            provider.RefreshTokenUpdated += (_, e) =>
            {
                var path = Env.Optional("LMAIL_REFRESH_TOKEN_FILE");

                if (path == null)
                {
                    Console.Error.WriteLine(
                        "A new refresh token was issued. Rerun with LMAIL_REFRESH_TOKEN_FILE to persist it.");
                    return;
                }

                File.WriteAllText(path, e.NewRefreshToken);
                Console.Error.WriteLine("A new refresh token was written to " + path + ".");
            };

            return provider;
        }

        private static EmailMessage CreateMessage()
        {
            var message = new EmailMessage
            {
                From = new EmailAddress(
                    Env.Required("LMAIL_FROM"),
                    Env.Optional("LMAIL_FROM_NAME")),
                Subject = Env.Optional("LMAIL_SUBJECT") ?? "LMail live test",
                TextBody = Env.Optional("LMAIL_TEXT") ??
                           "This is a test message sent with LMail.",
                HtmlBody = Env.Optional("LMAIL_HTML") ??
                           "<p>This is a test message sent with <strong>LMail</strong>.</p>"
            };

            foreach (var address in Env.Required("LMAIL_TO").Split(','))
            {
                message.To.Add(new EmailAddress(address.Trim()));
            }

            foreach (var address in (Env.Optional("LMAIL_CC") ?? string.Empty).Split(','))
            {
                if (address.Trim().Length > 0)
                {
                    message.Cc.Add(new EmailAddress(address.Trim()));
                }
            }

            foreach (var address in (Env.Optional("LMAIL_BCC") ?? string.Empty).Split(','))
            {
                if (address.Trim().Length > 0)
                {
                    message.Bcc.Add(new EmailAddress(address.Trim()));
                }
            }

            return message;
        }
    }

    internal static class Env
    {
        public static string Required(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Environment variable " + name + " is required.");
            }

            return value!;
        }

        public static string? Optional(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
