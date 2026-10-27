using System;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Net.Mail;

namespace LMail.Internal
{
    internal static class EmailValidator
    {
        // Header lines must stay under 998 characters; unbreakable values get this much room.
        private const int MaxHeaderValueLength = 900;
        private const int MaxSubjectLength = MaxHeaderValueLength;
        private const int MaxAddressLength = 254;
        private const int MaxDisplayNameLength = 255;

        private static readonly HashSet<string> ReservedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Bcc",
            "Cc",
            "Content-Transfer-Encoding",
            "Content-Type",
            "Date",
            "From",
            "MIME-Version",
            "Message-ID",
            "Reply-To",
            "Return-Path",
            "Sender",
            "Subject",
            "To"
        };

        public static void Validate(EmailMessage? message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (message.From == null)
            {
                throw new EmailValidationException("From is required.");
            }

            ValidateAddress(message.From, "From");

            if (message.To.Count == 0)
            {
                throw new EmailValidationException("At least one To recipient is required.");
            }

            ValidateAddresses(message.To, "To");
            ValidateAddresses(message.Cc, "Cc");
            ValidateAddresses(message.Bcc, "Bcc");
            ValidateAddresses(message.ReplyTo, "ReplyTo");

            if (string.IsNullOrWhiteSpace(message.Subject))
            {
                throw new EmailValidationException("Subject is required.");
            }

            if (message.Subject!.Length > MaxSubjectLength)
            {
                throw new EmailValidationException(
                    "Subject must not exceed " + MaxSubjectLength + " characters.");
            }

            if (ContainsControl(message.Subject))
            {
                throw new EmailValidationException("Subject must not contain control characters.");
            }

            if (string.IsNullOrEmpty(message.HtmlBody) && string.IsNullOrEmpty(message.TextBody))
            {
                throw new EmailValidationException("Either HtmlBody or TextBody is required.");
            }

            foreach (var attachment in message.Attachments)
            {
                ValidateAttachment(attachment);
            }

            foreach (var header in message.Headers)
            {
                ValidateHeader(header.Key, header.Value);
            }
        }

        private static void ValidateAddresses(IEnumerable<EmailAddress> addresses, string field)
        {
            foreach (var address in addresses)
            {
                ValidateAddress(address, field);
            }
        }

        private static void ValidateAddress(EmailAddress address, string field)
        {
            if (address == null)
            {
                throw new EmailValidationException(field + " contains a null address.");
            }

            if (string.IsNullOrWhiteSpace(address.Address))
            {
                throw new EmailValidationException(field + " contains an empty address.");
            }

            if (address.Address.Length > MaxAddressLength ||
                (address.DisplayName != null && address.DisplayName.Length > MaxDisplayNameLength))
            {
                throw new EmailValidationException(field + " contains an address or display name that is too long.");
            }

            if (ContainsControl(address.Address))
            {
                throw new EmailValidationException(field + " contains an address with a control character.");
            }

            try
            {
                var parsed = new MailAddress(address.Address);
                if (!string.Equals(parsed.Address, address.Address, StringComparison.OrdinalIgnoreCase))
                {
                    throw new EmailValidationException(
                        field + " contains an invalid mailbox address: " + address.Address);
                }
            }
            catch (FormatException)
            {
                throw new EmailValidationException(
                    field + " contains an invalid mailbox address: " + address.Address);
            }

            var at = address.Address.LastIndexOf('@');
            if (!AddressText.IsAscii(address.Address.Substring(0, at)))
            {
                throw new EmailValidationException(
                    field + " contains an address with a non-ASCII local part: " + address.Address);
            }

            try
            {
                AddressText.ToAscii(address.Address);
            }
            catch (ArgumentException)
            {
                throw new EmailValidationException(
                    field + " contains an invalid domain name: " + address.Address);
            }

            if (address.DisplayName != null && ContainsControl(address.DisplayName))
            {
                throw new EmailValidationException(field + " contains a display name with a control character.");
            }
        }

        private static void ValidateAttachment(EmailAttachment attachment)
        {
            if (attachment == null)
            {
                throw new EmailValidationException("Attachments contains a null entry.");
            }

            if (string.IsNullOrWhiteSpace(attachment.FileName))
            {
                throw new EmailValidationException("Attachment file names must not be empty.");
            }

            if (ContainsControl(attachment.FileName))
            {
                throw new EmailValidationException(
                    "Attachment file name contains an invalid character: " + attachment.FileName);
            }

            if (MimeHeaders.Parameter("filename", attachment.FileName).Length > MaxHeaderValueLength)
            {
                throw new EmailValidationException(
                    "Attachment file name is too long: " + attachment.FileName);
            }

            if (ContainsControl(attachment.ContentType) ||
                !AddressText.IsAscii(attachment.ContentType) ||
                !MediaTypeHeaderValue.TryParse(attachment.ContentType, out _))
            {
                throw new EmailValidationException(
                    "Attachment '" + attachment.FileName + "' has an invalid content type: " + attachment.ContentType);
            }

            if (attachment.ContentId != null)
            {
                var contentId = PayloadHelpers.NormalizeContentId(attachment.ContentId);
                if (contentId.Length == 0 || ContainsInvalidContentIdCharacter(contentId))
                {
                    throw new EmailValidationException(
                        "Attachment '" + attachment.FileName + "' has an invalid content ID: " + attachment.ContentId);
                }
            }
        }

        private static void ValidateHeader(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new EmailValidationException("Header names must not be empty.");
            }

            if (ReservedHeaders.Contains(name))
            {
                throw new EmailValidationException(
                    "Header '" + name + "' is generated from the message and cannot be set through Headers.");
            }

            for (var i = 0; i < name.Length; i++)
            {
                if (!IsHeaderNameCharacter(name[i]))
                {
                    throw new EmailValidationException("Header name contains an invalid character: " + name);
                }
            }

            if (value == null)
            {
                throw new EmailValidationException("Header '" + name + "' has a null value.");
            }

            if (ContainsControl(value))
            {
                throw new EmailValidationException("Header '" + name + "' contains an invalid character.");
            }

            if (name.Length > 64 || value.Length > MaxHeaderValueLength)
            {
                throw new EmailValidationException("Header '" + name + "' is too long.");
            }
        }

        private static bool IsHeaderNameCharacter(char c)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                return true;
            }

            switch (c)
            {
                case '!':
                case '#':
                case '$':
                case '%':
                case '&':
                case '\'':
                case '*':
                case '+':
                case '-':
                case '.':
                case '^':
                case '_':
                case '`':
                case '|':
                case '~':
                    return true;
                default:
                    return false;
            }
        }

        private static bool ContainsInvalidContentIdCharacter(string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c > 126 || char.IsWhiteSpace(c) || c == '<' || c == '>' || c == '"' || char.IsControl(c))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsControl(string value)
        {
            foreach (var c in value)
            {
                if ((c < ' ' && c != '\t') || c == '\u007f')
                {
                    return true;
                }
            }

            return false;
        }
    }
}
