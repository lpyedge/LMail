using System;
using System.Threading;
using System.Threading.Tasks;

namespace LMail
{
    /// <summary>
    /// Caches an OAuth2 token in memory and refreshes it shortly before expiry, or on the first
    /// request after an access token has expired. Refreshes are serialized with a semaphore so
    /// concurrent sends use a single token request.
    /// </summary>
    public sealed class OAuth2TokenProvider : IAccessTokenProvider, IDisposable
    {
        private readonly OAuth2TokenClient _client;
        private readonly bool _clientCredentials;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private OAuth2Token? _token;
        private bool _disposed;

        /// <summary>
        /// Creates a provider from an existing token or refresh token.
        /// </summary>
        /// <param name="client">Token client used for refreshes.</param>
        /// <param name="initialToken">
        /// Token that contains an access token, a refresh token, or both. When the access token is
        /// missing or expired, <see cref="OAuth2Token.RefreshToken"/> is used.
        /// </param>
        public OAuth2TokenProvider(OAuth2TokenClient client, OAuth2Token initialToken)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));

            if (initialToken == null)
            {
                throw new ArgumentNullException(nameof(initialToken));
            }

            if (string.IsNullOrEmpty(initialToken.RefreshToken) && string.IsNullOrEmpty(initialToken.AccessToken))
            {
                throw new ArgumentException("The initial token must contain an access or refresh token.", nameof(initialToken));
            }

            _token = initialToken;
        }

        private OAuth2TokenProvider(OAuth2TokenClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _clientCredentials = true;
        }

        /// <summary>
        /// Creates a provider that acquires tokens with the client-credentials flow and caches them
        /// until they approach expiry.
        /// </summary>
        public static OAuth2TokenProvider CreateClientCredentials(OAuth2TokenClient client)
        {
            return new OAuth2TokenProvider(client);
        }

        /// <summary>
        /// How long before expiry a token is refreshed. Defaults to five minutes.
        /// </summary>
        public TimeSpan RefreshAhead { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Current cached token, or <c>null</c> before the first client-credentials request.
        /// </summary>
        public OAuth2Token? CurrentToken
        {
            get { return Volatile.Read(ref _token); }
        }

        /// <summary>
        /// Raised when a refresh response contains a different refresh token. Persist the new value
        /// before continuing, because the provider may invalidate the old token.
        /// </summary>
        public event EventHandler<OAuth2RefreshTokenUpdatedEventArgs>? RefreshTokenUpdated;

        /// <inheritdoc />
        public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var token = Volatile.Read(ref _token);
            if (IsUsable(token))
            {
                return token!.AccessToken;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            string accessToken;
            OAuth2RefreshTokenUpdatedEventArgs? update = null;

            try
            {
                ThrowIfDisposed();

                token = _token;
                if (IsUsable(token))
                {
                    accessToken = token!.AccessToken;
                }
                else
                {
                    OAuth2Token refreshed;

                    if (_clientCredentials)
                    {
                        refreshed = await _client
                            .RequestClientCredentialsAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(token?.RefreshToken))
                        {
                            throw new OAuth2Exception(
                                "The access token has expired and no refresh token is available.");
                        }

                        refreshed = await _client
                            .RefreshAsync(token!.RefreshToken!, cancellationToken)
                            .ConfigureAwait(false);

                        if (string.IsNullOrEmpty(refreshed.RefreshToken))
                        {
                            refreshed = new OAuth2Token(
                                refreshed.AccessToken,
                                token.RefreshToken,
                                refreshed.ExpiresAt,
                                refreshed.TokenType,
                                refreshed.Scope);
                        }
                    }

                    var previousRefreshToken = token?.RefreshToken;
                    Volatile.Write(ref _token, refreshed);
                    accessToken = refreshed.AccessToken;

                    if (!string.Equals(previousRefreshToken, refreshed.RefreshToken, StringComparison.Ordinal))
                    {
                        update = new OAuth2RefreshTokenUpdatedEventArgs(
                            previousRefreshToken,
                            refreshed.RefreshToken!);
                    }
                }
            }
            finally
            {
                _gate.Release();
            }

            if (update != null)
            {
                RefreshTokenUpdated?.Invoke(this, update);
            }

            return accessToken;
        }

        /// <summary>
        /// Disposes the internal semaphore.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _gate.Dispose();
        }

        private bool IsUsable(OAuth2Token? token)
        {
            return token != null &&
                   !string.IsNullOrEmpty(token.AccessToken) &&
                   !token.IsExpired(DateTimeOffset.UtcNow,
                       _clientCredentials || !string.IsNullOrEmpty(token.RefreshToken) ? RefreshAhead : TimeSpan.Zero);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(OAuth2TokenProvider));
            }
        }
    }
}
