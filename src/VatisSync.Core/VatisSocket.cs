using System.Net.WebSockets;
using System.Text;

namespace VatisSync.Core;

/// <summary>One short-lived connection to vATIS. Abstracted so VatisClient can be tested without vATIS.</summary>
public interface IVatisSocket : IAsyncDisposable
{
    Task SendAsync(string text, CancellationToken ct);

    /// <summary>Returns the next text message, or null on timeout or if the peer closed.</summary>
    Task<string?> ReceiveAsync(TimeSpan timeout, CancellationToken ct);
}

public interface IVatisSocketFactory
{
    /// <summary>Throws if vATIS is not listening.</summary>
    Task<IVatisSocket> ConnectAsync(CancellationToken ct);
}

public sealed class WebSocketVatisSocketFactory : IVatisSocketFactory
{
    public static readonly Uri DefaultUri = new("ws://127.0.0.1:49082/");

    private readonly Uri _uri;
    private readonly TimeSpan _connectTimeout;

    public WebSocketVatisSocketFactory(Uri? uri = null, TimeSpan? connectTimeout = null)
    {
        _uri = uri ?? DefaultUri;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2);
    }

    public async Task<IVatisSocket> ConnectAsync(CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_connectTimeout);
            await ws.ConnectAsync(_uri, cts.Token).ConfigureAwait(false);
            return new Socket(ws);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    private sealed class Socket(ClientWebSocket ws) : IVatisSocket
    {
        public Task SendAsync(string text, CancellationToken ct) =>
            ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

        public async Task<string?> ReceiveAsync(TimeSpan timeout, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var buffer = new byte[8192];
            using var ms = new MemoryStream();
            try
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return null;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null; // our timeout, not the caller's cancellation
            }
            catch (WebSocketException)
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // best effort: we are done with the socket either way
            }
            finally
            {
                ws.Dispose();
            }
        }
    }
}
