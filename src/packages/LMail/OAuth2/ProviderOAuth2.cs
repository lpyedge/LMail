using System;

namespace LMail
{
    /// <summary>
    /// Microsoft identity platform endpoints and scopes.
    /// </summary>
    public static class MicrosoftOAuth2
    {
        /// <summary>
        /// Scope for SMTP XOAUTH2 with a delegated user.
        /// </summary>
        public const string SmtpScope = "https://outlook.office365.com/SMTP.Send offline_access";

        /// <summary>
        /// Scope for Microsoft Graph mail sending with a delegated user.
        /// </summary>
        public const string GraphScope = "https://graph.microsoft.com/Mail.Send offline_access";

        /// <summary>
        /// Client-credentials scope for application SMTP authentication.
        /// </summary>
        public const string SmtpAppScope = "https://outlook.office365.com/.default";

        /// <summary>
        /// Client-credentials scope for Microsoft Graph application permissions.
        /// </summary>
        public const string GraphAppScope = "https://graph.microsoft.com/.default";

        /// <summary>
        /// Creates options for SMTP XOAUTH2 authorization-code or refresh-token flows.
        /// </summary>
        public static OAuth2ClientOptions CreateSmtpOptions(
            string clientId,
            string? clientSecret,
            string? redirectUri = null,
            string tenantId = "common")
        {
            return CreateOptions(clientId, clientSecret, SmtpScope, redirectUri, tenantId);
        }

        /// <summary>
        /// Creates options for Microsoft Graph delegated sending.
        /// </summary>
        public static OAuth2ClientOptions CreateGraphOptions(
            string clientId,
            string? clientSecret,
            string? redirectUri = null,
            string tenantId = "common")
        {
            return CreateOptions(clientId, clientSecret, GraphScope, redirectUri, tenantId);
        }

        /// <summary>
        /// Creates options for the client-credentials flow with SMTP application permissions.
        /// </summary>
        public static OAuth2ClientOptions CreateSmtpClientCredentialsOptions(
            string clientId,
            string clientSecret,
            string tenantId)
        {
            return CreateOptions(clientId, clientSecret, SmtpAppScope, null, tenantId);
        }

        /// <summary>
        /// Creates options for the client-credentials flow with Graph application permissions.
        /// </summary>
        public static OAuth2ClientOptions CreateGraphClientCredentialsOptions(
            string clientId,
            string clientSecret,
            string tenantId)
        {
            return CreateOptions(clientId, clientSecret, GraphAppScope, null, tenantId);
        }

        private static OAuth2ClientOptions CreateOptions(
            string clientId,
            string? clientSecret,
            string scope,
            string? redirectUri,
            string tenantId)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw new ArgumentException("Client ID must not be empty.", nameof(clientId));
            }

            if (string.IsNullOrWhiteSpace(tenantId))
            {
                throw new ArgumentException("Tenant ID must not be empty.", nameof(tenantId));
            }

            var baseUrl = "https://login.microsoftonline.com/" + Uri.EscapeDataString(tenantId) + "/oauth2/v2.0";

            return new OAuth2ClientOptions
            {
                AuthorizationEndpoint = baseUrl + "/authorize",
                TokenEndpoint = baseUrl + "/token",
                ClientId = clientId,
                ClientSecret = clientSecret,
                RedirectUri = redirectUri,
                Scope = scope
            };
        }
    }

    /// <summary>
    /// Google identity endpoints and scopes.
    /// </summary>
    public static class GoogleOAuth2
    {
        /// <summary>
        /// Scope required by Gmail SMTP XOAUTH2.
        /// </summary>
        public const string GmailScope = "https://mail.google.com/";

        /// <summary>
        /// Creates options for Gmail SMTP OAuth2. Refresh tokens are requested with
        /// <c>access_type=offline</c> and <c>prompt=consent</c>.
        /// </summary>
        public static OAuth2ClientOptions CreateOptions(
            string clientId,
            string? clientSecret,
            string? redirectUri = null,
            string scope = GmailScope)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw new ArgumentException("Client ID must not be empty.", nameof(clientId));
            }

            var options = new OAuth2ClientOptions
            {
                AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth",
                TokenEndpoint = "https://oauth2.googleapis.com/token",
                ClientId = clientId,
                ClientSecret = clientSecret,
                RedirectUri = redirectUri,
                Scope = scope
            };

            options.ExtraAuthorizationParameters["access_type"] = "offline";
            options.ExtraAuthorizationParameters["prompt"] = "consent";

            return options;
        }
    }
}
