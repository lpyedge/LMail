using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LMail.Internal;
using MimeKit;

namespace LMail.Samples
{
    /// <summary>
    /// Parses the library's MIME output with MimeKit as an independent reader.
    /// </summary>
    internal static class MimeCheck
    {
        public static async Task RunAsync()
        {
            CheckUnicodeMultipart();
            CheckLongAsciiSubject();
            CheckTextOnly();
            CheckEncodedWordLookalike();
            await CheckLimitsAsync().ConfigureAwait(false);
        }

        private static void CheckEncodedWordLookalike()
        {
            var message = new EmailMessage
            {
                From = new EmailAddress("sender@example.com", "=?utf-8?B?QQ==?= name"),
                Subject = "=?utf-8?B?QQ==?= literal",
                TextBody = "body"
            };
            message.To.Add(new EmailAddress("to@example.com"));

            var mime = Load(MimeMessageFactory.Create(message, includeBcc: false));
            SelfTest.Assert(mime.Subject == message.Subject, "literal encoded-word text in subject");
            SelfTest.Assert(mime.From.Mailboxes.Single().Name == "=?utf-8?B?QQ==?= name", "literal encoded-word text in display name");
        }

        private static async Task CheckLimitsAsync()
        {
            var sender = new SmtpEmailSender(new SmtpOptions { Host = "127.0.0.1", Security = SmtpSecurity.None });

            EmailMessage Message(Action<EmailMessage> change)
            {
                var message = new EmailMessage { From = new EmailAddress("sender@example.com"), Subject = "s", TextBody = "b" };
                message.To.Add(new EmailAddress("to@example.com"));
                change(message);
                return message;
            }

            await SelfTest.Expect<EmailValidationException>(
                () => sender.SendAsync(Message(m => m.Subject = new string('a', 901))), "subject limit").ConfigureAwait(false);
            await SelfTest.Expect<EmailValidationException>(
                () => sender.SendAsync(Message(m => m.Subject = "a\0b")), "NUL in subject").ConfigureAwait(false);
            await SelfTest.Expect<EmailValidationException>(
                () => sender.SendAsync(Message(m => m.Headers["X-Long"] = new string('a', 901))), "header value limit").ConfigureAwait(false);
            await SelfTest.Expect<EmailValidationException>(
                () => sender.SendAsync(Message(m => m.To[0] = new EmailAddress("to@example.com", new string('n', 256)))), "display name limit").ConfigureAwait(false);
            await SelfTest.Expect<EmailValidationException>(
                () => sender.SendAsync(Message(m => m.To[0] = new EmailAddress("üser@example.com"))), "non-ASCII local part").ConfigureAwait(false);
            await SelfTest.Expect<EmailValidationException>(
                () => sender.SendAsync(Message(m => m.Attachments.Add(new EmailAttachment(new string('日', 120) + ".txt", new byte[1])))),
                "encoded file name limit").ConfigureAwait(false);

            var accepted = Message(m => m.Attachments.Add(new EmailAttachment(new string('日', 60) + ".txt", new byte[1])));
            SelfTest.Assert(MimeMessageFactory.Create(accepted, includeBcc: false).Length > 0, "long but encodable file name");
        }

