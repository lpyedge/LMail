using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LMail.Internal;

namespace LMail
{
    /// <summary>
    /// Exchanges authorization codes, refreshes tokens, and acquires client-credentials tokens.
    /// The client is stateless; caching is handled by <see cref="OAuth2TokenProvider"/>.
    /// </summary>
    public sealed class OAuth2TokenClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _tokenEndpoint;
        private readonly string _clientId;
        private readonly string? _clientSecret;
        private readonly string? _redirectUri;
        private readonly string? _scope;
        private readonly List<KeyValuePair<string, string>> _extraParameters;

        /// <summary>
        /// Creates a token client.
        /// </summary>
        /// <param name="options">Client configuration. Values are copied at construction.</param>
        /// <param name="httpClient">Optional HTTP client. A shared client is used when omitted.</param>
        public OAuth2TokenClient(OAuth2ClientOptions options, HttpClient? httpClient = null)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.TokenEndpoint))
            {
                throw new ArgumentException("TokenEndpoint is required.", nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.ClientId))
            {
                throw new ArgumentException("ClientId is required.", nameof(options));
            }

            _httpClient = ProviderHttp.Resolve(httpClient);
            _tokenEndpoint = options.TokenEndpoint;
            _clientId = options.ClientId;
            _clientSecret = options.ClientSecret;
            _redirectUri = options.RedirectUri;
            _scope = options.Scope;
            _extraParameters = new List<KeyValuePair<string, string>>(options.ExtraTokenParameters);
        }

        /// <summary>
        /// Exchanges an authorization code for tokens. Pass the PKCE verifier created by
        /// <see cref="OAuth2AuthorizationRequest.Create"/> for public clients.
        /// </summary>
        public Task<OAuth2Token> ExchangeAuthorizationCodeAsync(
            string code,
            string? codeVerifier = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new ArgumentException("Authorization code must not be empty.", nameof(code));
            }

            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", code)
            };

            if (!string.IsNullOrWhiteSpace(_redirectUri))
            {
                form.Add(new KeyValuePair<string, string>("redirect_uri", _redirectUri!));
            }

            if (!string.IsNullOrWhiteSpace(codeVerifier))
            {
                form.Add(new KeyValuePair<string, string>("code_verifier", codeVerifier!));
            }

            return RequestTokenAsync(form, cancellationToken);
        }

        /// <summary>
        /// Refreshes an access token. The response may contain a rotated refresh token; providers
        /// such as Microsoft and Google can return a new value on every refresh.
        /// </summary>
        public Task<OAuth2Token> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                throw new ArgumentException("Refresh token must not be empty.", nameof(refreshToken));
            }

            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", refreshToken)
            };

            return RequestTokenAsync(form, cancellationToken);
        }

        /// <summary>
        /// Acquires a token with the client-credentials flow. This flow has no user context and
        /// therefore no refresh token.
        /// </summary>
        public Task<OAuth2Token> RequestClientCredentialsAsync(CancellationToken cancellationToken = default)
        {
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            };

            if (!string.IsNullOrWhiteSpace(_scope))
            {
                form.Add(new KeyValuePair<string, string>("scope", _scope!));
            }

            return RequestTokenAsync(form, cancellationToken);
        }

        private async Task<OAuth2Token> RequestTokenAsync(
            List<KeyValuePair<string, string>> form,
            CancellationToken cancellationToken)
        {
            form.Add(new KeyValuePair<string, string>("client_id", _clientId));

            if (!string.IsNullOrEmpty(_clientSecret))
            {
                form.Add(new KeyValuePair<string, string>("client_secret", _clientSecret!));
            }

            foreach (var item in _extraParameters)
            {
                form.Add(new KeyValuePair<string, string>(item.Key, item.Value));
            }

            using (var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint))
            {
                request.Content = new FormUrlEncodedContent(form);

                string body;
                int statusCode;

                try
                {
                    using (var response = await ProviderHttp
                        .SendAsync(_httpClient, "OAuth2", request, null, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        statusCode = (int)response.StatusCode;
                        body = response.Content == null
                            ? string.Empty
                            : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
                catch (EmailSendException ex)
                {
                    throw new OAuth2Exception(
                        "OAuth2 token request failed: " + ex.Message,
                        null,
                        null,
                        ex.StatusCode,
                        ex);
                }

                using (var document = ParseJson(body, statusCode))
                {
                    var root = document.RootElement;

                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        throw new OAuth2Exception(
                            "OAuth2 token endpoint returned an unexpected JSON value.",
                            null,
                            ProviderHttp.Truncate(body),
                            statusCode);
                    }

                    var error = GetString(root, "error");
                    var errorDescription = GetString(root, "error_description");

                    if (statusCode < 200 || statusCode > 299 || !string.IsNullOrEmpty(error))
                    {
                        var message = string.IsNullOrEmpty(errorDescription)
                            ? "OAuth2 token request failed with HTTP " + statusCode + "."
                            : "OAuth2 token request failed: " + errorDescription;

                        throw new OAuth2Exception(message, error, errorDescription, statusCode);
                    }

                    var accessToken = GetString(root, "access_token");
                    if (string.IsNullOrWhiteSpace(accessToken))
                    {
                        throw new OAuth2Exception(
                            "OAuth2 token response did not contain an access token.",
                            null,
                            ProviderHttp.Truncate(body),
                            statusCode);
                    }

                    DateTimeOffset? expiresAt = null;
                    if (root.TryGetProperty("expires_in", out var expiresElement))
                    {
                        double seconds = 0;
                        var parsed = expiresElement.ValueKind == JsonValueKind.Number
                            ? expiresElement.TryGetDouble(out seconds)
                            : expiresElement.ValueKind == JsonValueKind.String && double.TryParse(
                                expiresElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
                        var now = DateTimeOffset.UtcNow;
                        if (!parsed || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0 ||
                            seconds >= (DateTimeOffset.MaxValue - now).TotalSeconds)
                        {
                            throw new OAuth2Exception("OAuth2 token response contains an invalid expires_in.",
                                statusCode: statusCode);
                        }

                        expiresAt = now.AddSeconds(seconds);
                    }

                    return new OAuth2Token(
                        accessToken!,
                        GetString(root, "refresh_token"),
                        expiresAt,
                        GetString(root, "token_type"),
                        GetString(root, "scope"));
                }
            }
        }

        private static JsonDocument ParseJson(string body, int statusCode)
        {
            try
            {
                return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            }
            catch (JsonException ex)
            {
                throw new OAuth2Exception(
                    "OAuth2 token endpoint returned a non-JSON response: " + ProviderHttp.Truncate(body),
                    null,
                    null,
                    statusCode,
                    ex);
            }
        }

        private static string? GetString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return element.GetString();
        }
    }
}
