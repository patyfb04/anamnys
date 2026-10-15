import { useCallback, useEffect, useRef, useState } from "react";
import type { LiveLanguage, LiveNoteFormat } from "@anamnys/shared/lib/types";
import { liveSessionHub } from "@/lib/signalr";
import { ChunkLog } from "@/lib/chunkLog";

export type LiveState = "idle" | "recording" | "paused" | "finalizing" | "done" | "error";
export type LiveError = "permission" | "device" | "connect" | "invalid" | "not_found" | "forbidden" | "too_long" | "finalize";

export interface StartOptions {
  patientId: string;
  appointmentId: string | null;
  format: LiveNoteFormat;
  language: LiveLanguage;
}

const SAMPLES_PER_MS = 16;
const CHUNK_BYTES = 8192 * 2;
export const WARN_AT_MS = 85 * 60 * 1000;
export const MAX_MS = 90 * 60 * 1000;
const MAX_AUDIO_BYTES = 90 * 60 * 16000 * 2;

type FatalCode = "invalid" | "not_found" | "forbidden";
const codeOf = (e: unknown): string | undefined => (e as { code?: string } | null)?.code;
const isFatal = (c: string | undefined): c is FatalCode => c === "invalid" || c === "not_found" || c === "forbidden";
const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

// Mic -> worklet (PCM 16 kHz) -> ChunkLog -> TranscriptionHub, resending after reconnects.
export function useLiveRecorder() {
  const [state, setState] = useState<LiveState>("idle");
  const [connection, setConnection] = useState<"connected" | "connecting">("connected");
  const [samples, setSamples] = useState(0);
  const [level, setLevel] = useState(0);
  const [error, setError] = useState<LiveError | null>(null);
  const [noteId, setNoteId] = useState<string | null>(null);

  const log = useRef(new ChunkLog());
  const recordingId = useRef<string>("");
  const opts = useRef<StartOptions | null>(null);
  const audio = useRef<{ ctx: AudioContext; stream: MediaStream; node: AudioWorkletNode } | null>(null);
  const sending = useRef(false);
  // Bumped on start, reset, failure and unmount: loops started under an older value exit.
  const generation = useRef(0);
  // True while recorded audio is held and must still reach the server.
  const holding = useRef(false);
  const stopping = useRef(false);
  const reconnecting = useRef(false);
  // Generation of the most recent start(); an older start must not tear down its successor's hub.
  const latestStart = useRef(0);
  const stopRef = useRef<() => Promise<void>>(() => Promise.resolve());
  const reconnectRef = useRef<() => void>(() => undefined);

  const begin = useCallback(async () => {
    const o = opts.current!;
    const ack = await liveSessionHub.begin(recordingId.current, o.patientId, o.appointmentId, o.format, o.language);
    if (ack.status !== "ok") throw Object.assign(new Error(ack.status), { code: ack.status });
  }, []);

  const releaseAudio = useCallback(() => {
    audio.current?.node.disconnect();
    audio.current?.stream.getTracks().forEach((t) => t.stop());
    void audio.current?.ctx.close();
    audio.current = null;
  }, []);

  // Unrecoverable: stop everything, drop the audio.
  const fail = useCallback((code: LiveError) => {
    generation.current++;
    holding.current = false;
    sending.current = false;
    releaseAudio();
    log.current.clear();
    void liveSessionHub.disconnect();
    setError(code);
    setState("error");
  }, [releaseAudio]);

  // Server-side refusals are fatal; anything else is connection trouble and retried.
  const failFor = useCallback((e: unknown) => {
    const code = codeOf(e);
    if (isFatal(code)) fail(code);
  }, [fail]);

  // Sends every pending chunk in order; one loop at a time.
  const pump = useCallback(async () => {
    if (sending.current || !liveSessionHub.isConnected()) return;
    const g = generation.current;
    sending.current = true;
    try {
      for (let next = log.current.pending(); next.length > 0 && generation.current === g; next = log.current.pending()) {
        for (const { seq, bytes } of next) {
          const ack = await liveSessionHub.sendChunk(recordingId.current, seq, bytes);
          if (generation.current !== g) return;
          if (ack.status === "accepted" || ack.status === "duplicate") log.current.ack(seq);
          else if (ack.status === "gap") { log.current.rewindTo(ack.lastSeq); break; }
          else if (ack.status === "buffer_expired") { log.current.resetAcks(); await begin(); break; }
          else if (ack.status === "too_long") {
            log.current.truncateAfter(Math.max(ack.lastSeq, log.current.lastAcked));
            setSamples(log.current.bytes / 2);
            setError("too_long");
            return;
          } else if (ack.status === "forbidden") { fail("forbidden"); return; }
          else throw new Error(ack.status);
        }
      }
    } catch (e) {
      // Connection trouble: the reconnect paths call pump again.
      if (generation.current === g) failFor(e);
    } finally {
      if (generation.current === g) sending.current = false;
    }
  }, [begin, fail, failFor]);

  const connectHub = useCallback(() => liveSessionHub.connect(
    () => { setConnection("connected"); void begin().then(() => pump()).catch(failFor); },
    () => setConnection("connecting"),
    () => reconnectRef.current(),
  ), [begin, pump, failFor]);

  // withAutomaticReconnect gave up: keep trying while audio is still held.
  const reconnect = useCallback(async () => {
    setConnection("connecting");
    if (!holding.current || reconnecting.current) return;
    reconnecting.current = true;
    const g = generation.current;
    let delay = 2000;
    try {
      while (holding.current && generation.current === g) {
        await sleep(delay);
        if (!holding.current || generation.current !== g) break;
        try {
          await connectHub();
          if (generation.current !== g) return;
          await begin();
          if (generation.current !== g) return;
          setConnection("connected");
          void pump();
          return;
        } catch (e) {
          failFor(e);
          delay = Math.min(delay * 2, 30000);
        }
      }
    } finally {
      reconnecting.current = false;
    }
  }, [begin, connectHub, failFor, pump]);

  useEffect(() => { reconnectRef.current = () => { void reconnect(); }; }, [reconnect]);

  const start = useCallback(async (o: StartOptions) => {
    const g = ++generation.current;
    latestStart.current = g;
    const stale = () => generation.current !== g;
    setError(null);
    setNoteId(null);
    setSamples(0);
    setLevel(0);
    log.current.clear();
    sending.current = false;
    stopping.current = false;
    reconnecting.current = false;
    recordingId.current = crypto.randomUUID();
    opts.current = o;
    let stream: MediaStream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } });
    } catch (e) {
      if (stale()) return;
      setError(e instanceof DOMException && e.name === "NotAllowedError" ? "permission" : "device");
      setState("error");
      return;
    }
    if (stale()) {
      stream.getTracks().forEach((t) => t.stop());
      return;
    }
    let ctx: AudioContext | null = null;
    let node: AudioWorkletNode | null = null;
    let phase: "connect" | "audio" = "connect";
    try {
      await connectHub();
      if (stale()) throw Object.assign(new Error("stale"), { code: "stale" });
      await begin();
      if (stale()) throw Object.assign(new Error("stale"), { code: "stale" });
      phase = "audio";
      ctx = new AudioContext();
      // Created after awaits, so the browser may have left it suspended.
      await ctx.resume();
      if (ctx.state !== "running") throw new Error("audio context not running");
      await ctx.audioWorklet.addModule(`${import.meta.env.BASE_URL}worklets/pcm16-recorder.js`);
      node = new AudioWorkletNode(ctx, "pcm16-recorder");
      if (stale()) throw Object.assign(new Error("stale"), { code: "stale" });
      node.port.onmessage = (e: MessageEvent) => {
        if (e.data.type === "chunk") {
          const bytes = new Uint8Array(e.data.buffer as ArrayBuffer);
          log.current.add(bytes);
          setSamples((s) => s + bytes.byteLength / 2);
          void pump();
          // The server refuses any chunk past the limit; stop one chunk short of it.
          if (log.current.bytes + CHUNK_BYTES > MAX_AUDIO_BYTES) void stopRef.current();
        } else if (e.data.type === "level") {
          setLevel(e.data.peak as number);
        }
      };
      ctx.createMediaStreamSource(stream).connect(node);
    } catch (e) {
      node?.disconnect();
      stream.getTracks().forEach((t) => t.stop());
      void ctx?.close();
      if (stale()) {
        if (latestStart.current === g) void liveSessionHub.disconnect();
        return;
      }
      holding.current = false;
      void liveSessionHub.disconnect();
      const code = codeOf(e);
      setError(isFatal(code) ? code : phase === "audio" ? "device" : "connect");
      setState("error");
      return;
    }
    audio.current = { ctx, stream, node };
    holding.current = true;
    setConnection("connected");
    setState("recording");
  }, [begin, connectHub, pump]);

  const pause = useCallback(() => { audio.current?.node.port.postMessage("pause"); setLevel(0); setState("paused"); }, []);
  const resume = useCallback(() => { audio.current?.node.port.postMessage("resume"); setState("recording"); }, []);

  const stop = useCallback(async () => {
    const node = audio.current?.node;
    if (!node || stopping.current) return;
    stopping.current = true;
    const g = generation.current;
    setState("finalizing");
    await new Promise<void>((resolve) => {
      const previous = node.port.onmessage;
      node.port.onmessage = (e: MessageEvent) => {
        previous?.call(node.port, e);
        if (e.data.type === "flushed") resolve();
      };
      node.port.postMessage("flush");
    });
    if (generation.current !== g) return;
    releaseAudio();
    setLevel(0);
    // Keep trying until every chunk is accepted and finalize answers; the audio stays in memory meanwhile.
    while (generation.current === g) {
      await pump();
      if (generation.current !== g) return;
      if (log.current.pending().length === 0 && liveSessionHub.isConnected()) {
        try {
          const ack = await liveSessionHub.finalize(recordingId.current);
          if (generation.current !== g) return;
          if (ack.status === "ok" && ack.noteId) {
            log.current.clear();
            holding.current = false;
            setNoteId(ack.noteId);
            setState("done");
            await liveSessionHub.disconnect();
            return;
          }
          log.current.resetAcks();
          await begin();
        } catch (e) {
          failFor(e);
        }
      }
      await sleep(1000);
    }
  }, [begin, failFor, pump, releaseAudio]);

  useEffect(() => { stopRef.current = stop; }, [stop]);

  // A chunk was refused for length: capture ends and the accepted audio is finalized.
  useEffect(() => {
    if (error === "too_long" && (state === "recording" || state === "paused")) void stop();
  }, [error, state, stop]);

  // "Gravar novamente": back to the prepare step. Only valid once no audio is held.
  const reset = useCallback(() => {
    generation.current++;
    holding.current = false;
    log.current.clear();
    void liveSessionHub.disconnect();
    setError(null);
    setNoteId(null);
    setSamples(0);
    setLevel(0);
    setState("idle");
  }, []);

  const elapsedMs = samples / SAMPLES_PER_MS;

  useEffect(() => () => {
    generation.current++;
    holding.current = false;
    releaseAudio();
    log.current.clear();
    void liveSessionHub.disconnect();
  }, [releaseAudio]);

  return { state, connection, elapsedMs, level, error, noteId, start, pause, resume, stop, reset };
}
