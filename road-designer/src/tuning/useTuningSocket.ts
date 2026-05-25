// Hook: opens a WebSocket to Unity's TuningServer and exposes the
// current set of entries + a setter that pushes changes.
//
// Design choices worth flagging:
//  - Updates are optimistic. `setValue` mutates local state IMMEDIATELY
//    and fires the message; we do NOT re-apply on every ack. Reason: a
//    slider drag emits ~60 events/sec and acks from older sends would
//    cause the slider to "fight" with the user. We only resync on a
//    full snapshot (which the server only sends on connect).
//  - Ack messages are still consumed — only to surface server-side
//    errors via `lastError`.
//  - Auto-reconnect with a short backoff. Useful when you toggle Play
//    mode in Unity (server tears down + restarts).

import { useCallback, useEffect, useRef, useState } from "react";
import type { IncomingMsg, TuningEntry } from "./types";

export type TuningStatus = "connecting" | "open" | "closed" | "error";

export type UseTuningSocket = {
  status: TuningStatus;
  entries: TuningEntry[];
  setValue: (key: string, value: unknown) => void;
  lastError: string | null;
  reconnect: () => void;
};

export function useTuningSocket(url: string): UseTuningSocket {
  const [status, setStatus] = useState<TuningStatus>("closed");
  const [entries, setEntries] = useState<TuningEntry[]>([]);
  const [lastError, setLastError] = useState<string | null>(null);
  const wsRef = useRef<WebSocket | null>(null);
  const reconnectTimerRef = useRef<number | null>(null);
  const [reconnectTick, setReconnectTick] = useState(0);

  const reconnect = useCallback(() => {
    setReconnectTick((t) => t + 1);
  }, []);

  useEffect(() => {
    let cancelled = false;
    setStatus("connecting");
    let ws: WebSocket;
    try {
      ws = new WebSocket(url);
    } catch (e) {
      setStatus("error");
      setLastError(String(e));
      return;
    }
    wsRef.current = ws;

    ws.onopen = () => {
      if (cancelled) return;
      setStatus("open");
      setLastError(null);
    };
    ws.onerror = () => {
      if (cancelled) return;
      setStatus("error");
    };
    ws.onclose = () => {
      if (cancelled) return;
      setStatus("closed");
      // Schedule a reconnect attempt; useful when Unity restarts.
      if (reconnectTimerRef.current != null) {
        window.clearTimeout(reconnectTimerRef.current);
      }
      reconnectTimerRef.current = window.setTimeout(() => {
        setReconnectTick((t) => t + 1);
      }, 1500);
    };
    ws.onmessage = (ev) => {
      if (cancelled) return;
      let msg: IncomingMsg;
      try {
        msg = JSON.parse(ev.data) as IncomingMsg;
      } catch {
        return;
      }
      if (msg.op === "snapshot") {
        setEntries(msg.entries);
      } else if (msg.op === "ack") {
        if (msg.error) {
          setLastError(`${msg.key}: ${msg.error}`);
        }
      }
    };

    return () => {
      cancelled = true;
      if (reconnectTimerRef.current != null) {
        window.clearTimeout(reconnectTimerRef.current);
        reconnectTimerRef.current = null;
      }
      try {
        ws.close();
      } catch {
        // ignore
      }
    };
  }, [url, reconnectTick]);

  const setValue = useCallback((key: string, value: unknown) => {
    setEntries((prev) =>
      prev.map((e) => (e.key === key ? { ...e, value } : e)),
    );
    const ws = wsRef.current;
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    ws.send(JSON.stringify({ op: "set", key, value }));
  }, []);

  return { status, entries, setValue, lastError, reconnect };
}
