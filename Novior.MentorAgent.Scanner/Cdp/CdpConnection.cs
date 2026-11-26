using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Novior.MentorAgent.Scanner.Cdp;

/// <summary>
/// Story 1636 — a connection to a browser over the Chrome DevTools Protocol: commands as JSON over a WebSocket, each
/// answered by its id. Written on <see cref="ClientWebSocket"/> and System.Text.Json, so the scanner downloads nothing.
/// Events are not read.
/// </summary>
internal sealed class CdpConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _reader;
    private int _id;

    private CdpConnection(ClientWebSocket socket)
    {
        _socket = socket;
        _reader = Task.Run(ReadAsync);
    }

    /// <summary>Connects to the browser's endpoint, <c>ws://127.0.0.1:&lt;port&gt;/devtools/browser/&lt;id&gt;</c>.</summary>
    internal static async Task<CdpConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return new CdpConnection(socket);
    }

    /// <summary>Sends <paramref name="method"/> and returns its <c>result</c>; a CDP error is a <see cref="CdpException"/>.</summary>
    internal async Task<JsonElement> SendAsync(string method, JsonObject? parameters, string? sessionId,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _id);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;

        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        if (sessionId is not null) message["sessionId"] = sessionId;
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());

        try
        {
            await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _send.Release();
            }
            return await answer.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpException($"{method} had no answer in {timeout.TotalSeconds:0} s.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open && !_closing.IsCancellationRequested)
            {
                var part = await _socket.ReceiveAsync(buffer, _closing.Token).ConfigureAwait(false);
                if (part.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, part.Count);
                if (!part.EndOfMessage) continue;

                Dispatch(message.ToArray());
                message.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // The browser closed, or the connection is being disposed.
        }
        finally
        {
            foreach (var waiting in _pending.Values)
                waiting.TrySetException(new CdpException("The browser closed the connection."));
        }
    }

    private void Dispatch(byte[] bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var idElement)
                || !idElement.TryGetInt32(out var id) || !_pending.TryGetValue(id, out var waiting))
                return;   // an event, or an answer nobody waits for any more

            if (root.TryGetProperty("error", out var error))
                waiting.TrySetException(new CdpException(error.TryGetProperty("message", out var text) ? text.GetString() ?? "CDP error" : "CDP error"));
            else
                waiting.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, quick.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
        _closing.Cancel();
        try { await _reader.ConfigureAwait(false); } catch (Exception) { /* already reported to the waiting commands */ }
        _socket.Dispose();
        _closing.Dispose();
        _send.Dispose();
    }
}

/// <summary>A command the browser refused or did not answer.</summary>
internal sealed class CdpException(string message) : Exception(message);
