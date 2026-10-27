using System;
using System.Collections.Generic;
using System.Text;

namespace LMail.Internal
{
    /// <summary>
    /// RFC 5322 header formatting: line folding, RFC 2047 encoded words and RFC 2231 parameters.
    /// Every returned value is 7-bit ASCII.
    /// </summary>
    internal static class MimeHeaders
    {
        private const int MaxLineLength = 76;
        private const int MaxEncodedWordBytes = 45;

        /// <summary>
        /// Joins tokens after <c>name:</c>, folding before a token that would pass 76 columns.
        /// </summary>
        public static string Format(string name, IEnumerable<string> tokens)
        {
            var result = new StringBuilder(name).Append(':');
            var lineLength = result.Length;
            var lineHasToken = false;

            foreach (var token in tokens)
            {
                if (token.Length == 0)
                {
                    continue;
                }

                var piece = token[0] == ' ' ? token : " " + token;

                if (lineHasToken && lineLength + piece.Length > MaxLineLength)
                {
                    result.Append("\r\n");
                    lineLength = 0;
                }

                result.Append(piece);
                lineLength += piece.Length;
                lineHasToken = true;
            }

            return result.Append("\r\n").ToString();
        }

        /// <summary>
        /// Tokens for free text such as a subject. Non-ASCII text is encoded as a whole.
        /// </summary>
        public static List<string> Unstructured(string value)
        {
            value = value.Trim();

            if (NeedsEncoding(value))
            {
                return EncodedWords(value);
            }

            var tokens = new List<string>();
            var i = 0;

            while (i < value.Length)
            {
                var start = i;

                while (i < value.Length && value[i] == ' ')
                {
                    i++;
                }

                while (i < value.Length && value[i] != ' ')
                {
                    i++;
                }

                tokens.Add(value.Substring(start, i - start));
            }

            return tokens;
        }

        public static List<string> Mailboxes(IEnumerable<EmailAddress> addresses)
        {
            var tokens = new List<string>();

            foreach (var address in addresses)
            {
                if (tokens.Count > 0)
                {
                    tokens[tokens.Count - 1] += ",";
                }

                var ascii = AddressText.ToAscii(address.Address);

                if (string.IsNullOrEmpty(address.DisplayName))
                {
                    tokens.Add(ascii);
                    continue;
                }

                if (NeedsEncoding(address.DisplayName!))
                {
                    tokens.AddRange(EncodedWords(address.DisplayName!));
                }
                else
                {
                    tokens.Add(AddressText.QuoteIfNeeded(address.DisplayName!));
                }

                tokens.Add("<" + ascii + ">");
            }

            return tokens;
        }

        /// <summary>
        /// A <c>name=value</c> parameter. Non-ASCII values use the RFC 2231 <c>name*=UTF-8''</c> form.
        /// </summary>
        public static string Parameter(string name, string value)
        {
            if (!NeedsEncoding(value))
            {
                return name + "=\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            }

            var result = new StringBuilder(name).Append("*=UTF-8''");

            foreach (var b in Encoding.UTF8.GetBytes(value))
            {
                var c = (char)b;

                if (b < 128 && (char.IsLetterOrDigit(c) || "!#$&+-.^_`|~".IndexOf(c) >= 0))
                {
                    result.Append(c);
                }
                else
                {
                    result.Append('%').Append(b.ToString("X2"));
                }
            }

            return result.ToString();
        }

        // Receivers decode anything shaped like an encoded word, so literal "=?" text must be encoded too.
        private static bool NeedsEncoding(string value)
        {
            return !AddressText.IsAscii(value) || value.Contains("=?");
        }

        private static List<string> EncodedWords(string text)
        {
            var words = new List<string>();
            var current = new List<byte>();

            for (var i = 0; i < text.Length; i++)
            {
                var length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
                var bytes = Encoding.UTF8.GetBytes(text.Substring(i, length));
                i += length - 1;

                if (current.Count + bytes.Length > MaxEncodedWordBytes)
                {
                    words.Add(EncodedWord(current));
                    current.Clear();
                }

                current.AddRange(bytes);
            }

            if (current.Count > 0)
            {
                words.Add(EncodedWord(current));
            }

            return words;
        }

        private static string EncodedWord(List<byte> bytes)
        {
            return "=?utf-8?B?" + Convert.ToBase64String(bytes.ToArray()) + "?=";
        }
    }
}
