using System;
using System.Collections.Generic;

namespace LMail
{
    /// <summary>
    /// OAuth2 client configuration shared by token exchange, refresh, and client-credentials flows.
    /// </summary>
    public sealed class OAuth2ClientOptions
    {
        /// <summary>
        /// Authorization endpoint used by <see cref="OAuth2AuthorizationRequest"/>. Optional for
        /// refresh-only or client-credentials usage.
        /// </summary>
        public string? AuthorizationEndpoint { get; set; }

        /// <summary>
        /// Token endpoint. Required.
        /// </summary>
        public string TokenEndpoint { get; set; } = string.Empty;

        /// <summary>
        /// Client ID. Required.
        /// </summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// Client secret. Leave empty for public clients that use PKCE.
        /// </summary>
        public string? ClientSecret { get; set; }

        /// <summary>
        /// Redirect URI registered with the provider.
        /// </summary>
        public string? RedirectUri { get; set; }

        /// <summary>
        /// Space-separated scope for authorization-code and client-credentials requests.
        /// </summary>
        public string? Scope { get; set; }

        /// <summary>
        /// Additional parameters added to the authorization URL, for example
        /// <c>access_type=offline</c> or <c>prompt=consent</c>.
        /// </summary>
        public IDictionary<string, string> ExtraAuthorizationParameters { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Additional parameters added to token requests, for example an audience.
        /// </summary>
        public IDictionary<string, string> ExtraTokenParameters { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
