using System;

namespace LMail.Internal
{
    internal static class PayloadHelpers
    {
        public static string NormalizeContentId(string contentId)
        {
            var value = contentId.Trim();
            if (value.Length >= 2 && value[0] == '<' && value[value.Length - 1] == '>')
            {
                value = value.Substring(1, value.Length - 2);
            }

            return value;
        }

        public static EmailAddress? SingleReplyTo(EmailMessage message, string provider)
        {
            if (message.ReplyTo.Count == 0)
            {
                return null;
            }

            if (message.ReplyTo.Count > 1)
            {
                throw new EmailValidationException(
                    provider + " supports only one reply-to address.");
            }

            return message.ReplyTo[0];
        }

        public static string PlainTextToHtml(string text)
        {
            return "<p>" + System.Net.WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br>") + "</p>";
        }
    }
}