        private static void CheckUnicodeMultipart()
        {
            var text = "第一行\nplain line\n.leading dot";
            var html = "<p>" + new string('x', 1500) + " é</p>";
            var pdf = Enumerable.Range(0, 100000).Select(i => (byte)(i * 31)).ToArray();

            var message = new EmailMessage
            {
                From = new EmailAddress("sender@example.com", "Zoë, \"Z\" Ünal"),
                Subject = "日本語のテスト ✓ " + new string('é', 60),
                TextBody = text,
                HtmlBody = html
            };
            message.To.Add(new EmailAddress("user@bücher.example", "山田 太郎"));
            message.Cc.Add(new EmailAddress("cc@example.com"));
            message.Bcc.Add(new EmailAddress("hidden@example.com"));
            message.Headers["X-Note"] = "héllo wörld";
            message.Attachments.Add(new EmailAttachment("logo.png", new byte[] { 1, 2, 3 }, "image/png", "<logo@x>"));
            message.Attachments.Add(new EmailAttachment("résumé 履歴書.pdf", pdf, "application/pdf"));

            var raw = MimeMessageFactory.Create(message, includeBcc: false);
            AssertWireFormat(raw);

            var mime = Load(raw);
            SelfTest.Assert(mime.Subject == message.Subject, "unicode subject");
            SelfTest.Assert(
                mime.From.Mailboxes.Single().Name == "Zoë, \"Z\" Ünal" &&
                mime.From.Mailboxes.Single().Address == "sender@example.com",
                "from display name with specials");
            var to = mime.To.Mailboxes.Single();
            SelfTest.Assert(to.Name == "山田 太郎" && to.Address == "user@bücher.example", "unicode recipient name");
            SelfTest.Assert(Encoding.ASCII.GetString(raw).Contains("<user@xn--bcher-kva.example>", StringComparison.Ordinal), "IDN domain sent as punycode");
            SelfTest.Assert(mime.Headers["X-Note"] == "héllo wörld", "unicode custom header");
            SelfTest.Assert(mime.Bcc.Count == 0, "Bcc omitted");
            SelfTest.Assert(mime.MessageId != null && mime.MessageId.EndsWith("@example.com", StringComparison.Ordinal), "message id domain");
            SelfTest.Assert(Math.Abs((mime.Date - DateTimeOffset.UtcNow).TotalMinutes) < 5, "date header");

            var mixed = (Multipart)mime.Body!;
            SelfTest.Assert(mixed.ContentType.MediaSubtype == "mixed" && mixed.Count == 2, "mixed structure");
            var alternative = (Multipart)mixed[0];
            SelfTest.Assert(alternative.ContentType.MediaSubtype == "alternative", "alternative structure");
            SelfTest.Assert(Normalize(((TextPart)alternative[0]).Text) == text, "unicode text body");
            var related = (Multipart)alternative[1];
            SelfTest.Assert(related.ContentType.MediaSubtype == "related", "related structure");
            SelfTest.Assert(Normalize(((TextPart)related[0]).Text) == html, "long html body");
            var inline = (MimePart)related[1];
            SelfTest.Assert(inline.ContentId == "logo@x" && inline.ContentDisposition!.Disposition == "inline", "inline content id");

            var attachment = (MimePart)mixed[1];
            SelfTest.Assert(attachment.FileName == "résumé 履歴書.pdf", "unicode file name");
            using var content = new MemoryStream();
            attachment.Content!.DecodeTo(content);
            SelfTest.Assert(content.ToArray().SequenceEqual(pdf), "attachment bytes");

            var withBcc = Load(MimeMessageFactory.Create(message, includeBcc: true));
            SelfTest.Assert(withBcc.Bcc.Mailboxes.Single().Address == "hidden@example.com", "Bcc included on request");
        }

        private static void CheckLongAsciiSubject()
        {
            var subject = string.Join(" ", Enumerable.Range(0, 60).Select(i => "word" + i)) + "  double  space";
            var message = new EmailMessage
            {
                From = new EmailAddress("sender@example.com"),
                Subject = subject,
                TextBody = "body"
            };
            message.To.Add(new EmailAddress("to@example.com"));

            var raw = MimeMessageFactory.Create(message, includeBcc: false);
            AssertWireFormat(raw);

            var header = Encoding.ASCII.GetString(raw);
            header = header.Substring(0, header.IndexOf("\r\n\r\n", StringComparison.Ordinal));
            SelfTest.Assert(header.Split(new[] { "\r\n" }, StringSplitOptions.None).All(l => l.Length <= 78), "folded header lines");
            SelfTest.Assert(Load(raw).Subject == subject, "folded subject round trip");
        }

        private static void CheckTextOnly()
        {
            var message = new EmailMessage
            {
                From = new EmailAddress("sender@example.com"),
                Subject = "Plain",
                TextBody = "one\rtwo\nthree\r\n"
            };
            message.To.Add(new EmailAddress("to@example.com"));

            var raw = MimeMessageFactory.Create(message, includeBcc: false);
            AssertWireFormat(raw);

            var mime = Load(raw);
            SelfTest.Assert(mime.Body is TextPart, "text-only is a single part");
            SelfTest.Assert(Normalize(mime.TextBody!) == "one\ntwo\nthree", "text newlines normalised");
            SelfTest.Assert(Encoding.ASCII.GetString(raw).Contains("Content-Transfer-Encoding: 7bit", StringComparison.Ordinal), "ascii text stays 7bit");
        }

        private static void AssertWireFormat(byte[] raw)
        {
            SelfTest.Assert(raw.All(b => b < 128), "MIME output is 7-bit ASCII");

            var text = Encoding.ASCII.GetString(raw);
            SelfTest.Assert(!text.Replace("\r\n", string.Empty).Contains('\n') && !text.Replace("\r\n", string.Empty).Contains('\r'), "CRLF only");
            SelfTest.Assert(text.EndsWith("\r\n", StringComparison.Ordinal), "ends with CRLF");
            SelfTest.Assert(text.Split(new[] { "\r\n" }, StringSplitOptions.None).All(l => l.Length <= 998), "line length limit");
        }

        private static MimeMessage Load(byte[] raw)
        {
            using (var stream = new MemoryStream(raw))
            {
                return MimeMessage.Load(stream);
            }
        }

        private static string Normalize(string value)
        {
            return value.Replace("\r\n", "\n").TrimEnd('\n');
        }
    }
}
