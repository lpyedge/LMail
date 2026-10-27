using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;

namespace LMail.Samples
{
    /// <summary>
    /// Drives <see cref="SmtpEmailSender"/> against a scripted local SMTP server.
    /// </summary>
    internal static class SmtpSmokeCheck
    {
        private static readonly X509Certificate2 ServerCertificate = CreateCertificate();

        public static async Task RunAsync()
        {
            await CredentialsNeedTlsAsync().ConfigureAwait(false);
            await LoginOverPlainConnectionAsync().ConfigureAwait(false);
            await StartTlsWithPlainAuthAsync().ConfigureAwait(false);
            await ImplicitTlsWithOAuth2Async().ConfigureAwait(false);
            await RecipientRejectedAsync().ConfigureAwait(false);
            await StartTlsRequiredButMissingAsync().ConfigureAwait(false);
            await UntrustedCertificateAsync().ConfigureAwait(false);
            await TokenFailureAsync().ConfigureAwait(false);
            await TimeoutAndCancellationAsync().ConfigureAwait(false);
        }

        private static async Task CredentialsNeedTlsAsync()
        {
            using var server = new FakeServer { Auth = "LOGIN" };
            var sender = new SmtpEmailSender(server.Options(SmtpSecurity.Auto, "user", "pa55"));

            await SelfTest.Expect<EmailSendException>(() => sender.SendAsync(NewMessage()), "credentials refused without TLS")
                .ConfigureAwait(false);
            SelfTest.Assert(server.AuthLines.Count == 0, "no credentials reach a plain connection");
        }

        private static async Task LoginOverPlainConnectionAsync()
        {
            using var server = new FakeServer { Auth = "LOGIN" };
            var sender = new SmtpEmailSender(server.Options(SmtpSecurity.None, "user", "pa55"));

            await sender.SendAsync(NewMessage()).ConfigureAwait(false);
            await server.Completion.ConfigureAwait(false);

            SelfTest.Assert(server.AuthLines.SequenceEqual(new[] { "user", "pa55" }), "AUTH LOGIN credentials");
            SelfTest.Assert(server.Recipients.Count == 3, "recipients are de-duplicated");
            SelfTest.Assert(server.Recipients.Contains("<hidden@example.com>"), "Bcc is an envelope recipient");
            SelfTest.Assert(server.MailFrom == "<sender@example.com>", "envelope sender");

            using var stream = new MemoryStream(Encoding.ASCII.GetBytes(server.Data));
            var mime = MimeMessage.Load(stream);
            SelfTest.Assert(mime.Bcc.Count == 0, "Bcc must not be in the message");
            SelfTest.Assert(mime.TextBody!.Contains("\n.leading dot", StringComparison.Ordinal), "dot-stuffing round trip");
            SelfTest.Assert(mime.BodyParts.OfType<MimePart>().Any(p => p.FileName == "logo.png"), "attachment delivered");
        }

        private static async Task StartTlsWithPlainAuthAsync()
        {
            using var server = new FakeServer { StartTls = true, Auth = "PLAIN LOGIN" };
            var sender = new SmtpEmailSender(server.Options(SmtpSecurity.StartTls, "user", "pass"));

            await sender.SendAsync(NewMessage()).ConfigureAwait(false);
            await server.Completion.ConfigureAwait(false);

            SelfTest.Assert(
                server.Commands.Where(c => c.StartsWith("EHLO", StringComparison.Ordinal) || c == "STARTTLS")
                    .SequenceEqual(new[] { "EHLO localhost", "STARTTLS", "EHLO localhost" }),
                "EHLO is repeated after STARTTLS");
            SelfTest.Assert(server.AuthLines.SequenceEqual(new[] { "\0user\0pass" }), "AUTH PLAIN credentials");
            SelfTest.Assert(server.TlsActiveAtAuth, "credentials are sent only after TLS");
        }

        private static async Task ImplicitTlsWithOAuth2Async()
        {
            using var server = new FakeServer { ImplicitTls = true, Auth = "XOAUTH2" };
            var options = server.Options(SmtpSecurity.SslOnConnect, "user", null);
            options.AccessTokenProvider = new StaticAccessTokenProvider("tok");
            var sender = new SmtpEmailSender(options);

            await sender.SendAsync(NewMessage()).ConfigureAwait(false);
            await server.Completion.ConfigureAwait(false);

            SelfTest.Assert(
                server.AuthLines.SequenceEqual(new[] { "user=user\u0001auth=Bearer tok\u0001\u0001" }),
                "XOAUTH2 initial response");
        }

        private static async Task RecipientRejectedAsync()
        {
            using var server = new FakeServer { RejectRecipient = "<cc@example.com>" };
            var sender = new SmtpEmailSender(server.Options(SmtpSecurity.None, null, null));

            try
            {
                await sender.SendAsync(NewMessage()).ConfigureAwait(false);
            }
            catch (EmailSendException ex)
            {
                SelfTest.Assert(ex.StatusCode == 550 && ex.Provider == "SMTP", "rejected recipient keeps the SMTP reply code");
                return;
            }

            throw new InvalidOperationException("A rejected recipient must fail the send");
        }

