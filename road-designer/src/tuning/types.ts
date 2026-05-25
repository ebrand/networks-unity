// Shape of messages exchanged with Unity's TuningServer over WebSocket.

export type TuningType = "float" | "color" | "bool" | "vector3" | "profile";

export type TuningEntry = {
  key: string;
  type: TuningType;
  label: string;
  category: string;
  value: unknown;
  meta: Record<string, unknown>;
};

export type SnapshotMsg = {
  op: "snapshot";
  entries: TuningEntry[];
};

export type AckMsg = {
  op: "ack";
  key: string;
  value: unknown;
  error: string | null;
};

export type IncomingMsg = SnapshotMsg | AckMsg;

export type SetMsg = {
  op: "set";
  key: string;
  value: unknown;
};

export type SnapshotRequest = {
  op: "snapshot";
};

export type OutgoingMsg = SetMsg | SnapshotRequest;
