using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace LMail.Internal
{
    /// <summary>
    /// Minimal AWS Signature Version 4 signer for a JSON POST request with no query string.
    /// </summary>
    internal static class AwsSigV4Signer
    {
        public static void Sign(
            HttpRequestMessage request,
            byte[] payload,
            string accessKeyId,
            string secretAccessKey,
            string? sessionToken,
            string region,
            string service,
            DateTimeOffset timestamp)
        {
            if (request.RequestUri == null)
            {
                throw new ArgumentException("Request URI is required.", nameof(request));
            }

            var uri = request.RequestUri;
            var canonicalUri = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
            var canonicalQuery = uri.Query.Length > 0 ? uri.Query.Substring(1) : string.Empty;

            var amzDate = timestamp.UtcDateTime.ToString(
                "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture);
            var dateStamp = timestamp.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

            var host = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);

            var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["host"] = host,
                ["x-amz-date"] = amzDate
            };

            var contentType = request.Content?.Headers.ContentType?.ToString();
            if (!string.IsNullOrEmpty(contentType))
            {
                headers["content-type"] = contentType!;
            }

            if (!string.IsNullOrEmpty(sessionToken))
            {
                headers["x-amz-security-token"] = sessionToken!;
            }

            var canonicalHeaders = new StringBuilder();
            foreach (var header in headers)
            {
                canonicalHeaders.Append(header.Key);
                canonicalHeaders.Append(':');
                canonicalHeaders.Append(CollapseWhitespace(header.Value));
                canonicalHeaders.Append('\n');
            }

            var signedHeaders = string.Join(";", headers.Keys);
            var payloadHash = ToHex(Sha256(payload));

            var canonicalRequest = request.Method.Method + "\n" +
                                   canonicalUri + "\n" +
                                   canonicalQuery + "\n" +
                                   canonicalHeaders + "\n" +
                                   signedHeaders + "\n" +
                                   payloadHash;

            var scope = dateStamp + "/" + region + "/" + service + "/aws4_request";
            var stringToSign = "AWS4-HMAC-SHA256\n" +
                               amzDate + "\n" +
                               scope + "\n" +
                               ToHex(Sha256(Encoding.UTF8.GetBytes(canonicalRequest)));

            var dateKey = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), dateStamp);
            var regionKey = Hmac(dateKey, region);
            var serviceKey = Hmac(regionKey, service);
            var signingKey = Hmac(serviceKey, "aws4_request");
            var signature = ToHex(Hmac(signingKey, stringToSign));

            request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);

            if (!string.IsNullOrEmpty(sessionToken))
            {
                request.Headers.TryAddWithoutValidation("x-amz-security-token", sessionToken);
            }

            request.Headers.TryAddWithoutValidation(
                "Authorization",
                "AWS4-HMAC-SHA256 Credential=" + accessKeyId + "/" + scope +
                ", SignedHeaders=" + signedHeaders +
                ", Signature=" + signature);
        }

        private static string CollapseWhitespace(string value)
        {
            var builder = new StringBuilder(value.Length);
            var previousWasSpace = false;

            foreach (var c in value.Trim())
            {
                var isSpace = c == ' ' || c == '\t';
                if (isSpace)
                {
                    if (!previousWasSpace)
                    {
                        builder.Append(' ');
                    }
                }
                else
                {
                    builder.Append(c);
                }

                previousWasSpace = isSpace;
            }

            return builder.ToString();
        }

        private static byte[] Sha256(byte[] value)
        {
            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(value);
            }
        }

        private static byte[] Hmac(byte[] key, string value)
        {
            using (var hmac = new HMACSHA256(key))
            {
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
            }
        }

        private static string ToHex(byte[] value)
        {
            var builder = new StringBuilder(value.Length * 2);

            foreach (var b in value)
            {
                builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }
}
