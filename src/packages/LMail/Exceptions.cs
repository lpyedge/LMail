using System;

namespace LMail
{
    /// <summary>
    /// Base exception for LMail failures.
    /// </summary>
    public class LMailException : Exception
    {
        /// <summary>
        /// Creates an exception.
        /// </summary>
        public LMailException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Creates an exception with an inner exception.
        /// </summary>
        public LMailException(string message, Exception? innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// The message or sender configuration is invalid. No network request was made.
    /// </summary>
    public sealed class EmailValidationException : LMailException
    {
        /// <summary>
        /// Creates an exception.
        /// </summary>
        public EmailValidationException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// OAuth2 authorization, token exchange, or refresh failed.
    /// </summary>
    public sealed class OAuth2Exception : LMailException
    {
        /// <summary>
        /// Creates an exception.
        /// </summary>
        /// <param name="message">Human-readable failure description.</param>
        /// <param name="error">OAuth2 error code, for example <c>invalid_grant</c>.</param>
        /// <param name="errorDescription">Provider error description.</param>
        /// <param name="statusCode">HTTP status code when the provider returned one.</param>
        /// <param name="innerException">Optional transport exception.</param>
        public OAuth2Exception(
            string message,
            string? error = null,
            string? errorDescription = null,
            int? statusCode = null,
            Exception? innerException = null)
            : base(message, innerException)
        {
            Error = error;
            ErrorDescription = errorDescription;
            StatusCode = statusCode;
        }

        /// <summary>
        /// OAuth2 error code, or <c>null</c>.
        /// </summary>
        public string? Error { get; }

        /// <summary>
        /// Provider error description, or <c>null</c>.
        /// </summary>
        public string? ErrorDescription { get; }

        /// <summary>
        /// HTTP status code, or <c>null</c>.
        /// </summary>
        public int? StatusCode { get; }
    }

    /// <summary>
    /// A provider or transport failed to send the message.
    /// </summary>
    public sealed class EmailSendException : LMailException
    {
        /// <summary>
        /// Creates an exception.
        /// </summary>
        /// <param name="message">Human-readable failure description.</param>
        /// <param name="provider">Provider name, for example <c>Brevo</c> or <c>SMTP</c>.</param>
        /// <param name="statusCode">HTTP status code or SMTP reply code when the provider returned one.</param>
        /// <param name="responseBody">Provider response body or SMTP reply text, truncated when very large.</param>
        /// <param name="innerException">Optional transport exception.</param>
        public EmailSendException(
            string message,
            string? provider = null,
            int? statusCode = null,
            string? responseBody = null,
            Exception? innerException = null)
            : base(message, innerException)
        {
            Provider = provider;
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }

        /// <summary>
        /// Provider name, or <c>null</c> when not applicable.
        /// </summary>
        public string? Provider { get; }

        /// <summary>
        /// HTTP status code or SMTP reply code, or <c>null</c> when not applicable.
        /// </summary>
        public int? StatusCode { get; }

        /// <summary>
        /// Provider response body or SMTP reply text, or <c>null</c> when not applicable.
        /// </summary>
        public string? ResponseBody { get; }
    }
}
