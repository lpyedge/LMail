using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LMail
{
    /// <summary>
    /// Sends an <see cref="EmailMessage"/> through a concrete provider.
    /// </summary>
    public interface IEmailSender
    {
        /// <summary>
        /// Sends the message.
        /// </summary>
        /// <param name="message">Message to send. It is validated before any network call.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <exception cref="EmailValidationException">The message is not valid for this sender.</exception>
        /// <exception cref="EmailSendException">The provider rejected the message or the request failed.</exception>
        Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// An email address with an optional display name.
    /// </summary>
    public sealed class EmailAddress
    {
        /// <summary>
        /// Creates an address.
        /// </summary>
        /// <param name="address">Mailbox address, for example <c>user@example.com</c>.</param>
        /// <param name="displayName">Optional display name.</param>
        public EmailAddress(string address, string? displayName = null)
        {
            Address = address ?? throw new ArgumentNullException(nameof(address));
            DisplayName = displayName;
        }

        /// <summary>
        /// Mailbox address.
        /// </summary>
        public string Address { get; }

        /// <summary>
        /// Optional display name.
        /// </summary>
        public string? DisplayName { get; }

        /// <summary>
        /// Returns <c>Display Name &lt;address&gt;</c>, or just the address.
        /// </summary>
        public override string ToString()
        {
            return string.IsNullOrEmpty(DisplayName) ? Address : DisplayName + " <" + Address + ">";
        }
    }

    /// <summary>
    /// A file attachment. A non-null <see cref="ContentId"/> marks the attachment as inline.
    /// </summary>
    public sealed class EmailAttachment
    {
        /// <summary>
        /// Creates an attachment from in-memory content.
        /// </summary>
        /// <param name="fileName">File name shown to the recipient.</param>
        /// <param name="content">Raw file bytes.</param>
        /// <param name="contentType">MIME type. Defaults to <c>application/octet-stream</c>.</param>
        /// <param name="contentId">
        /// Content ID used by <c>cid:</c> links in HTML. Set it to make the attachment inline.
        /// </param>
        public EmailAttachment(
            string fileName,
            byte[] content,
            string? contentType = null,
            string? contentId = null)
        {
            FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
            Content = content ?? throw new ArgumentNullException(nameof(content));
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType!;
            ContentId = contentId;
        }

        /// <summary>
        /// File name shown to the recipient.
        /// </summary>
        public string FileName { get; }

        /// <summary>
        /// Raw file bytes.
        /// </summary>
        public byte[] Content { get; }

        /// <summary>
        /// MIME type.
        /// </summary>
        public string ContentType { get; }

        /// <summary>
        /// Content ID for inline attachments, or <c>null</c> for a regular attachment.
        /// </summary>
        public string? ContentId { get; }

        /// <summary>
        /// Creates an attachment by reading a file from disk.
        /// </summary>
        public static EmailAttachment FromFile(
            string path,
            string? contentType = null,
            string? contentId = null)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            return new EmailAttachment(
                System.IO.Path.GetFileName(path),
                System.IO.File.ReadAllBytes(path),
                contentType,
                contentId);
        }
    }

    /// <summary>
    /// A provider-independent email message.
    /// </summary>
    public sealed class EmailMessage
    {
        /// <summary>
        /// Sender. Required.
        /// </summary>
        public EmailAddress? From { get; set; }

        /// <summary>
        /// Primary recipients. At least one recipient is required.
        /// </summary>
        public List<EmailAddress> To { get; } = new List<EmailAddress>();

        /// <summary>
        /// Carbon-copy recipients.
        /// </summary>
        public List<EmailAddress> Cc { get; } = new List<EmailAddress>();

        /// <summary>
        /// Blind carbon-copy recipients.
        /// </summary>
        public List<EmailAddress> Bcc { get; } = new List<EmailAddress>();

        /// <summary>
        /// Reply-to addresses. Providers that support only one address reject additional entries.
        /// </summary>
        public List<EmailAddress> ReplyTo { get; } = new List<EmailAddress>();

        /// <summary>
        /// Subject. Required.
        /// </summary>
        public string? Subject { get; set; }

        /// <summary>
        /// HTML body, or <c>null</c> for a text-only message.
        /// </summary>
        public string? HtmlBody { get; set; }

        /// <summary>
        /// Plain-text body, or <c>null</c> for an HTML-only message.
        /// </summary>
        public string? TextBody { get; set; }

        /// <summary>
        /// Attachments, including inline parts.
        /// </summary>
        public List<EmailAttachment> Attachments { get; } = new List<EmailAttachment>();

        /// <summary>
        /// Custom message headers. Standard headers such as <c>From</c>, <c>To</c>, and <c>Subject</c>
        /// are rejected because they are generated from the message model.
        /// </summary>
        public IDictionary<string, string> Headers { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
