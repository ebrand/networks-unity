// Hand-rolled WebSocket server (RFC 6455) for live tuning of scene
// parameters from an external client (the React /tuning page).
//
// Why hand-rolled: Unity has no built-in WS server and adding a 3rd-party
// dep felt heavy for a dev-only tool. The implementation supports only
// what's needed: HTTP/1.1 upgrade handshake, single text-frame reads,
// single text-frame writes, and a graceful close. No fragmentation, no
// ping/pong (localhost doesn't need it), no compression. Single client
// at a time — second connection replaces the first.
//
// Threading model:
//   - One background "accept" loop accepts incoming TCP connections.
//   - On a new connection, we do the handshake on the worker thread,
//     then spawn a "read" task that pushes incoming JSON onto a
//     concurrent queue.
//   - Unity Update() drains that queue and dispatches via TuningRegistry,
//     so all setter invocations happen on the Unity main thread.
//   - Outgoing writes (snapshot + acks) are also marshaled through a
//     concurrent queue so any thread can enqueue, but only the worker
//     "write" thread actually touches the stream.
//
// Protocol (text frames carrying JSON):
//   Client → Server:
//     {"op":"snapshot"}              — request the full settings snapshot
//     {"op":"set","key":"x","value":...}
//
//   Server → Client:
//     {"op":"snapshot","entries":[{key,type,label,category,value,meta},...]}
//     {"op":"ack","key":"x","value":...,"error":null|"msg"}
//
// The server emits a snapshot on connect and on any "snapshot" request.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NetworkDesigner.Tuning
{
    [DisallowMultipleComponent]
    public class TuningServer : MonoBehaviour
    {
        [Tooltip("TCP port to listen on. The React panel should connect to ws://localhost:<port>/")]
        public int Port = 8787;

        [Tooltip("If true, log every inbound/outbound message. Noisy during slider drags.")]
        public bool LogTraffic = false;

        const string WS_MAGIC = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        TcpListener _listener;
        CancellationTokenSource _cts;

        // Inbound JSON messages from the current client (worker → main).
        readonly ConcurrentQueue<string> _inbound = new ConcurrentQueue<string>();
        // Outbound JSON messages to the current client (main → worker).
        readonly ConcurrentQueue<string> _outbound = new ConcurrentQueue<string>();

        // Only one client at a time. Workers signal "have a client" via
        // _hasClient; on disconnect they clear it.
        volatile bool _hasClient;
        TcpClient _activeClient;

        void OnEnable()
        {
            try
            {
                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                Task.Run(() => AcceptLoop(_cts.Token));
                Debug.Log($"[TuningServer] Listening on ws://localhost:{Port}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TuningServer] Failed to start: {ex.Message}");
            }
        }

        void OnDisable()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _activeClient?.Close(); } catch { }
            _hasClient = false;
        }

        void Update()
        {
            // Drain inbound queue and dispatch on the main thread.
            while (_inbound.TryDequeue(out string msg))
            {
                try
                {
                    HandleClientMessage(msg);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[TuningServer] error handling '{msg}': {ex}");
                }
            }
        }

        // -----------------------------------------------------------------
        // Inbound message dispatch (main thread)
        // -----------------------------------------------------------------

        void HandleClientMessage(string json)
        {
            if (LogTraffic) Debug.Log($"[TuningServer] ← {json}");
            JObject obj = JObject.Parse(json);
            string op = obj["op"]?.ToString();
            if (string.IsNullOrEmpty(op)) return;

            if (op == "snapshot")
            {
                SendSnapshot();
                return;
            }

            if (op == "set")
            {
                string key = obj["key"]?.ToString();
                JToken valueTok = obj["value"];
                object value = valueTok?.ToObject<object>();

                bool ok = TuningRegistry.TrySet(key, value, out string err);
                // Re-read so client sees the value that actually landed
                // (e.g. clamped).
                object after = TuningRegistry.TryGet(key);

                string ack = JsonConvert.SerializeObject(new
                {
                    op = "ack",
                    key,
                    value = after,
                    error = ok ? null : err,
                });
                EnqueueOutbound(ack);
                return;
            }

            Debug.LogWarning($"[TuningServer] unknown op '{op}'");
        }

        void SendSnapshot()
        {
            List<object> entries = new List<object>();
            foreach (TuningRegistry.Entry e in TuningRegistry.Entries)
            {
                entries.Add(new
                {
                    key = e.Key,
                    type = e.Type,
                    label = e.Label,
                    category = e.Category,
                    value = e.Get(),
                    meta = e.Meta,
                });
            }
            string payload = JsonConvert.SerializeObject(new { op = "snapshot", entries });
            EnqueueOutbound(payload);
        }

        void EnqueueOutbound(string msg)
        {
            if (LogTraffic) Debug.Log($"[TuningServer] → {msg}");
            _outbound.Enqueue(msg);
        }

        // -----------------------------------------------------------------
        // Accept loop (worker thread)
        // -----------------------------------------------------------------

        async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[TuningServer] accept error: {ex.Message}");
                    continue;
                }

                // Replace any existing client.
                if (_hasClient)
                {
                    try { _activeClient?.Close(); } catch { }
                }
                _activeClient = client;
                _hasClient = true;
                _ = Task.Run(() => ClientSession(client, ct));
            }
        }

        async Task ClientSession(TcpClient client, CancellationToken ct)
        {
            string remote = "?";
            try
            {
                remote = client.Client?.RemoteEndPoint?.ToString() ?? "?";
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    if (!await DoHandshake(stream, ct))
                    {
                        Debug.LogWarning($"[TuningServer] handshake failed with {remote}");
                        return;
                    }
                    Debug.Log($"[TuningServer] client connected: {remote}");

                    // Push an immediate snapshot so the React side has data.
                    _inbound.Enqueue("{\"op\":\"snapshot\"}");

                    // Run reader + writer in parallel; either ending tears down the session.
                    Task reader = Task.Run(() => ReadLoop(stream, ct));
                    Task writer = Task.Run(() => WriteLoop(stream, ct));
                    await Task.WhenAny(reader, writer);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TuningServer] client {remote} session error: {ex.Message}");
            }
            finally
            {
                if (_activeClient == client)
                {
                    _hasClient = false;
                    _activeClient = null;
                }
                Debug.Log($"[TuningServer] client disconnected: {remote}");
            }
        }

        // -----------------------------------------------------------------
        // HTTP/1.1 → WebSocket upgrade handshake
        // -----------------------------------------------------------------

        async Task<bool> DoHandshake(NetworkStream stream, CancellationToken ct)
        {
            // Read the HTTP request headers (up to CRLF CRLF).
            byte[] buf = new byte[4096];
            int total = 0;
            while (true)
            {
                int n = await stream.ReadAsync(buf, total, buf.Length - total, ct);
                if (n <= 0) return false;
                total += n;
                string header = Encoding.ASCII.GetString(buf, 0, total);
                if (header.IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0) break;
                if (total >= buf.Length) return false;
            }

            string text = Encoding.ASCII.GetString(buf, 0, total);
            Match m = Regex.Match(text, "Sec-WebSocket-Key:\\s*(.+)\\r\\n", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            string key = m.Groups[1].Value.Trim();

            string accept;
            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(key + WS_MAGIC));
                accept = Convert.ToBase64String(hash);
            }

            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            byte[] respBytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(respBytes, 0, respBytes.Length, ct);
            return true;
        }

        // -----------------------------------------------------------------
        // Read loop: parse incoming text frames and enqueue
        // -----------------------------------------------------------------

        async Task ReadLoop(NetworkStream stream, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    string msg = await ReadTextFrame(stream, ct);
                    if (msg == null) return; // close or error
                    _inbound.Enqueue(msg);
                }
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) Debug.LogWarning($"[TuningServer] read loop ended: {ex.Message}");
            }
        }

        // Returns null on close/error. Skips control frames other than close.
        async Task<string> ReadTextFrame(NetworkStream stream, CancellationToken ct)
        {
            byte[] header = new byte[2];
            if (!await ReadExact(stream, header, 0, 2, ct)) return null;
            bool fin = (header[0] & 0x80) != 0;
            int opcode = header[0] & 0x0F;
            bool masked = (header[1] & 0x80) != 0;
            int len = header[1] & 0x7F;

            if (opcode == 0x8) return null; // close frame
            // We don't expect fragmentation in our protocol. If we ever get
            // a continuation frame we'll fail loudly.
            if (!fin && opcode != 0x0)
            {
                Debug.LogWarning("[TuningServer] fragmented frame received — not supported");
                return null;
            }

            long payloadLen = len;
            if (len == 126)
            {
                byte[] ext = new byte[2];
                if (!await ReadExact(stream, ext, 0, 2, ct)) return null;
                payloadLen = (ext[0] << 8) | ext[1];
            }
            else if (len == 127)
            {
                byte[] ext = new byte[8];
                if (!await ReadExact(stream, ext, 0, 8, ct)) return null;
                payloadLen = 0;
                for (int i = 0; i < 8; i++) payloadLen = (payloadLen << 8) | ext[i];
            }

            byte[] mask = null;
            if (masked)
            {
                mask = new byte[4];
                if (!await ReadExact(stream, mask, 0, 4, ct)) return null;
            }

            if (payloadLen > 1024 * 1024) // 1MB sanity cap
            {
                Debug.LogWarning($"[TuningServer] frame too large ({payloadLen} bytes), aborting");
                return null;
            }

            byte[] payload = new byte[payloadLen];
            if (payloadLen > 0)
            {
                if (!await ReadExact(stream, payload, 0, (int)payloadLen, ct)) return null;
                if (masked)
                {
                    for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
                }
            }

            if (opcode == 0x1) return Encoding.UTF8.GetString(payload);
            // Binary or unknown opcode — ignore and keep reading.
            return await ReadTextFrame(stream, ct);
        }

        static async Task<bool> ReadExact(NetworkStream stream, byte[] buf, int offset, int count, CancellationToken ct)
        {
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buf, offset + read, count - read, ct);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        // -----------------------------------------------------------------
        // Write loop: pull from outbound queue, emit text frames
        // -----------------------------------------------------------------

        async Task WriteLoop(NetworkStream stream, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!_outbound.TryDequeue(out string msg))
                    {
                        await Task.Delay(5, ct);
                        continue;
                    }
                    byte[] frame = BuildTextFrame(msg);
                    await stream.WriteAsync(frame, 0, frame.Length, ct);
                }
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) Debug.LogWarning($"[TuningServer] write loop ended: {ex.Message}");
            }
        }

        static byte[] BuildTextFrame(string payload)
        {
            byte[] data = Encoding.UTF8.GetBytes(payload);
            int hdrLen;
            if (data.Length < 126) hdrLen = 2;
            else if (data.Length <= ushort.MaxValue) hdrLen = 4;
            else hdrLen = 10;

            byte[] frame = new byte[hdrLen + data.Length];
            frame[0] = 0x81; // FIN + text
            if (hdrLen == 2)
            {
                frame[1] = (byte)data.Length;
            }
            else if (hdrLen == 4)
            {
                frame[1] = 126;
                frame[2] = (byte)((data.Length >> 8) & 0xFF);
                frame[3] = (byte)(data.Length & 0xFF);
            }
            else
            {
                frame[1] = 127;
                long len = data.LongLength;
                for (int i = 7; i >= 0; i--)
                {
                    frame[2 + (7 - i)] = (byte)((len >> (i * 8)) & 0xFF);
                }
            }
            Buffer.BlockCopy(data, 0, frame, hdrLen, data.Length);
            return frame;
        }
    }
}
