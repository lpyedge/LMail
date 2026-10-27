using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using LMail.Internal;

namespace LMail
{
    /// <summary>
    /// Parameters for the first authorization leg. Send the user to <see cref="AuthorizationUrl"/>
    /// and pass the returned <c>code</c> and <see cref="CodeVerifier"/> to
    /// <see cref="OAuth2TokenClient.ExchangeAuthorizationCodeAsync"/>.
    /// </summary>
    public sealed class OAuth2AuthorizationRequest
    {
        internal OAuth2AuthorizationRequest(string authorizationUrl, string state, string codeVerifier)
        {
            AuthorizationUrl = authorizationUrl;
            State = state;
            CodeVerifier = codeVerifier;
        }

        /// <summary>
        /// URL to open in a browser.
        /// </summary>
        public string AuthorizationUrl { get; }

        /// <summary>
        /// Opaque state value. Verify that the callback returns the same value.
        /// </summary>
        public string State { get; }

        /// <summary>
        /// PKCE code verifier. Pass it to the token exchange for public clients.
        /// </summary>
        public string CodeVerifier { get; }

        /// <summary>
        /// Builds an authorization URL with PKCE (S256) and a random state value.
        /// </summary>
        public static OAuth2AuthorizationRequest Create(OAuth2ClientOptions options, string? state = null)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.AuthorizationEndpoint))
            {
                throw new ArgumentException("AuthorizationEndpoint is required.", nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.ClientId))
            {
                throw new ArgumentException("ClientId is required.", nameof(options));
            }

            var codeVerifier = Base64Url(RandomBytes(32));
            byte[] challengeBytes;
            using (var sha = SHA256.Create())
            {
                challengeBytes = sha.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
            }

            var codeChallenge = Base64Url(challengeBytes);
            var stateValue = string.IsNullOrEmpty(state) ? Base64Url(RandomBytes(16)) : state!;

            var parameters = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("response_type", "code"),
                new KeyValuePair<string, string>("client_id", options.ClientId),
                new KeyValuePair<string, string>("code_challenge", codeChallenge),
                new KeyValuePair<string, string>("code_challenge_method", "S256"),
                new KeyValuePair<string, string>("state", stateValue)
            };

            if (!string.IsNullOrWhiteSpace(options.RedirectUri))
            {
                parameters.Add(new KeyValuePair<string, string>("redirect_uri", options.RedirectUri!));
            }

            if (!string.IsNullOrWhiteSpace(options.Scope))
            {
                parameters.Add(new KeyValuePair<string, string>("scope", options.Scope!));
            }

            foreach (var item in options.ExtraAuthorizationParameters)
            {
                parameters.Add(new KeyValuePair<string, string>(item.Key, item.Value));
            }

            return new OAuth2AuthorizationRequest(
                AppendQuery(options.AuthorizationEndpoint!, parameters),
                stateValue,
                codeVerifier);
        }

        internal static string AppendQuery(string url, IEnumerable<KeyValuePair<string, string>> parameters)
        {
            var builder = new StringBuilder(url);
            var separator = url.IndexOf('?') >= 0 ? '&' : '?';

            foreach (var parameter in parameters)
            {
                builder.Append(separator);
                builder.Append(Uri.EscapeDataString(parameter.Key));
                builder.Append('=');
                builder.Append(Uri.EscapeDataString(parameter.Value));
                separator = '&';
            }

            return builder.ToString();
        }

        internal static string Base64Url(byte[] value)
        {
            return Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        internal static byte[] RandomBytes(int length)
        {
            var bytes = new byte[length];

            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }

            return bytes;
        }
    }
}
