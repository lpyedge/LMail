using System;
using System.Threading;
using System.Threading.Tasks;

namespace LMail
{
    /// <summary>
    /// Supplies an OAuth2 access token for SMTP XOAUTH2 and HTTP providers.
    /// </summary>
    public interface IAccessTokenProvider
    {
        /// <summary>
        /// Returns a valid access token, refreshing or acquiring one when necessary.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Returns a fixed access token. Useful for short-lived tokens acquired elsewhere.
    /// </summary>
    public sealed class StaticAccessTokenProvider : IAccessTokenProvider
    {
        private readonly string _accessToken;

        /// <summary>
        /// Creates a provider from an existing access token.
        /// </summary>
        public StaticAccessTokenProvider(string accessToken)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new ArgumentException("Access token must not be empty.", nameof(accessToken));
            }

            _accessToken = accessToken;
        }

        /// <inheritdoc />
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_accessToken);
        }
    }
}
