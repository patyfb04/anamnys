import { useCallback, useEffect, useRef, useState } from "react";
import type { LiveLanguage, LiveNoteFormat } from "@anamnys/shared/lib/types";
import { liveSessionHub, type LiveMode } from "@/lib/signalr";
import { ChunkLog } from "@/lib/chunkLog";
import {
  AudioMixer,
  CaptureError,
  createWakeLock,
  displayAudioSource,
  isCapturing,
  isOnlineSource,
  microphoneErrorCode,
  openMicrophone,
  pauseWhenHidden,
  readCaptureEnv,
  shareErrorCode,
  transition,
  type AudioSource,
  type CaptureErrorCode,
  type CaptureEvent,
  type CaptureState,
  type RecordingSource,
} from "@/lib/audioCapture";

// suspended: the browser suspended the page or the audio ("Retomar?"); share_lost: the shared tab/screen stopped.
export type LiveState = "idle" | CaptureState | "finalizing" | "done" | "error";
export type LiveError =
  | "permission" | "device" | "connect" | "invalid" | "not_found" | "forbidden" | "archived" | "too_long"
  | "consent_required" | "dictation_not_confirmed" | "finalize"
  | "share_cancelled" | "share_without_audio" | "share_unsupported";
// A problem with a source during a recording that keeps the recording alive (share again, switch method).
export type SourceError = CaptureErrorCode | "switch_failed";

export interface StartOptions {
  patientId: string;
  appointmentId: string | null;
  format: LiveNoteFormat;
  language: LiveLanguage;
  mode: LiveMode;
  // The provider's confirmation that the patient is not being recorded (dictation mode only).
  dictationConfirmed: boolean;
  // Where the audio comes from; "in_person" for dictation.
  source: RecordingSource;
}

export interface SourceLevels {
  mic: number;
  // null when no call source is mixed (in person, the speaker method, a lost share).
  call: number | null;
}

const SAMPLES_PER_MS = 16;
const CHUNK_BYTES = 8192 * 2;
export const WARN_AT_MS = 85 * 60 * 1000;
export const MAX_MS = 90 * 60 * 1000;
const MAX_AUDIO_BYTES = 90 * 60 * 16000 * 2;
const LEVEL_POLL_MS = 100;
const FLUSH_TIMEOUT_MS = 3000;
const CALL = "call";
const MIC = "microphone";

type FatalCode = "invalid" | "not_found" | "forbidden" | "archived" | "consent_required" | "dictation_not_confirmed";
const codeOf = (e: unknown): string | undefined => (e as { code?: string } | null)?.code;
const isFatal = (c: string | undefined): c is FatalCode =>
  c === "invalid" || c === "not_found" || c === "forbidden" || c === "archived" || c === "consent_required" || c === "dictation_not_confirmed";
const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));
const isCaptureState = (s: LiveState): s is CaptureState =>
  s === "recording" || s === "paused" || s === "suspended" || s === "share_lost";

interface Chain { ctx: AudioContext; node: AudioWorkletNode; mixer: AudioMixer }

