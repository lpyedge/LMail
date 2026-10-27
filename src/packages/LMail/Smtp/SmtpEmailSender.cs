using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LMail.Internal;

namespace LMail
{
    /// <summary>
    /// Sends messages through any SMTP server with a built-in SMTP client. Supports PLAIN/LOGIN passwords and XOAUTH2.
    /// </summary>
    public sealed class SmtpEmailSender : IEmailSender
    {
        private readonly SmtpOptions _options;

        /// <summary>
        /// Creates an SMTP sender.
        /// </summary>
        public SmtpEmailSender(SmtpOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            ValidateOptions(options);

            _options = new SmtpOptions
            {
                Host = options.Host,
                Port = options.Port,
                Security = options.Security,
                UserName = options.UserName,
                Password = options.Password,
                AccessTokenProvider = options.AccessTokenProvider,
                Timeout = options.Timeout,
                CertificateValidation = options.CertificateValidation
            };
        }

        private SmtpEmailSender(
            string host,
            int port,
            SmtpSecurity security = SmtpSecurity.Auto,
            string? userName = null,
            string? password = null)
            : this(new SmtpOptions
            {
                Host = host,
                Port = port,
                Security = security,
                UserName = userName,
                Password = password
            })
        {
        }

        /// <summary>
        /// Gmail with an app password.
        /// </summary>
        public static SmtpEmailSender Gmail(string address, string appPassword)
        {
            return new SmtpEmailSender("smtp.gmail.com", 587, SmtpSecurity.StartTls, address, appPassword);
        }

        /// <summary>
        /// Gmail with an OAuth2 access token. Use <c>https://mail.google.com/</c> as the scope.
        /// </summary>
        public static SmtpEmailSender Gmail(string address, IAccessTokenProvider accessTokenProvider)
        {
            return new SmtpEmailSender(new SmtpOptions
            {
                Host = "smtp.gmail.com",
                Port = 587,
                Security = SmtpSecurity.StartTls,
                UserName = address,
                AccessTokenProvider = accessTokenProvider
            });
        }

        /// <summary>
        /// Microsoft 365 with OAuth2. Basic authentication is not supported by Microsoft 365 SMTP.
        /// </summary>
        public static SmtpEmailSender Microsoft365(string address, IAccessTokenProvider accessTokenProvider)
        {
            return new SmtpEmailSender(new SmtpOptions
            {
                Host = "smtp.office365.com",
                Port = 587,
                Security = SmtpSecurity.StartTls,
                UserName = address,
                AccessTokenProvider = accessTokenProvider
            });
        }

        /// <summary>
        /// Cloudflare Email Sending SMTP. Uses port 465 with implicit TLS.
        /// </summary>
        public static SmtpEmailSender Cloudflare(string apiToken)
        {
            return new SmtpEmailSender(
                "smtp.mx.cloudflare.net",
                465,
                SmtpSecurity.SslOnConnect,
                "api_token",
                apiToken);
        }

        /// <summary>
        /// Brevo SMTP relay.
        /// </summary>
        public static SmtpEmailSender Brevo(string login, string smtpKey)
        {
            return new SmtpEmailSender("smtp-relay.brevo.com", 587, SmtpSecurity.StartTls, login, smtpKey);
        }

        /// <summary>
        /// Mailtrap live or sandbox SMTP.
        /// </summary>
        public static SmtpEmailSender Mailtrap(string userName, string password, bool sandbox = false)
        {
            return sandbox
                ? new SmtpEmailSender("sandbox.smtp.mailtrap.io", 2525, SmtpSecurity.Auto, userName, password)
                : new SmtpEmailSender("live.smtp.mailtrap.io", 587, SmtpSecurity.StartTls, userName, password);
        }

        /// <summary>
        /// Maileroo SMTP relay. Use the SMTP account email address as the user name.
        /// </summary>
        public static SmtpEmailSender Maileroo(string userName, string password)
        {
            return new SmtpEmailSender("smtp.maileroo.com", 587, SmtpSecurity.StartTls, userName, password);
        }

        /// <summary>
        /// Amazon SES SMTP. These are SES SMTP credentials, not IAM access keys.
        /// </summary>
        public static SmtpEmailSender AmazonSes(string region, string userName, string password)
        {
            if (string.IsNullOrWhiteSpace(region))
            {
                throw new ArgumentException("Region must not be empty.", nameof(region));
            }

            return new SmtpEmailSender(
                "email-smtp." + region.Trim() + ".amazonaws.com",
                587,
                SmtpSecurity.StartTls,
                userName,
                password);
        }

