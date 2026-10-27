using System;

namespace LMail
{
    /// <summary>
    /// OAuth2 token response. <see cref="ExpiresAt"/> is an absolute UTC time.
    /// </summary>
    public sealed class OAuth2Token
    {
        /// <summary>
        /// Creates a token.
        /// </summary>
        public OAuth2Token(
            string accessToken,
            string? refreshToken = null,
            DateTimeOffset? expiresAt = null,
            string? tokenType = null,
            string? scope = null)
        {
            if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(refreshToken))
            {
                throw new ArgumentException(
                    "A token must contain an access token or a refresh token.",
                    nameof(accessToken));
            }

            AccessToken = accessToken ?? string.Empty;
            RefreshToken = refreshToken;
            ExpiresAt = expiresAt;
            TokenType = tokenType;
            Scope = scope;
        }

        /// <summary>
        /// Creates a token containing only a refresh token, for example after restarting a service.
        /// </summary>
        public static OAuth2Token FromRefreshToken(string refreshToken, DateTimeOffset? expiresAt = null)
        {
            return new OAuth2Token(string.Empty, refreshToken, expiresAt);
        }

        /// <summary>
        /// Bearer access token.
        /// </summary>
        public string AccessToken { get; }

        /// <summary>
        /// Refresh token, or <c>null</c> when the provider did not issue one.
        /// </summary>
        public string? RefreshToken { get; }

        /// <summary>
        /// Absolute expiry time, or <c>null</c> when the provider did not specify one.
        /// </summary>
        public DateTimeOffset? ExpiresAt { get; }

        /// <summary>
        /// Token type, usually <c>Bearer</c>.
        /// </summary>
        public string? TokenType { get; }

        /// <summary>
        /// Granted scopes, space separated.
        /// </summary>
        public string? Scope { get; }

        /// <summary>
        /// Returns <c>true</c> when the token is expired or expires within <paramref name="window"/>.
        /// </summary>
        public bool IsExpired(DateTimeOffset now, TimeSpan window)
        {
            return ExpiresAt.HasValue && ExpiresAt.Value <= now.Add(window);
        }
    }

    /// <summary>
    /// Raised when a refresh response contains a new refresh token, which must be persisted
    /// because the old token may no longer be valid.
    /// </summary>
    public sealed class OAuth2RefreshTokenUpdatedEventArgs : EventArgs
    {
        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        public OAuth2RefreshTokenUpdatedEventArgs(string? oldRefreshToken, string newRefreshToken)
        {
            OldRefreshToken = oldRefreshToken;
            NewRefreshToken = newRefreshToken;
        }

        /// <summary>
        /// Refresh token that was used for the request, or <c>null</c>.
        /// </summary>
        public string? OldRefreshToken { get; }

        /// <summary>
        /// New refresh token. Persist this value before the next send.
        /// </summary>
        public string NewRefreshToken { get; }
    }
}