        private static async Task StartTlsRequiredButMissingAsync()
        {
            using var server = new FakeServer();
            var sender = new SmtpEmailSender(server.Options(SmtpSecurity.StartTls, "user", "pass"));

            await SelfTest.Expect<EmailSendException>(() => sender.SendAsync(NewMessage()), "STARTTLS required").ConfigureAwait(false);
            SelfTest.Assert(!server.Commands.Any(c => c.StartsWith("MAIL", StringComparison.Ordinal)), "nothing is sent without TLS");
        }

        private static async Task UntrustedCertificateAsync()
        {
            using var server = new FakeServer { ImplicitTls = true };
            var options = server.Options(SmtpSecurity.SslOnConnect, null, null);
            options.CertificateValidation = null;
            var sender = new SmtpEmailSender(options);

            await SelfTest.Expect<EmailSendException>(() => sender.SendAsync(NewMessage()), "self-signed certificate rejected")
                .ConfigureAwait(false);
        }

        private static async Task TokenFailureAsync()
        {
            using var server = new FakeServer { Auth = "XOAUTH2" };
            var options = server.Options(SmtpSecurity.None, "user", null);
            options.AccessTokenProvider = new FailingTokenProvider();
            var sender = new SmtpEmailSender(options);

            await SelfTest.Expect<OAuth2Exception>(() => sender.SendAsync(NewMessage()), "OAuth2 failure propagates")
                .ConfigureAwait(false);
        }

        private static async Task TimeoutAndCancellationAsync()
        {
            using (var server = new FakeServer { Silent = true })
            {
                var options = server.Options(SmtpSecurity.None, null, null);
                options.Timeout = TimeSpan.FromMilliseconds(300);

                await SelfTest.Expect<EmailSendException>(
                    () => new SmtpEmailSender(options).SendAsync(NewMessage()),
                    "SMTP timeout").ConfigureAwait(false);
            }

            using (var server = new FakeServer { Silent = true })
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            {
                var sender = new SmtpEmailSender(server.Options(SmtpSecurity.None, null, null));

                await SelfTest.Expect<OperationCanceledException>(
                    () => sender.SendAsync(NewMessage(), cancel.Token),
                    "SMTP cancellation").ConfigureAwait(false);
            }
        }

        private static EmailMessage NewMessage()
        {
            var message = new EmailMessage
            {
                From = new EmailAddress("sender@example.com"),
                Subject = "SMTP loopback",
                TextBody = "Plain text body\n.leading dot\nend"
            };
            message.To.Add(new EmailAddress("to@example.com"));
            message.Cc.Add(new EmailAddress("cc@example.com"));
            message.Cc.Add(new EmailAddress("to@example.com"));
            message.Bcc.Add(new EmailAddress("hidden@example.com"));
            message.Attachments.Add(new EmailAttachment("logo.png", new byte[] { 1, 2, 3 }, "image/png", "logo"));
            return message;
        }

