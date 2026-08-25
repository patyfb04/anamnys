import * as signalR from "@microsoft/signalr";
import type { PipelineProgress } from "@/lib/types";

// Unset in normal Aspire-hosted dev/prod — relative "/hubs" already resolves through
// vite.config.ts's same-origin proxy to the server resource (see also api/client.ts).
const SIGNALR_URL = import.meta.env.VITE_SIGNALR_URL ?? "/hubs";

// SignalR's JSON hub protocol serializes byte[] parameters as base64 strings
// (matching System.Text.Json), not raw ArrayBuffers — the client must encode manually.
function arrayBufferToBase64(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let binary = "";
  for (let i = 0; i < bytes.byteLength; i++) {
    binary += String.fromCharCode(bytes[i]);
  }
  return btoa(binary);
}

// ~16KB raw ≈ ~21KB after base64 encoding — safely under SignalR's default 32KB message limit.
const AUDIO_CHUNK_BYTES = 16 * 1024;

let transcriptionConnection: signalR.HubConnection | null = null;

export const transcriptionHub = {
  connect: async (): Promise<signalR.HubConnection> => {
    transcriptionConnection = new signalR.HubConnectionBuilder()
      .withUrl(`${SIGNALR_URL}/transcription`, { withCredentials: true })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    await transcriptionConnection.start();
    return transcriptionConnection;
  },

  disconnect: async (): Promise<void> => {
    if (transcriptionConnection) {
      await transcriptionConnection.stop();
      transcriptionConnection = null;
    }
  },

  sendChunk: (chunk: ArrayBuffer): Promise<void> => {
    if (!transcriptionConnection) return Promise.reject(new Error("Not connected to transcription hub"));
    return transcriptionConnection.invoke("SendAudioChunk", arrayBufferToBase64(chunk));
  },

  sendAudioBuffer: async (buffer: ArrayBuffer): Promise<void> => {
    const bytes = new Uint8Array(buffer);
    for (let offset = 0; offset < bytes.byteLength; offset += AUDIO_CHUNK_BYTES) {
      const slice = bytes.slice(offset, offset + AUDIO_CHUNK_BYTES).buffer;
      await transcriptionHub.sendChunk(slice);
    }
  },

  finalize: (patientId: string, format: string, language?: string): Promise<void> => {
    if (!transcriptionConnection) return Promise.reject(new Error("Not connected to transcription hub"));
    return transcriptionConnection.invoke("FinalizeStream", patientId, format, language ?? null);
  },

  onFinalTranscript: (cb: (text: string) => void): void => {
    transcriptionConnection?.on("FinalTranscript", cb);
  },

  onTranscriptionError: (cb: (message: string) => void): void => {
    transcriptionConnection?.on("TranscriptionError", cb);
  },
};

let progressConnection: signalR.HubConnection | null = null;

export const progressHub = {
  connect: async (): Promise<signalR.HubConnection> => {
    progressConnection = new signalR.HubConnectionBuilder()
      .withUrl(`${SIGNALR_URL}/progress`, { withCredentials: true })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    await progressConnection.start();
    return progressConnection;
  },

  disconnect: async (): Promise<void> => {
    if (progressConnection) {
      await progressConnection.stop();
      progressConnection = null;
    }
  },

  subscribeToJob: (jobId: string): void => {
    progressConnection?.invoke("SubscribeToJob", jobId).catch((e) => console.error("SubscribeToJob failed:", e));
  },

  onProgress: (cb: (jobId: string, progress: PipelineProgress) => void): void => {
    progressConnection?.on("JobProgress", cb);
  },

  onComplete: (cb: (jobId: string, noteId: string) => void): void => {
    progressConnection?.on("JobComplete", cb);
  },

  onError: (cb: (jobId: string, error: string) => void): void => {
    progressConnection?.on("JobError", cb);
  },
};