        /// <inheritdoc />
        public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            var mime = MimeMessageFactory.Create(message, includeBcc: false);
            var from = AddressText.ToAscii(message.From!.Address);
            var recipients = CollectRecipients(message);
            var accessToken = _options.AccessTokenProvider == null
                ? null
                : await _options.AccessTokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            var security = _options.Security == SmtpSecurity.Auto
                ? (_options.Port == 465 ? SmtpSecurity.SslOnConnect : SmtpSecurity.StartTlsWhenAvailable)
                : _options.Security;

            try
            {
                using (var connection = new SmtpConnection(
                    _options.Host,
                    _options.Timeout,
                    _options.CertificateValidation,
                    cancellationToken))
                {
                    await connection.ConnectAsync(_options.Port, security == SmtpSecurity.SslOnConnect).ConfigureAwait(false);
                    await connection.EhloAsync().ConfigureAwait(false);

                    if (security == SmtpSecurity.StartTls || security == SmtpSecurity.StartTlsWhenAvailable)
                    {
                        if (connection.SupportsStartTls)
                        {
                            await connection.StartTlsAsync().ConfigureAwait(false);
                        }
                        else if (security == SmtpSecurity.StartTls)
                        {
                            throw new EmailSendException("SMTP server does not support STARTTLS.", "SMTP");
                        }
                    }

                    var authenticate = accessToken != null || !string.IsNullOrEmpty(_options.UserName);
                    if (authenticate && !connection.IsEncrypted && security != SmtpSecurity.None)
                    {
                        throw new EmailSendException(
                            "SMTP server did not offer TLS; credentials are not sent over a plain connection. " +
                            "Set Security to None to allow it.",
                            "SMTP");
                    }

                    if (accessToken != null)
                    {
                        if (!connection.SupportsAuth("XOAUTH2"))
                        {
                            throw new EmailSendException("SMTP server does not support XOAUTH2.", "SMTP");
                        }

                        await connection.AuthenticateOAuth2Async(_options.UserName!, accessToken).ConfigureAwait(false);
                    }
                    else if (!string.IsNullOrEmpty(_options.UserName))
                    {
                        await connection.AuthenticatePasswordAsync(_options.UserName!, _options.Password!).ConfigureAwait(false);
                    }

                    await connection.SendMessageAsync(from, recipients, mime).ConfigureAwait(false);
                    await connection.QuitAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (LMailException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new EmailSendException("SMTP send failed: " + ex.Message, "SMTP", null, null, ex);
            }
        }

        private static List<string> CollectRecipients(EmailMessage message)
        {
            var recipients = new List<string>(message.To.Count + message.Cc.Count + message.Bcc.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var address in message.To.Concat(message.Cc).Concat(message.Bcc))
            {
                var ascii = AddressText.ToAscii(address.Address);

                if (seen.Add(ascii))
                {
                    recipients.Add(ascii);
                }
            }

            return recipients;
        }

        private static void ValidateOptions(SmtpOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.Host))
            {
                throw new ArgumentException("SMTP host must not be empty.", nameof(options));
            }

            if (options.Port < 1 || options.Port > 65535)
            {
                throw new ArgumentException("SMTP port must be between 1 and 65535.", nameof(options));
            }

            if (options.Timeout <= TimeSpan.Zero)
            {
                throw new ArgumentException("Timeout must be greater than zero.", nameof(options));
            }

            if (options.AccessTokenProvider != null && string.IsNullOrWhiteSpace(options.UserName))
            {
                throw new ArgumentException("UserName is required when using OAuth2.", nameof(options));
            }

            if (options.AccessTokenProvider != null && !string.IsNullOrEmpty(options.Password))
            {
                throw new ArgumentException(
                    "Password and AccessTokenProvider are mutually exclusive.",
                    nameof(options));
            }

            if (options.AccessTokenProvider == null)
            {
                if (!string.IsNullOrEmpty(options.UserName) && string.IsNullOrEmpty(options.Password))
                {
                    throw new ArgumentException(
                        "Password is required when UserName is set.",
                        nameof(options));
                }

                if (string.IsNullOrEmpty(options.UserName) && !string.IsNullOrEmpty(options.Password))
                {
                    throw new ArgumentException(
                        "UserName is required when Password is set.",
                        nameof(options));
                }
            }
        }
    }
}
