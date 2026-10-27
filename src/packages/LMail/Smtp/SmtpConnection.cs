using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LMail.Internal
{
    /// <summary>
    /// One SMTP session (RFC 5321): replies, STARTTLS, AUTH and message transfer. Every network
    /// operation is bounded by the timeout and the caller's cancellation token; both abort the
    /// socket, since the stream APIs of netstandard2.1 cannot be cancelled otherwise.
    /// </summary>
    internal sealed class SmtpConnection : IDisposable
    {
        private const string Provider = "SMTP";
        private const int MaxLineLength = 4096;
        private const int WriteChunkSize = 16 * 1024;

        private readonly string _host;
        private readonly int _timeoutMilliseconds;
        private readonly RemoteCertificateValidationCallback? _certificateValidation;
        private readonly CancellationToken _callerToken;
        private readonly CancellationTokenSource _abort;
        private readonly CancellationTokenRegistration _abortRegistration;
        private readonly TcpClient _client = new TcpClient();
        private readonly byte[] _buffer = new byte[MaxLineLength];
        private int _bufferStart;
        private int _bufferEnd;
        private Stream? _stream;
        private HashSet<string> _extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _authMechanisms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public SmtpConnection(
            string host,
            TimeSpan timeout,
            RemoteCertificateValidationCallback? certificateValidation,
            CancellationToken cancellationToken)
        {
            _host = host;
            _timeoutMilliseconds = (int)Math.Min(timeout.TotalMilliseconds, int.MaxValue);
            _certificateValidation = certificateValidation;
            _callerToken = cancellationToken;
            _abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _abortRegistration = _abort.Token.Register(_client.Dispose);
        }

        public bool IsEncrypted => _stream is SslStream;

        public bool SupportsStartTls => _extensions.Contains("STARTTLS");

        public bool SupportsAuth(string mechanism)
        {
            return _authMechanisms.Contains(mechanism);
        }

        public async Task ConnectAsync(int port, bool implicitTls)
        {
            await RunAsync(() => _client.ConnectAsync(_host, port)).ConfigureAwait(false);
            _stream = _client.GetStream();

            if (implicitTls)
            {
                await UpgradeToTlsAsync().ConfigureAwait(false);
            }

            Check(await ReadReplyAsync().ConfigureAwait(false), 2, "connect");
        }

        public async Task EhloAsync()
        {
            var reply = await CommandAsync("EHLO localhost", 2).ConfigureAwait(false);

            _extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _authMechanisms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 1; i < reply.Lines.Count; i++)
            {
                var parts = reply.Lines[i].Split(new[] { ' ', '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    continue;
                }

                _extensions.Add(parts[0]);

                if (string.Equals(parts[0], "AUTH", StringComparison.OrdinalIgnoreCase))
                {
                    for (var j = 1; j < parts.Length; j++)
                    {
                        _authMechanisms.Add(parts[j]);
                    }
                }
            }
        }

        public async Task StartTlsAsync()
        {
            await CommandAsync("STARTTLS", 2).ConfigureAwait(false);
            await UpgradeToTlsAsync().ConfigureAwait(false);
            await EhloAsync().ConfigureAwait(false);
        }

        public async Task AuthenticateOAuth2Async(string userName, string accessToken)
        {
            var initial = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                "user=" + userName + "\u0001auth=Bearer " + accessToken + "\u0001\u0001"));

            var reply = await ExchangeAsync("AUTH XOAUTH2 " + initial).ConfigureAwait(false);

            if (reply.Code == 334)
            {
                // The server sent a JSON error; an empty line ends the exchange with the final failure reply.
                reply = await ExchangeAsync(string.Empty).ConfigureAwait(false);
            }

            Check(reply, 2, "AUTH");
        }

        public async Task AuthenticatePasswordAsync(string userName, string password)
        {
            if (SupportsAuth("PLAIN"))
            {
                var initial = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0" + userName + "\0" + password));
                Check(await ExchangeAsync("AUTH PLAIN " + initial).ConfigureAwait(false), 2, "AUTH");
                return;
            }

            if (SupportsAuth("LOGIN"))
            {
                Check(await ExchangeAsync("AUTH LOGIN").ConfigureAwait(false), 3, "AUTH");
                Check(await ExchangeAsync(Base64(userName)).ConfigureAwait(false), 3, "AUTH");
                Check(await ExchangeAsync(Base64(password)).ConfigureAwait(false), 2, "AUTH");
                return;
            }

            throw new EmailSendException("SMTP server supports neither PLAIN nor LOGIN authentication.", Provider);
        }

        public async Task SendMessageAsync(string from, IReadOnlyList<string> recipients, byte[] message)
        {
            await CommandAsync("MAIL FROM:<" + from + ">", 2).ConfigureAwait(false);

            foreach (var recipient in recipients)
            {
                await CommandAsync("RCPT TO:<" + recipient + ">", 2).ConfigureAwait(false);
            }

            await CommandAsync("DATA", 3).ConfigureAwait(false);
            await WriteDataAsync(message).ConfigureAwait(false);
            Check(await ReadReplyAsync().ConfigureAwait(false), 2, "DATA");
        }

        /// <summary>
        /// Ends the session politely. The message is already accepted, so failures here are ignored.
        /// </summary>
        public async Task QuitAsync()
        {
            try
            {
                await ExchangeAsync("QUIT").ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            _abortRegistration.Dispose();
            _abort.Dispose();
            _stream?.Dispose();
            _client.Dispose();
        }

        private async Task UpgradeToTlsAsync()
        {
            var ssl = new SslStream(_stream!, false, _certificateValidation);
            _stream = ssl;
            _bufferStart = 0;
            _bufferEnd = 0;

            await RunAsync(() => ssl.AuthenticateAsClientAsync(_host)).ConfigureAwait(false);
        }

        private async Task<SmtpReply> CommandAsync(string command, int expectedClass)
        {
            var reply = await ExchangeAsync(command).ConfigureAwait(false);
            Check(reply, expectedClass, command.Split(' ', ':')[0]);
            return reply;
        }

        private async Task<SmtpReply> ExchangeAsync(string line)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            await WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await RunAsync(() => _stream!.FlushAsync(_abort.Token)).ConfigureAwait(false);
            return await ReadReplyAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Sends the message as the DATA payload: dot-stuffed and ended by <c>CRLF.CRLF</c>. The
        /// message always ends with CRLF.
        /// </summary>
        private async Task WriteDataAsync(byte[] message)
        {
            var start = 0;

            for (var i = 0; i < message.Length; i++)
            {
                if (message[i] == (byte)'.' && (i == 0 || message[i - 1] == (byte)'\n'))
                {
                    await WriteAsync(message, start, i - start).ConfigureAwait(false);
                    await WriteAsync(new[] { (byte)'.' }, 0, 1).ConfigureAwait(false);
                    start = i;
                }
            }

            await WriteAsync(message, start, message.Length - start).ConfigureAwait(false);

            var end = Encoding.ASCII.GetBytes(".\r\n");
            await WriteAsync(end, 0, end.Length).ConfigureAwait(false);
            await RunAsync(() => _stream!.FlushAsync(_abort.Token)).ConfigureAwait(false);
        }

        private async Task WriteAsync(byte[] data, int offset, int count)
        {
            while (count > 0)
            {
                var size = Math.Min(count, WriteChunkSize);
                var chunkOffset = offset;
                await RunAsync(() => _stream!.WriteAsync(data, chunkOffset, size, _abort.Token)).ConfigureAwait(false);
                offset += size;
                count -= size;
            }
        }

        private async Task<SmtpReply> ReadReplyAsync()
        {
            var lines = new List<string>();

            while (true)
            {
                var line = await RunAsync(ReadLineAsync).ConfigureAwait(false);

                if (line.Length < 3 || !int.TryParse(line.Substring(0, 3), out var code))
                {
                    throw new EmailSendException("SMTP server sent an invalid reply: " + line, Provider);
                }

                lines.Add(line.Length > 4 ? line.Substring(4) : string.Empty);

                if (line.Length == 3 || line[3] != '-')
                {
                    return new SmtpReply(code, lines);
                }
            }
        }

        private async Task<string> ReadLineAsync()
        {
            while (true)
            {
                var newLine = Array.IndexOf(_buffer, (byte)'\n', _bufferStart, _bufferEnd - _bufferStart);

                if (newLine >= 0)
                {
                    var line = Encoding.UTF8.GetString(_buffer, _bufferStart, newLine - _bufferStart).TrimEnd('\r');
                    _bufferStart = newLine + 1;
                    return line;
                }

                if (_bufferStart > 0)
                {
                    Buffer.BlockCopy(_buffer, _bufferStart, _buffer, 0, _bufferEnd - _bufferStart);
                    _bufferEnd -= _bufferStart;
                    _bufferStart = 0;
                }

                if (_bufferEnd == _buffer.Length)
                {
                    throw new EmailSendException("SMTP server sent a reply line that is too long.", Provider);
                }

                var read = await _stream!
                    .ReadAsync(_buffer, _bufferEnd, _buffer.Length - _bufferEnd, _abort.Token)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    throw new EmailSendException("SMTP server closed the connection.", Provider);
                }

                _bufferEnd += read;
            }
        }

        private static void Check(SmtpReply reply, int expectedClass, string command)
        {
            if (reply.Code / 100 != expectedClass)
            {
                var text = string.Join(" ", reply.Lines);
                throw new EmailSendException(
                    "SMTP " + command + " failed: " + reply.Code + " " + text,
                    Provider,
                    reply.Code,
                    text);
            }
        }

        private static string Base64(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private async Task RunAsync(Func<Task> operation)
        {
            await RunAsync(async () =>
            {
                await operation().ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }

        private async Task<T> RunAsync<T>(Func<Task<T>> operation)
        {
            _abort.CancelAfter(_timeoutMilliseconds);

            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception) when (_abort.IsCancellationRequested)
            {
                _callerToken.ThrowIfCancellationRequested();
                throw new EmailSendException("SMTP operation timed out.", Provider);
            }
        }

        private sealed class SmtpReply
        {
            public SmtpReply(int code, List<string> lines)
            {
                Code = code;
                Lines = lines;
            }

            public int Code { get; }

            public List<string> Lines { get; }
        }
    }
}
