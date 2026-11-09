// One WebSocket to /ws/battle. Sends and receives on background tasks; received text frames and the
// close are queued and handed to the main thread by Drain(), so callers can touch Unity objects.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Freestyle.Battle
{
    public sealed class BattleSocket : IDisposable
    {
        readonly ClientWebSocket ws = new();
        readonly CancellationTokenSource cts = new();
        readonly ConcurrentQueue<(string text, byte[] bin)> outbox = new();
        readonly SemaphoreSlim outSignal = new(0);
        readonly ConcurrentQueue<string> inbox = new();
        volatile string closeReason;   // set once, when the socket ends
        bool closeReported;
        int pending;                   // frames queued or being sent

        public bool IsOpen => ws.State == WebSocketState.Open;

        /// <param name="password">the server's password, sent as `Authorization: Bearer`; null when it has none</param>
        public async Task ConnectAsync(Uri uri, string password = null)
        {
            if (!string.IsNullOrEmpty(password)) ws.Options.SetRequestHeader("Authorization", "Bearer " + password);
            await ws.ConnectAsync(uri, cts.Token);
            _ = Task.Run(SendLoop);
            _ = Task.Run(ReceiveLoop);
        }

        public void SendText(string json) => Enqueue((json, null));
        public void SendBinary(byte[] data) => Enqueue((null, data));

        void Enqueue((string, byte[]) m)
        {
            Interlocked.Increment(ref pending);
            outbox.Enqueue(m);
            outSignal.Release();
        }

        /// <summary>Main thread: hands each received text frame to onText, then reports the close once.</summary>
        public void Drain(Action<string> onText, Action<string> onClosed)
        {
            while (inbox.TryDequeue(out var msg)) onText(msg);
            if (closeReason != null && !closeReported && inbox.IsEmpty)
            {
                closeReported = true;
                onClosed(closeReason);
            }
        }

        async Task SendLoop()
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await outSignal.WaitAsync(cts.Token);
                    if (!outbox.TryDequeue(out var m)) continue;
                    try
                    {
                        if (m.text != null)
                            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(m.text)), WebSocketMessageType.Text, true, cts.Token);
                        else
                            await ws.SendAsync(new ArraySegment<byte>(m.bin), WebSocketMessageType.Binary, true, cts.Token);
                    }
                    finally { Interlocked.Decrement(ref pending); }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { closeReason ??= "send failed: " + e.Message; }
        }

        async Task ReceiveLoop()
        {
            var buf = new byte[64 * 1024];
            var msg = new MemoryStream();
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        closeReason ??= "server closed the connection";
                        return;
                    }
                    msg.Write(buf, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    if (r.MessageType == WebSocketMessageType.Text)
                        inbox.Enqueue(Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length));
                    msg.SetLength(0);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { closeReason ??= "connection lost: " + e.Message; }
        }

        public void Dispose()
        {
            closeReported = true;   // a deliberate close isn't reported as a disconnect
            // In the background: let queued frames (e.g. "stop") go out, close politely, then tear down.
            Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < 50 && Volatile.Read(ref pending) > 0 && ws.State == WebSocketState.Open; i++)
                        await Task.Delay(10);
                    using var t = new CancellationTokenSource(1000);
                    if (ws.State == WebSocketState.Open)
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", t.Token);
                }
                catch { }
                finally
                {
                    cts.Cancel();
                    ws.Dispose();
                }
            });
        }
    }
}
