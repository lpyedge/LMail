using System.Globalization;

namespace LMail.Internal
{
    internal static class AddressText
    {
        public static bool IsAscii(string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] > 127)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Mailbox address for MIME headers and the SMTP envelope. A non-ASCII domain becomes punycode.
        /// </summary>
        public static string ToAscii(string address)
        {
            var at = address.LastIndexOf('@');
            var domain = address.Substring(at + 1);

            return IsAscii(domain)
                ? address
                : address.Substring(0, at + 1) + new IdnMapping().GetAscii(domain);
        }

        /// <summary>
        /// <c>Display Name &lt;address&gt;</c> for JSON payloads. The display name is not MIME-encoded.
        /// </summary>
        public static string Mailbox(EmailAddress address)
        {
            return string.IsNullOrEmpty(address.DisplayName)
                ? address.Address
                : QuoteIfNeeded(address.DisplayName!) + " <" + address.Address + ">";
        }

        public static string QuoteIfNeeded(string name)
        {
            var simple = true;

            for (var i = 0; i < name.Length; i++)
            {
                if (!IsAtomOrSpace(name[i]))
                {
                    simple = false;
                    break;
                }
            }

            return simple
                ? name
                : "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static bool IsAtomOrSpace(char c)
        {
            return c == ' ' || char.IsLetterOrDigit(c) || "!#$%&'*+-/=?^_`{|}~".IndexOf(c) >= 0;
        }
    }
}