        private static X509Certificate2 CreateCertificate()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());

            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
        }

        private sealed class FakeServer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            private bool _tlsActive;

            public FakeServer()
            {
                _listener.Start();
                Completion = RunAsync();
            }

            public bool StartTls { get; set; }

            public bool ImplicitTls { get; set; }

            public string? Auth { get; set; }

            public string? RejectRecipient { get; set; }

            public bool Silent { get; set; }

            public List<string> Commands { get; } = new List<string>();

            public List<string> Recipients { get; } = new List<string>();

            public List<string> AuthLines { get; } = new List<string>();

            public string? MailFrom { get; private set; }

            public string Data { get; private set; } = string.Empty;

            public bool TlsActiveAtAuth { get; private set; }

            public Task Completion { get; }

            public SmtpOptions Options(SmtpSecurity security, string? userName, string? password)
            {
                return new SmtpOptions
                {
                    Host = "127.0.0.1",
                    Port = ((IPEndPoint)_listener.LocalEndpoint).Port,
                    Security = security,
                    UserName = userName,
                    Password = password,
                    CertificateValidation = (_, certificate, _, _) =>
                        certificate != null && certificate.GetCertHashString() == ServerCertificate.GetCertHashString()
                };
            }

            public void Dispose()
            {
                _stop.Cancel();
                _listener.Stop();
                try { Completion.GetAwaiter().GetResult(); }
                catch (Exception) { }
                _stop.Dispose();
            }

            private async Task RunAsync()
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                    Stream stream = client.GetStream();

                    if (Silent)
                    {
                        await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false);
                    }

                    if (ImplicitTls)
                    {
                        stream = await UpgradeAsync(stream).ConfigureAwait(false);
                    }

                    await WriteAsync(stream, "220 localhost ready").ConfigureAwait(false);

                    while (await ReadLineAsync(stream).ConfigureAwait(false) is { } line)
                    {
                        Commands.Add(line);

                        if (line.StartsWith("EHLO", StringComparison.Ordinal))
                        {
                            var lines = new List<string> { "localhost" };
                            if (StartTls && !_tlsActive)
                            {
                                lines.Add("STARTTLS");
                            }
                            if (Auth != null)
                            {
                                lines.Add("AUTH " + Auth);
                            }
                            lines.Add("8BITMIME");

                            for (var i = 0; i < lines.Count; i++)
                            {
                                await WriteAsync(stream, "250" + (i == lines.Count - 1 ? " " : "-") + lines[i]).ConfigureAwait(false);
                            }
                        }
                        else if (line == "STARTTLS")
                        {
                            await WriteAsync(stream, "220 go ahead").ConfigureAwait(false);
                            stream = await UpgradeAsync(stream).ConfigureAwait(false);
                        }
                        else if (line == "AUTH LOGIN")
                        {
                            TlsActiveAtAuth = _tlsActive;
                            await WriteAsync(stream, "334 VXNlcm5hbWU6").ConfigureAwait(false);
                            AuthLines.Add(Decode(await ReadLineAsync(stream).ConfigureAwait(false)));
                            await WriteAsync(stream, "334 UGFzc3dvcmQ6").ConfigureAwait(false);
                            AuthLines.Add(Decode(await ReadLineAsync(stream).ConfigureAwait(false)));
                            await WriteAsync(stream, "235 authenticated").ConfigureAwait(false);
                        }
                        else if (line.StartsWith("AUTH PLAIN ", StringComparison.Ordinal) ||
                                 line.StartsWith("AUTH XOAUTH2 ", StringComparison.Ordinal))
                        {
                            TlsActiveAtAuth = _tlsActive;
                            AuthLines.Add(Decode(line.Substring(line.LastIndexOf(' ') + 1)));
                            await WriteAsync(stream, "235 authenticated").ConfigureAwait(false);
                        }
                        else if (line.StartsWith("MAIL FROM:", StringComparison.Ordinal))
                        {
                            MailFrom = line.Substring("MAIL FROM:".Length);
                            await WriteAsync(stream, "250 ok").ConfigureAwait(false);
                        }
                        else if (line.StartsWith("RCPT TO:", StringComparison.Ordinal))
                        {
                            var recipient = line.Substring("RCPT TO:".Length);
                            if (recipient == RejectRecipient)
                            {
                                await WriteAsync(stream, "550 no such user").ConfigureAwait(false);
                                continue;
                            }

                            Recipients.Add(recipient);
                            await WriteAsync(stream, "250 ok").ConfigureAwait(false);
                        }
                        else if (line == "DATA")
                        {
                            await WriteAsync(stream, "354 end with a dot").ConfigureAwait(false);

                            var data = new StringBuilder();
                            while (await ReadLineAsync(stream).ConfigureAwait(false) is { } bodyLine && bodyLine != ".")
                            {
                                data.Append(bodyLine.StartsWith("..", StringComparison.Ordinal) ? bodyLine.Substring(1) : bodyLine);
                                data.Append("\r\n");
                            }

                            Data = data.ToString();
                            await WriteAsync(stream, "250 accepted").ConfigureAwait(false);
                        }
                        else if (line == "QUIT")
                        {
                            await WriteAsync(stream, "221 bye").ConfigureAwait(false);
                            break;
                        }
                        else
                        {
                            await WriteAsync(stream, "500 unknown command").ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException ||
                                           ex is System.Security.Authentication.AuthenticationException || ex is SocketException)
                {
                    // The client closing or aborting the connection is expected in the failure scenarios.
                }
            }

            private async Task<Stream> UpgradeAsync(Stream stream)
            {
                var ssl = new SslStream(stream, false);
                await ssl.AuthenticateAsServerAsync(ServerCertificate).ConfigureAwait(false);
                _tlsActive = true;
                return ssl;
            }

            private async Task<string?> ReadLineAsync(Stream stream)
            {
                var bytes = new List<byte>();
                var one = new byte[1];

                while (await stream.ReadAsync(one, 0, 1, _stop.Token).ConfigureAwait(false) == 1)
                {
                    if (one[0] == '\n')
                    {
                        return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                    }

                    bytes.Add(one[0]);
                }

                return null;
            }

            private async Task WriteAsync(Stream stream, string line)
            {
                var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
                await stream.WriteAsync(bytes, 0, bytes.Length, _stop.Token).ConfigureAwait(false);
                await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
            }

            private static string Decode(string? base64)
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(base64 ?? string.Empty));
            }
        }

        private sealed class FailingTokenProvider : IAccessTokenProvider
        {
            public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromException<string>(new OAuth2Exception("Expected token failure"));
            }
        }
    }
}
