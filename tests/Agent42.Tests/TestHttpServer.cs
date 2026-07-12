using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Agent42.Tests;

/// <summary>
/// A minimal real HTTP server on a loopback TCP socket, for the tests that must open an actual
/// connection.
///
/// Why not the in-memory <c>TestServer</c> / a stub <c>HttpMessageHandler</c>: the thing under test is
/// <c>SocketsHttpHandler.ConnectCallback</c>, which only runs when a socket is really dialled. A stub
/// handler short-circuits the transport, so a test built on one would pass whether or not the SSRF
/// guard existed at all. Raw <see cref="TcpListener"/> rather than <c>HttpListener</c> because the
/// latter needs a URL ACL (elevation) for non-<c>localhost</c> prefixes on Windows.
/// </summary>
public sealed class TestHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private TestHttpServer(TcpListener listener, Func<string, HttpReply> respond)
    {
        _listener = listener;
        _loop = AcceptLoopAsync(respond);
    }

    /// <summary>Starts a server that answers every request with <paramref name="reply"/>.</summary>
    public static TestHttpServer Start(HttpReply reply) => Start(_ => reply);

    /// <summary>Starts a server whose reply is chosen from the request's start line.</summary>
    public static TestHttpServer Start(Func<string, HttpReply> respond)
    {
        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        return new TestHttpServer(listener, respond);
    }

    /// <summary>The server's base URL, e.g. <c>http://127.0.0.1:52413</c>.</summary>
    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    public sealed record HttpReply(int Status, string ContentType, string Body, string? Location = null);

    private async Task AcceptLoopAsync(Func<string, HttpReply> respond)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = ServeAsync(client, respond);
        }
    }

    private static async Task ServeAsync(TcpClient client, Func<string, HttpReply> respond)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();

                // Read just the request head; the tests never send a body.
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await stream.ReadAsync(one) == 0)
                    {
                        return;
                    }

                    head.Append((char)one[0]);
                }

                var startLine = head.ToString().Split("\r\n")[0];
                var reply = respond(startLine);

                var body = Encoding.UTF8.GetBytes(reply.Body);
                var header = new StringBuilder()
                    .Append($"HTTP/1.1 {reply.Status} X\r\n")
                    .Append($"Content-Type: {reply.ContentType}\r\n")
                    .Append($"Content-Length: {body.Length}\r\n");

                if (reply.Location is not null)
                {
                    header.Append($"Location: {reply.Location}\r\n");
                }

                header.Append("Connection: close\r\n\r\n");

                await stream.WriteAsync(Encoding.UTF8.GetBytes(header.ToString()));
                await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
            catch (IOException)
            {
                // The client hung up (e.g. the fetcher stopped reading at its download cap).
            }
            catch (SocketException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
