import * as signalR from "@microsoft/signalr";
import type { LiveLanguage, LiveNoteFormat, NoteFailureReason, NoteSnapshot } from "@anamnys/shared/lib/types";

// Unset in normal Aspire-hosted dev/prod — relative "/hubs" resolves through vite.config.ts's
// same-origin proxy to the server resource (see also api/client.ts).
const SIGNALR_URL = import.meta.env.VITE_SIGNALR_URL ?? "/hubs";

export interface BeginAck { status: "ok" | "invalid" | "not_found" | "forbidden" | "archived" }
export interface ChunkAck {
  status: "accepted" | "duplicate" | "gap" | "buffer_expired" | "forbidden" | "too_long";
  lastSeq: number;
}
export interface FinalizeAck { status: "ok" | "buffer_expired"; noteId: string | null }

// SignalR's JSON hub protocol serializes byte[] as base64 (System.Text.Json), so the client
// encodes manually.
function toBase64(bytes: Uint8Array): string {
  let binary = "";
  for (let i = 0; i < bytes.byteLength; i++) binary += String.fromCharCode(bytes[i]);
  return btoa(binary);
}

function build(path: string): signalR.HubConnection {
  return new signalR.HubConnectionBuilder()
    .withUrl(`${SIGNALR_URL}/${path}`, { withCredentials: true })
    .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 10000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();
}

let live: signalR.HubConnection | null = null;

export const liveSessionHub = {
  connect: async (onReconnected: () => void, onReconnecting: () => void, onClose: () => void): Promise<void> => {
    await liveSessionHub.disconnect();
    const conn = build("transcription");
    // Callbacks of a connection that was replaced or deliberately stopped are ignored.
    conn.onreconnecting(() => { if (live === conn) onReconnecting(); });
    conn.onreconnected(() => { if (live === conn) onReconnected(); });
    conn.onclose(() => { if (live === conn) onClose(); });
    live = conn;
    try {
      await conn.start();
      // Disconnected or replaced while starting: nobody owns this connection any more.
      if (live !== conn) await conn.stop();
    } catch (e) {
      if (live === conn) live = null;
      throw e;
    }
  },
  disconnect: async (): Promise<void> => {
    const conn = live;
    live = null;
    await conn?.stop();
  },
  isConnected: (): boolean => live?.state === signalR.HubConnectionState.Connected,
  begin: (recordingId: string, patientId: string, appointmentId: string | null, format: LiveNoteFormat, language: LiveLanguage) =>
    live!.invoke<BeginAck>("BeginRecording", recordingId, patientId, appointmentId, format, language),
  sendChunk: (recordingId: string, seq: number, bytes: Uint8Array) =>
    live!.invoke<ChunkAck>("SendAudioChunk", recordingId, seq, toBase64(bytes)),
  finalize: (recordingId: string) => live!.invoke<FinalizeAck>("FinalizeStream", recordingId),
};

let progress: signalR.HubConnection | null = null;

export interface ProgressHandlers {
  onProgress: (noteId: string, stage: string, queuePosition: number) => void;
  onReady: (noteId: string) => void;
  onFailed: (noteId: string, reason: NoteFailureReason) => void;
}

// The connection one connect() call opened; it stays bound to that call, so a later connect()
// (e.g. React StrictMode's second effect run) cannot redirect it.
export interface ProgressConnection {
  subscribe: (noteId: string) => Promise<NoteSnapshot | null>;
}

export const progressHub = {
  // Resolves to null when disconnect() or another connect() took over while this one was starting.
  connect: async (handlers: ProgressHandlers): Promise<ProgressConnection | null> => {
    await progressHub.disconnect();
    const conn = build("progress");
    // Registered before start so no notification sent right after the subscribe is missed.
    conn.on("NoteProgress", handlers.onProgress);
    conn.on("NoteReady", handlers.onReady);
    conn.on("NoteFailed", handlers.onFailed);
    progress = conn;
    try {
      await conn.start();
    } catch (e) {
      if (progress === conn) progress = null;
      throw e;
    }
    if (progress !== conn) {
      await conn.stop();
      return null;
    }
    return { subscribe: (noteId) => conn.invoke<NoteSnapshot | null>("SubscribeToNote", noteId) };
  },
  disconnect: async (): Promise<void> => {
    const conn = progress;
    progress = null;
    await conn?.stop();
  },
};
