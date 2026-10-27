using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http.Headers;
using System.Text;

namespace LMail.Internal
{
    /// <summary>
    /// Builds an RFC 5322 / MIME message as 7-bit ASCII bytes with CRLF line endings.
    /// Non-ASCII text is encoded: headers as encoded words, bodies as base64.
    /// </summary>
    internal static class MimeMessageFactory
    {
        private const int Base64LineBytes = 57;
        private const int MaxSevenBitLineLength = 998;

        public static byte[] Create(EmailMessage message, bool includeBcc)
        {
            EmailValidator.Validate(message);

            using (var stream = new MemoryStream())
            {
                using (var writer = new StreamWriter(stream, Encoding.ASCII, 4096, leaveOpen: true))
                {
                    WriteHeaders(writer, message, includeBcc);
                    WriteBody(writer, message);
                }

                return stream.ToArray();
            }
        }

        private static void WriteHeaders(TextWriter writer, EmailMessage message, bool includeBcc)
        {
            var date = DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            var from = AddressText.ToAscii(message.From!.Address);
            var domain = from.Substring(from.LastIndexOf('@') + 1);

            writer.Write(MimeHeaders.Format("Date", new[] { date + " +0000" }));
            writer.Write(MimeHeaders.Format("From", MimeHeaders.Mailboxes(new[] { message.From })));
            writer.Write(MimeHeaders.Format("To", MimeHeaders.Mailboxes(message.To)));

            if (message.Cc.Count > 0)
            {
                writer.Write(MimeHeaders.Format("Cc", MimeHeaders.Mailboxes(message.Cc)));
            }

            if (includeBcc && message.Bcc.Count > 0)
            {
                writer.Write(MimeHeaders.Format("Bcc", MimeHeaders.Mailboxes(message.Bcc)));
            }

            if (message.ReplyTo.Count > 0)
            {
                writer.Write(MimeHeaders.Format("Reply-To", MimeHeaders.Mailboxes(message.ReplyTo)));
            }

            writer.Write(MimeHeaders.Format("Subject", MimeHeaders.Unstructured(message.Subject!)));
            writer.Write(MimeHeaders.Format(
                "Message-ID",
                new[] { "<" + Guid.NewGuid().ToString("N") + "@" + domain + ">" }));

            foreach (var header in message.Headers)
            {
                writer.Write(MimeHeaders.Format(header.Key, MimeHeaders.Unstructured(header.Value)));
            }

            writer.Write(MimeHeaders.Format("MIME-Version", new[] { "1.0" }));
        }

        private static void WriteBody(TextWriter writer, EmailMessage message)
        {
            var hasText = !string.IsNullOrEmpty(message.TextBody);
            var hasHtml = !string.IsNullOrEmpty(message.HtmlBody);

            // Inline parts need an HTML body to be linked from; otherwise they stay plain attachments.
            var linked = new List<Action<TextWriter>>();
            var attached = new List<Action<TextWriter>>();

            foreach (var attachment in message.Attachments)
            {
                var item = attachment;
                Action<TextWriter> part = w => WriteAttachment(w, item);

                if (item.ContentId != null && hasHtml)
                {
                    linked.Add(part);
                }
                else
                {
                    attached.Add(part);
                }
            }

            Action<TextWriter> html = w => WriteText(w, "html", message.HtmlBody!);

            if (linked.Count > 0)
            {
                var htmlPart = html;
                linked.Insert(0, htmlPart);
                html = w => WriteMultipart(w, "related", linked);
            }

            Action<TextWriter> main;

            if (hasText && hasHtml)
            {
                main = w => WriteMultipart(w, "alternative", new List<Action<TextWriter>>
                {
                    w2 => WriteText(w2, "plain", message.TextBody!),
                    html
                });
            }
            else if (hasHtml)
            {
                main = html;
            }
            else
            {
                main = w => WriteText(w, "plain", message.TextBody!);
            }

            if (attached.Count > 0)
            {
                var body = main;
                attached.Insert(0, body);
                main = w => WriteMultipart(w, "mixed", attached);
            }

            main(writer);
        }

        private static void WriteMultipart(TextWriter writer, string subtype, List<Action<TextWriter>> parts)
        {
            var boundary = "=_" + Guid.NewGuid().ToString("N");

            writer.Write(MimeHeaders.Format(
                "Content-Type",
                new[] { "multipart/" + subtype + ";", MimeHeaders.Parameter("boundary", boundary) }));
            writer.Write("\r\n");

            foreach (var part in parts)
            {
                writer.Write("--" + boundary + "\r\n");
                part(writer);
            }

            writer.Write("--" + boundary + "--\r\n");
        }

        private static void WriteText(TextWriter writer, string subtype, string text)
        {
            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
            var sevenBit = IsSevenBit(normalized);

            writer.Write(MimeHeaders.Format("Content-Type", new[] { "text/" + subtype + ";", "charset=utf-8" }));
            writer.Write(MimeHeaders.Format("Content-Transfer-Encoding", new[] { sevenBit ? "7bit" : "base64" }));
            writer.Write("\r\n");

            if (sevenBit)
            {
                writer.Write(normalized);
                writer.Write("\r\n");
            }
            else
            {
                WriteBase64(writer, Encoding.UTF8.GetBytes(normalized));
            }
        }

        private static void WriteAttachment(TextWriter writer, EmailAttachment attachment)
        {
            var contentType = new List<string>(
                MediaTypeHeaderValue.Parse(attachment.ContentType).ToString().Split(' '));
            contentType[contentType.Count - 1] += ";";
            contentType.Add(MimeHeaders.Parameter("name", attachment.FileName));

            var inline = attachment.ContentId != null;

            writer.Write(MimeHeaders.Format("Content-Type", contentType));
            writer.Write(MimeHeaders.Format("Content-Transfer-Encoding", new[] { "base64" }));
            writer.Write(MimeHeaders.Format(
                "Content-Disposition",
                new[] { (inline ? "inline" : "attachment") + ";", MimeHeaders.Parameter("filename", attachment.FileName) }));

            if (inline)
            {
                writer.Write(MimeHeaders.Format(
                    "Content-ID",
                    new[] { "<" + PayloadHelpers.NormalizeContentId(attachment.ContentId!) + ">" }));
            }

            writer.Write("\r\n");
            WriteBase64(writer, attachment.Content);
        }

        private static void WriteBase64(TextWriter writer, byte[] content)
        {
            for (var offset = 0; offset < content.Length; offset += Base64LineBytes)
            {
                var count = Math.Min(Base64LineBytes, content.Length - offset);
                writer.Write(Convert.ToBase64String(content, offset, count));
                writer.Write("\r\n");
            }
        }

        private static bool IsSevenBit(string text)
        {
            var lineLength = 0;

            foreach (var c in text)
            {
                if (c > 127 || c == '\0')
                {
                    return false;
                }

                lineLength = c == '\n' ? 0 : lineLength + 1;

                if (lineLength > MaxSevenBitLineLength)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
