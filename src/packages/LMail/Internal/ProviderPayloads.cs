using System;
using System.Collections.Generic;

namespace LMail.Internal
{
    internal static class ProviderPayloads
    {
        public static Dictionary<string, object?> New()
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        public static Dictionary<string, object?> Address(
            EmailAddress address,
            string emailProperty = "email",
            string nameProperty = "name")
        {
            var payload = New();
            payload[emailProperty] = address.Address;

            if (!string.IsNullOrEmpty(address.DisplayName))
            {
                payload[nameProperty] = address.DisplayName;
            }

            return payload;
        }

        public static List<Dictionary<string, object?>> AddressList(
            IEnumerable<EmailAddress> addresses,
            string emailProperty = "email",
            string nameProperty = "name")
        {
            var list = new List<Dictionary<string, object?>>();

            foreach (var address in addresses)
            {
                list.Add(Address(address, emailProperty, nameProperty));
            }

            return list;
        }

        public static List<string> AddressStrings(IEnumerable<EmailAddress> addresses)
        {
            var list = new List<string>();

            foreach (var address in addresses)
            {
                list.Add(address.Address);
            }

            return list;
        }

        public static List<string> MailboxStrings(IEnumerable<EmailAddress> addresses)
        {
            var list = new List<string>();

            foreach (var address in addresses)
            {
                list.Add(AddressText.Mailbox(address));
            }

            return list;
        }

        public static void AddCopies(
            Dictionary<string, object?> payload,
            EmailMessage message,
            Func<IEnumerable<EmailAddress>, object> format)
        {
            if (message.Cc.Count > 0)
            {
                payload["cc"] = format(message.Cc);
            }

            if (message.Bcc.Count > 0)
            {
                payload["bcc"] = format(message.Bcc);
            }
        }

        public static void AddHeaders(Dictionary<string, object?> payload, EmailMessage message)
        {
            var headers = Headers(message.Headers);
            if (headers != null)
            {
                payload["headers"] = headers;
            }
        }

        private static Dictionary<string, string>? Headers(IEnumerable<KeyValuePair<string, string>> headers)
        {
            Dictionary<string, string>? result = null;

            foreach (var header in headers)
            {
                if (result == null)
                {
                    result = new Dictionary<string, string>(StringComparer.Ordinal);
                }

                result[header.Key] = header.Value;
            }

            return result;
        }

        public static List<Dictionary<string, object?>> Attachments(EmailMessage message)
        {
            var list = new List<Dictionary<string, object?>>();

            foreach (var attachment in message.Attachments)
            {
                var item = New();
                item["content"] = Convert.ToBase64String(attachment.Content);
                item["filename"] = attachment.FileName;
                item["type"] = attachment.ContentType;

                item["disposition"] = attachment.ContentId == null ? "attachment" : "inline";

                if (attachment.ContentId != null)
                {
                    item["content_id"] = PayloadHelpers.NormalizeContentId(attachment.ContentId);
                }

                list.Add(item);
            }

            return list;
        }
    }
}