// Sources (microphone, call tab/screen) -> mixer -> worklet (PCM 16 kHz) -> ChunkLog -> TranscriptionHub,
// resending after reconnects. Audio lives only in memory.
export function useLiveRecorder() {
  const [state, setState] = useState<LiveState>("idle");
  const [connection, setConnection] = useState<"connected" | "connecting">("connected");
  const [samples, setSamples] = useState(0);
  const [level, setLevel] = useState(0);
  const [levels, setLevels] = useState<SourceLevels>({ mic: 0, call: null });
  const [error, setError] = useState<LiveError | null>(null);
  const [sourceError, setSourceError] = useState<SourceError | null>(null);
  const [source, setSource] = useState<RecordingSource | null>(null);
  // True while a share-again or a method switch is changing the sources.
  const [busy, setBusy] = useState(false);
  const [noteId, setNoteId] = useState<string | null>(null);
  const [env] = useState(readCaptureEnv);
  const [wakeLock] = useState(createWakeLock);

  const log = useRef(new ChunkLog());
  const recordingId = useRef<string>("");
  const opts = useRef<StartOptions | null>(null);
  const currentSource = useRef<RecordingSource>("in_person");
  // Every source used in this recording, in order of first use (to re-open an expired server buffer).
  const usedSources = useRef<RecordingSource[]>([]);
  const audio = useRef<Chain | null>(null);
  const stateRef = useRef<LiveState>("idle");
  const sending = useRef(false);
  // Bumped on start, reset, failure and unmount: loops started under an older value exit.
  const generation = useRef(0);
  // True while recorded audio is held and must still reach the server.
  const holding = useRef(false);
  const stopping = useRef(false);
  const reconnecting = useRef(false);
  // A share-again or method switch is in flight: the worklet stays paused until it ends.
  const switching = useRef(false);
  // The OS ended the microphone track (e.g. a phone call or app switch on mobile); re-opened on resume.
  const micLost = useRef(false);
  // A switch left the new method half-joined and not yet told to the server (see announcePending).
  const pendingAnnounce = useRef(false);
  // Generation of the most recent start(); an older start must not tear down its successor's hub.
  const latestStart = useRef(0);
  const stopRef = useRef<() => Promise<void>>(() => Promise.resolve());
  const reconnectRef = useRef<() => void>(() => undefined);

  const go = useCallback((s: LiveState) => {
    stateRef.current = s;
    setState(s);
  }, []);

  // Applies a capture event: the worklet records only in "recording". While sources are being changed the
  // worklet is left paused; the change re-syncs it when it ends.
  const step = useCallback((event: CaptureEvent) => {
    const s = stateRef.current;
    if (!isCaptureState(s)) return;
    const next = transition(s, event);
    if (!switching.current) audio.current?.node.port.postMessage(isCapturing(next) ? "resume" : "pause");
    if (!isCapturing(next)) setLevel(0);
    go(next);
  }, [go]);

  const setSwitching = useCallback((value: boolean) => {
    switching.current = value;
    setBusy(value);
  }, []);

  // Begin (or resume) the recording on the server with a source: the current one unless given.
  const begin = useCallback(async (source?: RecordingSource) => {
    const o = opts.current!;
    const ack = await liveSessionHub.begin(
      recordingId.current, o.patientId, o.appointmentId, o.format, o.language, o.mode, o.dictationConfirmed, source ?? currentSource.current,
    );
    if (ack.status !== "ok") throw Object.assign(new Error(ack.status), { code: ack.status });
  }, []);

  // Resumes the recording on the server after a reconnect or an expired buffer: one begin per source used, in
  // order of first use. A resume adds a source that is new to the buffer (without a switch audit), so a buffer
  // that was swept meanwhile is re-created with every source and the destruction record still lists them all.
  const reopen = useCallback(async () => {
    const sources = usedSources.current.length > 0 ? usedSources.current : [currentSource.current];
    for (const s of sources) await begin(s);
  }, [begin]);

  const noteSource = useCallback((s: RecordingSource) => {
    currentSource.current = s;
    if (!usedSources.current.includes(s)) usedSources.current = [...usedSources.current, s];
    setSource(s);
  }, []);

  // Stops every track of every source and disconnects every node.
  const releaseAudio = useCallback(() => {
    const a = audio.current;
    audio.current = null;
    wakeLock.release();
    if (!a) return;
    a.ctx.onstatechange = null;
    a.mixer.close();
    a.node.port.onmessage = null;
    a.node.disconnect();
    void a.ctx.close().catch(() => undefined);
  }, [wakeLock]);

  // Unrecoverable: stop everything, drop the audio.
  const fail = useCallback((code: LiveError) => {
    generation.current++;
    holding.current = false;
    sending.current = false;
    releaseAudio();
    log.current.clear();
    void liveSessionHub.disconnect();
    setError(code);
    go("error");
  }, [releaseAudio, go]);

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
          else if (ack.status === "buffer_expired") { log.current.resetAcks(); await reopen(); break; }
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
  }, [reopen, fail, failFor]);

  const connectHub = useCallback(() => liveSessionHub.connect(
    () => { setConnection("connected"); void reopen().then(() => pump()).catch(failFor); },
    () => setConnection("connecting"),
    () => reconnectRef.current(),
  ), [reopen, pump, failFor]);

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
          await reopen();
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
  }, [reopen, connectHub, failFor, pump]);

  useEffect(() => { reconnectRef.current = () => { void reconnect(); }; }, [reconnect]);

  // The shared tab/screen stopped: pause at once, never continue microphone-only.
  const shareEnded = useCallback(() => {
    audio.current?.mixer.remove(CALL);
    step("share_ended");
  }, [step]);

  const microphoneEnded = useCallback(() => {
    micLost.current = true;
    step("suspend");
  }, [step]);

  // Mixes the sources and wires their end events.
  const mix = useCallback(async (mixer: AudioMixer, mic: AudioSource | null, call: AudioSource | null) => {
    if (mic) {
      await mixer.add(mic);
      micLost.current = false;
      mic.onEnded(microphoneEnded);
    }
    if (call) {
      await mixer.add(call);
      call.onEnded(shareEnded);
    }
  }, [microphoneEnded, shareEnded]);

  // Tells the server about a method that is already in the mixer. Offline counts as ok: the reconnect re-opens
  // the buffer with every source used (noteSource adds this one).
  const announce = useCallback(async (target: RecordingSource, gone: () => boolean): Promise<"ok" | "invalid" | "forbidden" | "gone"> => {
    if (!liveSessionHub.isConnected()) return "ok";
    try {
      let ack = await liveSessionHub.switchSource(recordingId.current, target);
      if (!gone() && ack.status === "expired") {
        // The server dropped the buffer: re-open it with every source used so far, then add the new one.
        log.current.resetAcks();
        await reopen();
        if (!gone()) ack = await liveSessionHub.switchSource(recordingId.current, target);
      }
      if (gone()) return "gone";
      return ack.status === "forbidden" || ack.status === "invalid" ? ack.status : "ok";
    } catch (e) {
      // Connection trouble: the reconnect carries the source.
      failFor(e);
      return gone() ? "gone" : "ok";
    }
  }, [reopen, failFor]);

  // A method that joined the mixer during a switch that did not finish is announced before recording continues.
  // Returns whether recording may continue.
  const announcePending = useCallback(async (gone: () => boolean): Promise<boolean> => {
    if (!pendingAnnounce.current) return true;
    const target = currentSource.current;
    const told = await announce(target, gone);
    if (told === "gone") return false;
    if (told === "forbidden") { fail("forbidden"); return false; }
    if (told === "invalid") { setSourceError("switch_failed"); return false; }
    pendingAnnounce.current = false;
    noteSource(target);
    return true;
  }, [announce, fail, noteSource]);

  const start = useCallback(async (o: StartOptions) => {
    const g = ++generation.current;
    latestStart.current = g;
    const stale = () => generation.current !== g;
    setError(null);
    setSourceError(null);
    setNoteId(null);
    setSamples(0);
    setLevel(0);
    setLevels({ mic: 0, call: null });
    log.current.clear();
    sending.current = false;
    stopping.current = false;
    reconnecting.current = false;
    setSwitching(false);
    micLost.current = false;
    pendingAnnounce.current = false;
    recordingId.current = crypto.randomUUID();
    const initial: RecordingSource = o.mode === "dictation" ? "in_person" : o.source;
    opts.current = { ...o, source: initial };
    usedSources.current = [];
    noteSource(initial);
    // The share picker first: it needs the click's activation, which the other awaits would use up.
    let call: AudioSource | null = null;
    let mic: AudioSource | null = null;
    try {
      if (initial === "online_capture") call = await displayAudioSource();
      if (!stale()) mic = await openMicrophone(initial);
    } catch (e) {
      call?.stop();
      if (stale()) return;
      setError(e instanceof CaptureError ? e.code : "device");
      go("error");
      return;
    }
    if (stale() || !mic) {
      call?.stop();
      mic?.stop();
      return;
    }
    let ctx: AudioContext | null = null;
    let node: AudioWorkletNode | null = null;
    let mixer: AudioMixer | null = null;
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
      mixer = new AudioMixer(ctx, node);
      // From here on the mixer owns the sources.
      const m = mic, c = call;
      mic = null;
      call = null;
      await mix(mixer, m, c);
      if (stale()) throw Object.assign(new Error("stale"), { code: "stale" });
    } catch (e) {
      mixer?.close();
      mic?.stop();
      call?.stop();
      node?.disconnect();
      void ctx?.close().catch(() => undefined);
      if (stale()) {
        if (latestStart.current === g) void liveSessionHub.disconnect();
        return;
      }
      holding.current = false;
      void liveSessionHub.disconnect();
      const code = codeOf(e);
      setError(isFatal(code) ? code : phase === "audio" ? "device" : "connect");
      go("error");
      return;
    }
    const chain: Chain = { ctx, node, mixer };
    audio.current = chain;
    // The browser suspended the audio (phone screen off, app switch, another app took the audio).
    ctx.onstatechange = () => {
      if (audio.current === chain && (chain.ctx.state as string) !== "running" && chain.ctx.state !== "closed") step("suspend");
    };
    holding.current = true;
    setConnection("connected");
    go("recording");
    // The share may have stopped while the recording was starting.
    if (!mixer.has(CALL) && initial === "online_capture") step("share_ended");
    void wakeLock.acquire();
  }, [begin, connectHub, pump, mix, go, step, wakeLock, noteSource, setSwitching]);

  const pause = useCallback(() => step("pause"), [step]);

  // "Retomar" after a manual pause and "Retomar?" after the browser suspended the recording.
  const resume = useCallback(async () => {
    const a = audio.current;
    if (!a || stopping.current || (stateRef.current !== "paused" && stateRef.current !== "suspended")) return;
    const g = generation.current;
    const gone = () => generation.current !== g || audio.current !== a || stopping.current;
    setSourceError(null);
    if (a.ctx.state !== "running") await a.ctx.resume().catch(() => undefined);
    if (gone()) return;
    if (micLost.current) {
      let mic: AudioSource;
      try {
        mic = await openMicrophone(currentSource.current);
      } catch (e) {
        if (!gone()) setSourceError(microphoneErrorCode(e));
        return;
      }
      if (gone()) { mic.stop(); return; }
      try {
        await mix(a.mixer, mic, null);
      } catch {
        if (!gone()) setSourceError("device");
        return;
      }
    }
    if (gone()) return;
    if (!(await announcePending(gone)) || gone()) return;
    step("resume");
    void wakeLock.acquire();
  }, [announcePending, mix, step, wakeLock]);

  // "Compartilhar de novo": the same recording continues with the newly shared tab/screen.
  const reshare = useCallback(async () => {
    const a = audio.current;
    if (!a || stateRef.current !== "share_lost" || switching.current || stopping.current) return;
    const g = generation.current;
    // Stopped, restarted or torn down meanwhile: the new media is dropped and the held audio left alone.
    const gone = () => generation.current !== g || audio.current !== a || stopping.current || stateRef.current !== "share_lost";
    setSourceError(null);
    setSwitching(true);
    let call: AudioSource | null = null;
    let mic: AudioSource | null = null;
    const drop = () => { call?.stop(); mic?.stop(); };
    try {
      try {
        call = await displayAudioSource();
      } catch (e) {
        if (!gone()) setSourceError(shareErrorCode(e));
        return;
      }
      if (gone()) { drop(); return; }
      // The OS may also have ended the microphone while the share was lost.
      if (micLost.current) {
        try {
          mic = await openMicrophone(currentSource.current);
        } catch (e) {
          drop();
          if (!gone()) setSourceError(microphoneErrorCode(e));
          return;
        }
        if (gone()) { drop(); return; }
      }
      if (a.ctx.state !== "running") {
        await a.ctx.resume().catch(() => undefined);
        if (gone()) { drop(); return; }
      }
      const m = mic, c = call;
      mic = null;
      call = null;
      try {
        await mix(a.mixer, m, c);
      } catch {
        if (!gone()) setSourceError("share_cancelled");
        return;
      }
      if (gone()) return;
      if (!(await announcePending(gone)) || gone()) return;
      setSwitching(false);
      step("share_restored");
    } finally {
      if (switching.current) {
        setSwitching(false);
        // Re-sync the worklet with whatever state the recording is in now.
        if (!gone()) a.node.port.postMessage(isCapturing(stateRef.current as CaptureState) ? "resume" : "pause");
      }
    }
  }, [announcePending, mix, step, setSwitching]);

  // "Trocar método": pause, swap the call source and the microphone constraints, tell the server, continue the
  // same recording.
  const switchMethod = useCallback(async (target: RecordingSource) => {
    const from = currentSource.current;
    const a = audio.current;
    if (!a || switching.current || stopping.current || target === from || !isOnlineSource(target) || opts.current?.mode !== "session") return;
    if (!isCaptureState(stateRef.current)) return;
    const g = generation.current;
    // Stopped, restarted or torn down meanwhile: the new media is dropped and the held audio left alone.
    const gone = () => generation.current !== g || audio.current !== a || stopping.current;
    setSourceError(null);
    setSwitching(true);
    let call: AudioSource | null = null;
    let mic: AudioSource | null = null;
    const drop = () => { call?.stop(); mic?.stop(); call = null; mic = null; };
    let suspendAfter = false;
    let shareLostAfter = false;
    try {
      // The picker first, while the click's activation lasts.
      if (target === "online_capture") {
        try {
          call = await displayAudioSource();
        } catch (e) {
          if (!gone()) setSourceError(shareErrorCode(e));
          return;
        }
        if (gone()) { drop(); return; }
      }
      // Nothing is recorded while the sources change (step() leaves the worklet alone until the end).
      a.node.port.postMessage("pause");
      if (a.ctx.state !== "running") {
        await a.ctx.resume().catch(() => undefined);
        if (gone()) { drop(); return; }
      }
      // Release the old microphone before opening the new one: a device still open keeps its processing
      // (echo cancellation) for the next request.
      a.mixer.remove(MIC);
      try {
        mic = await openMicrophone(target);
      } catch (e) {
        drop();
        if (gone()) return;
        setSourceError(microphoneErrorCode(e));
        // Stay on the previous method with its own microphone.
        let previous: AudioSource | null = null;
        try {
          previous = await openMicrophone(from);
        } catch {
          previous = null;
        }
        if (gone()) { previous?.stop(); return; }
        if (previous) {
          try { await mix(a.mixer, previous, null); } catch { previous = null; }
        }
        if (!previous && !gone()) { micLost.current = true; suspendAfter = true; }
        return;
      }
      if (gone()) { drop(); return; }
      a.mixer.remove(CALL);
      const m = mic, c = call;
      mic = null;
      call = null;
      try {
        await mix(a.mixer, m, c);
      } catch {
        if (gone()) return;
        const micIn = a.mixer.has(MIC);
        const callIn = a.mixer.has(CALL);
        if (!micIn && !callIn) {
          // Nothing of the new method joined: recover on the previous one.
          micLost.current = true;
          if (from === "online_capture") shareLostAfter = true;
          else suspendAfter = true;
          return;
        }
        // Part of the new method joined. It is announced to the server only when recording continues
        // (Retomar / Compartilhar de novo), and never microphone-only for the capture method.
        currentSource.current = target;
        setSource(target);
        pendingAnnounce.current = true;
        if (!micIn) {
          micLost.current = true;
          suspendAfter = true;
        }
        if (target === "online_capture" && !callIn) shareLostAfter = true;
        return;
      }
      if (gone()) return;
      // Only now, with the new media in the mixer, does the server hear about the switch.
      const told = await announce(target, gone);
      if (told === "gone") return;
      if (told === "forbidden") { fail("forbidden"); return; }
      if (told === "invalid") {
        // The server refused: drop the new media and fall back to the previous method's recovery paths.
        a.mixer.remove(MIC);
        a.mixer.remove(CALL);
        micLost.current = true;
        setSourceError("switch_failed");
        if (from === "online_capture") shareLostAfter = true;
        else suspendAfter = true;
        return;
      }
      noteSource(target);
      pendingAnnounce.current = false;
      setSwitching(false);
      step("switched");
      void pump();
    } finally {
      if (switching.current) {
        setSwitching(false);
        if (!gone()) {
          // Re-sync the worklet with the state the recording is in now.
          a.node.port.postMessage(isCapturing(stateRef.current as CaptureState) ? "resume" : "pause");
          if (shareLostAfter) step("share_ended");
          else if (suspendAfter) step("suspend");
        }
      }
    }
  }, [announce, fail, mix, noteSource, pump, step, setSwitching]);

  const stop = useCallback(async () => {
    const a = audio.current;
    if (!a || stopping.current) return;
    const node = a.node;
    stopping.current = true;
    const g = generation.current;
    wakeLock.release();
    go("finalizing");
    // A suspended context may not deliver the flush.
    if (a.ctx.state !== "running") await a.ctx.resume().catch(() => undefined);
    await Promise.race([
      new Promise<void>((resolve) => {
        const previous = node.port.onmessage;
        node.port.onmessage = (e: MessageEvent) => {
          previous?.call(node.port, e);
          if (e.data.type === "flushed") resolve();
        };
        node.port.postMessage("flush");
      }),
      sleep(FLUSH_TIMEOUT_MS),
    ]);
    if (generation.current !== g) return;
    releaseAudio();
    setLevel(0);
    setLevels({ mic: 0, call: null });
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
            go("done");
            await liveSessionHub.disconnect();
            return;
          }
          log.current.resetAcks();
          await reopen();
        } catch (e) {
          failFor(e);
        }
      }
      await sleep(1000);
    }
  }, [reopen, failFor, pump, releaseAudio, go, wakeLock]);

  useEffect(() => { stopRef.current = stop; }, [stop]);

  // A chunk was refused for length: capture ends and the accepted audio is finalized.
  useEffect(() => {
    if (error === "too_long" && isCaptureState(state)) void stop();
  }, [error, state, stop]);

  // Per-source meters ("Você", "Chamada") while audio flows.
  useEffect(() => {
    if (state !== "recording") {
      setLevels((l) => (l.mic === 0 && (l.call ?? 0) === 0 ? l : { mic: 0, call: l.call === null ? null : 0 }));
      return;
    }
    const id = setInterval(() => {
      const l = audio.current?.mixer.levels() ?? {};
      setLevels({ mic: l.microphone ?? 0, call: l.call ?? null });
    }, LEVEL_POLL_MS);
    return () => clearInterval(id);
  }, [state]);

  // Phones suspend a hidden page: pause and ask "Retomar?" on return. The wake lock is re-requested when visible.
  useEffect(() => {
    const onVisibility = () => {
      if (!audio.current || !isCaptureState(stateRef.current)) return;
      if (document.visibilityState === "hidden") {
        if (pauseWhenHidden(env)) step("suspend");
      } else {
        void wakeLock.acquire();
      }
    };
    document.addEventListener("visibilitychange", onVisibility);
    return () => document.removeEventListener("visibilitychange", onVisibility);
  }, [env, step, wakeLock]);

  // "Gravar novamente": back to the prepare step. Only valid once no audio is held.
  const reset = useCallback(() => {
    generation.current++;
    holding.current = false;
    log.current.clear();
    void liveSessionHub.disconnect();
    setError(null);
    setSourceError(null);
    setNoteId(null);
    setSamples(0);
    setLevel(0);
    setSource(null);
    go("idle");
  }, [go]);

  const elapsedMs = samples / SAMPLES_PER_MS;

  useEffect(() => () => {
    generation.current++;
    holding.current = false;
    releaseAudio();
    log.current.clear();
    void liveSessionHub.disconnect();
  }, [releaseAudio]);

  return {
    state, connection, elapsedMs, level, levels, source, sourceError, error, noteId, busy,
    start, pause, resume, stop, reset, reshare, switchMethod,
  };
}
