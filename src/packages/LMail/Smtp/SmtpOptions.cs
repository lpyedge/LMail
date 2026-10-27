using System;
using System.Net.Security;

namespace LMail
{
    /// <summary>
    /// SMTP transport security mode.
    /// </summary>
    public enum SmtpSecurity
    {
        /// <summary>
        /// Implicit TLS on port 465; otherwise STARTTLS when the server offers it, and plain text
        /// when it does not. Credentials are never sent over a plain connection in this mode.
        /// </summary>
        Auto,

        /// <summary>
        /// No TLS, and credentials are sent as-is. Only use on a trusted local network.
        /// </summary>
        None,

        /// <summary>
        /// Require STARTTLS.
        /// </summary>
        StartTls,

        /// <summary>
        /// Use STARTTLS when the server advertises it.
        /// </summary>
        StartTlsWhenAvailable,

        /// <summary>
        /// Use implicit TLS (for example port 465).
        /// </summary>
        SslOnConnect
    }

    /// <summary>
    /// SMTP connection options.
    /// </summary>
    public sealed class SmtpOptions
    {
        /// <summary>
        /// SMTP host.
        /// </summary>
        public string Host { get; set; } = string.Empty;

        /// <summary>
        /// SMTP port. Defaults to 587.
        /// </summary>
        public int Port { get; set; } = 587;

        /// <summary>
        /// Transport security. Defaults to <see cref="SmtpSecurity.Auto"/>.
        /// </summary>
        public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;

        /// <summary>
        /// User name for password or OAuth2 authentication.
        /// </summary>
        public string? UserName { get; set; }

        /// <summary>
        /// Password or provider SMTP key. Mutually exclusive with <see cref="AccessTokenProvider"/>.
        /// </summary>
        public string? Password { get; set; }

        /// <summary>
        /// OAuth2 token source. When set, XOAUTH2 is used instead of <see cref="Password"/>.
        /// </summary>
        public IAccessTokenProvider? AccessTokenProvider { get; set; }

        /// <summary>
        /// Timeout for each network operation. Defaults to 30 seconds.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Replaces server certificate validation. Only the offline self-test sets it.
        /// </summary>
        internal RemoteCertificateValidationCallback? CertificateValidation { get; set; }
    }
}
