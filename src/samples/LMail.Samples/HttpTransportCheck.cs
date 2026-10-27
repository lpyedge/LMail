using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LMail.Samples
{
    // A real loopback response catches body-read bugs that an immediate JSON stub cannot expose.
    internal static class HttpTransportCheck
    {
        public static async Task RunAsync()
        {
            await CheckAsync(cancel: false).ConfigureAwait(false);
            await CheckAsync(cancel: true).ConfigureAwait(false);
        }

        private static async Task CheckAsync(bool cancel)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = RespondAsync(listener, headersSent, stop.Token);
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
            using var cancellation = new CancellationTokenSource();
            var endpoint = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/token";
            var tokenClient = new OAuth2TokenClient(new OAuth2ClientOptions
            {
                ClientId = "local-test",
                TokenEndpoint = endpoint
            }, client);

            var request = tokenClient.RequestClientCredentialsAsync(cancellation.Token);
            try
            {
                await headersSent.Task.WaitAsync(stop.Token).ConfigureAwait(false);
                if (cancel)
                {
                    cancellation.Cancel();
                }

                var completed = await Task.WhenAny(request, Task.Delay(1500, stop.Token)).ConfigureAwait(false);
                if (completed != request)
                {
                    throw new InvalidOperationException("response body ignored " + (cancel ? "cancellation" : "HTTP timeout"));
                }

                try
                {
                    await request.ConfigureAwait(false);
                    throw new InvalidOperationException("stalled response must fail");
                }
                catch (OperationCanceledException) when (cancel)
                {
                }
                catch (OAuth2Exception) when (!cancel)
                {
                }
            }
            finally
            {
                stop.Cancel();
                await server.ConfigureAwait(false);
                // Observe a request that was unblocked by server shutdown after a failed assertion.
                try { await request.ConfigureAwait(false); }
                catch (Exception) { }
            }
        }

        private static async Task RespondAsync(TcpListener listener, TaskCompletionSource headersSent, CancellationToken stop)
        {
            try
            {
                using var connection = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false);
                using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                string? line;
                do
                {
                    line = await reader.ReadLineAsync(stop).ConfigureAwait(false);
                } while (!string.IsNullOrEmpty(line));

                var headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 100\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, stop).ConfigureAwait(false);
                headersSent.TrySetResult();
                await Task.Delay(Timeout.Infinite, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }
    }
}
