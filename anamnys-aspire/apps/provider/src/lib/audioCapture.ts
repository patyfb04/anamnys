// Audio capture for Sessão ao Vivo with pluggable sources (F-25,
// design/specs/2026-11-16-transient-audio-tab-capture-design.md §3).
//
// The recording chain is AudioContext -> AudioMixer (one GainNode per source, summed into a mono bus) ->
// the pcm16-recorder AudioWorkletNode. Sources can be added and removed on the running chain without restarting
// the recording; H2 will add a remote-track source (its own room) the same way.
//
// Everything stays in memory: no IndexedDB, no localStorage. Every track is stopped and every node disconnected
// when a source is removed or the mixer closed.
//
// The decision logic (support detection, constraints, state transitions) is pure so it can be unit-tested
// without a browser.
import type { RecordingSource } from "@anamnys/shared/lib/types";

export type { RecordingSource };

// ---------------------------------------------------------------------------------------------------------------
// Choices

// Where the session happens; dictation is always "in_person" (microphone only).
export type SessionPlace = "in_person" | "online";
// How an online session's call audio is captured: directly from the call's tab/screen, or from the loudspeaker.
export type OnlineMethod = "capture" | "speaker";
export type RecordingModeKind = "session" | "dictation";

export function sourceFor(mode: RecordingModeKind, place: SessionPlace, method: OnlineMethod): RecordingSource {
  if (mode === "dictation" || place === "in_person") return "in_person";
  return method === "capture" ? "online_capture" : "online_speaker";
}

export const isOnlineSource = (s: RecordingSource): boolean => s === "online_capture" || s === "online_speaker";

// The default method: direct capture where the browser can do it, the speaker method otherwise.
export function defaultMethod(support: CaptureSupport): OnlineMethod {
  return support.tab ? "capture" : "speaker";
}

// The place preselected from the linked appointment's modality ("Presencial" without one).
export function placeFromModality(modality: string | null | undefined): SessionPlace {
  return modality === "online" ? "online" : "in_person";
}

// ---------------------------------------------------------------------------------------------------------------
// Support detection

export interface CaptureEnv {
  hasGetDisplayMedia: boolean;
  mobile: boolean;
  chromium: boolean;
  windows: boolean;
}

// Tab audio: Chromium desktop only (Firefox and Safari share no audio; mobile browsers have no getDisplayMedia
// audio). System audio with "entire screen": Chromium on Windows only.
export interface CaptureSupport {
  tab: boolean;
  systemAudio: boolean;
}

export function captureSupport(env: CaptureEnv = readCaptureEnv()): CaptureSupport {
  const tab = env.hasGetDisplayMedia && env.chromium && !env.mobile;
  return { tab, systemAudio: tab && env.windows };
}

interface UADataLike {
  brands?: { brand: string }[];
  mobile?: boolean;
  platform?: string;
}

// Pure: derives the environment from what the navigator reports.
export function envFrom(input: {
  userAgent: string;
  uaData?: UADataLike | null;
  platform?: string;
  maxTouchPoints?: number;
  hasGetDisplayMedia: boolean;
}): CaptureEnv {
  const ua = input.userAgent;
  const data = input.uaData ?? null;
  const brands = data?.brands?.map((b) => b.brand) ?? [];
  const chromium = brands.length > 0
    ? brands.some((b) => b === "Chromium" || b === "Google Chrome" || b === "Microsoft Edge")
    : /(Chrome|Chromium|Edg)\//.test(ua) && !/(Firefox|FxiOS|CriOS|EdgiOS|OPiOS)\//.test(ua);
  // iPadOS reports a Mac user agent; a touch screen gives it away.
  const iPadOs = /Macintosh/.test(ua) && (input.maxTouchPoints ?? 0) > 1;
  const mobile = data?.mobile ?? (/Android|iPhone|iPad|iPod|Mobile/i.test(ua) || iPadOs);
  const windows = data?.platform ? data.platform === "Windows" : /Windows/.test(ua) || /^Win/.test(input.platform ?? "");
  return { hasGetDisplayMedia: input.hasGetDisplayMedia, mobile, chromium, windows };
}

export function readCaptureEnv(): CaptureEnv {
  if (typeof navigator === "undefined") return { hasGetDisplayMedia: false, mobile: false, chromium: false, windows: false };
  return envFrom({
    userAgent: navigator.userAgent,
    uaData: (navigator as Navigator & { userAgentData?: UADataLike }).userAgentData ?? null,
    platform: navigator.platform,
    maxTouchPoints: navigator.maxTouchPoints,
    hasGetDisplayMedia: typeof navigator.mediaDevices?.getDisplayMedia === "function",
  });
}

// ---------------------------------------------------------------------------------------------------------------
// Constraints

// In person and with direct capture the microphone keeps the browser's processing. The speaker method turns echo
// cancellation and noise suppression off: they would erase the patient's voice coming from the loudspeaker.
export function microphoneConstraints(source: RecordingSource): MediaTrackConstraints {
  return source === "online_speaker"
    ? { channelCount: 1, echoCancellation: false, noiseSuppression: false, autoGainControl: true }
    : { channelCount: 1, echoCancellation: true, noiseSuppression: true };
}

// Chromium's extra getDisplayMedia options; not every lib.dom version declares them.
type DisplayAudioOptions = DisplayMediaStreamOptions & {
  systemAudio?: "include" | "exclude";
  selfBrowserSurface?: "include" | "exclude";
  preferCurrentTab?: boolean;
};

export const DISPLAY_MEDIA_OPTIONS: DisplayAudioOptions = {
  video: true,
  audio: { echoCancellation: false, noiseSuppression: false },
  systemAudio: "include",
  selfBrowserSurface: "exclude",
  preferCurrentTab: false,
};

// ---------------------------------------------------------------------------------------------------------------
// Errors

export type CaptureErrorCode =
  | "permission" // microphone refused
  | "device" // no microphone / could not open it
  | "share_cancelled" // the browser's share picker was closed or refused
  | "share_without_audio" // shared without ticking "Compartilhar áudio"
  | "share_unsupported"; // this browser cannot capture the call's audio

export class CaptureError extends Error {
  readonly code: CaptureErrorCode;
  constructor(code: CaptureErrorCode) {
    super(code);
    this.code = code;
  }
}

export function microphoneErrorCode(e: unknown): CaptureErrorCode {
  if (e instanceof CaptureError) return e.code;
  return e instanceof DOMException && (e.name === "NotAllowedError" || e.name === "SecurityError") ? "permission" : "device";
}

export function shareErrorCode(e: unknown): CaptureErrorCode {
  if (e instanceof CaptureError) return e.code;
  if (e instanceof DOMException && e.name === "NotSupportedError") return "share_unsupported";
  return "share_cancelled";
}

// ---------------------------------------------------------------------------------------------------------------
// State machine of a capture in progress (the recorder adds idle/finalizing/done/error around it)

export type CaptureState = "recording" | "paused" | "suspended" | "share_lost";

export type CaptureEvent =
  | "pause" // the provider paused
  | "resume" // the provider resumed (also "Retomar?" after a suspension)
  | "suspend" // the browser suspended the page or the audio context
  | "share_ended" // the shared tab/screen stopped
  | "share_restored" // shared again
  | "switched"; // a new method is in place

// Share loss wins over everything (never silently microphone-only); a suspension only interrupts a live
// recording; a lost share comes back only by sharing again or switching method.
export function transition(state: CaptureState, event: CaptureEvent): CaptureState {
  switch (event) {
    case "share_ended":
      return "share_lost";
    case "pause":
      return state === "recording" ? "paused" : state;
    case "suspend":
      return state === "recording" ? "suspended" : state;
    case "resume":
      return state === "paused" || state === "suspended" ? "recording" : state;
    case "share_restored":
      return state === "share_lost" ? "recording" : state;
    case "switched":
      return state === "paused" ? "paused" : "recording";
  }
}

// Whether audio flows to the worklet in this state.
export const isCapturing = (s: CaptureState): boolean => s === "recording";

// Whether a page that became hidden must pause. On desktop the provider is expected to look at the call's tab
// while recording, so a hidden page keeps recording (only a suspended audio context pauses it); on mobile a
// hidden page is about to be suspended by the browser.
export const pauseWhenHidden = (env: CaptureEnv): boolean => env.mobile;

// The method switches offered during an online recording.
export function switchTargets(current: RecordingSource, support: CaptureSupport): RecordingSource[] {
  if (current === "in_person") return [];
  if (current === "online_capture") return ["online_speaker"];
  return support.tab ? ["online_capture"] : [];
}

// ---------------------------------------------------------------------------------------------------------------
// Sources

export type SourceKind = "microphone" | "call";

export interface AudioSource {
  readonly id: string;
  readonly kind: SourceKind;
  // Builds this source's nodes on the context and returns the node to mix.
  start(ctx: AudioContext): Promise<AudioNode>;
  // Stops every track and disconnects every node; idempotent.
  stop(): void;
  // Called once when the source ends by itself (e.g. the user stopped sharing); never after stop().
  onEnded(cb: () => void): void;
  // Peak level of the last few milliseconds, 0..1.
  level(): number;
  // The settings the browser actually applied to the audio track (null once stopped).
  settings(): MediaTrackSettings | null;
}

function streamSource(id: string, kind: SourceKind, stream: MediaStream): AudioSource {
  let node: MediaStreamAudioSourceNode | null = null;
  let analyser: AnalyserNode | null = null;
  let buffer: Float32Array<ArrayBuffer> | null = null;
  let stopped = false;
  let ended: (() => void) | null = null;
  const fire = () => {
    if (stopped) return;
    const cb = ended;
    ended = null;
    cb?.();
  };
  stream.getAudioTracks().forEach((t) => t.addEventListener("ended", fire));
  return {
    id,
    kind,
    start: async (ctx) => {
      if (stopped) throw new Error("source stopped");
      node = ctx.createMediaStreamSource(stream);
      analyser = ctx.createAnalyser();
      analyser.fftSize = 512;
      buffer = new Float32Array(analyser.fftSize);
      node.connect(analyser);
      return node;
    },
    stop: () => {
      if (stopped) return;
      stopped = true;
      ended = null;
      stream.getAudioTracks().forEach((t) => t.removeEventListener("ended", fire));
      node?.disconnect();
      analyser?.disconnect();
      node = null;
      analyser = null;
      buffer = null;
      stream.getTracks().forEach((t) => t.stop());
    },
    onEnded: (cb) => {
      ended = cb;
      // Ended before anyone listened (e.g. the share stopped while the recording was starting).
      if (stream.getAudioTracks().some((t) => t.readyState === "ended")) fire();
    },
    level: () => {
      if (!analyser || !buffer) return 0;
      analyser.getFloatTimeDomainData(buffer);
      let peak = 0;
      for (let i = 0; i < buffer.length; i++) peak = Math.max(peak, Math.abs(buffer[i]));
      return peak;
    },
    settings: () => (stopped ? null : stream.getAudioTracks()[0]?.getSettings() ?? null),
  };
}

// Whether the processing the browser applied matches what was asked. A device that is still open elsewhere keeps
// its processing settings, and a plain (non-exact) request is then satisfied with them; for the speaker method an
// echo canceller left on erases the patient's voice. Unknown settings count as a match.
export function processingMatches(settings: MediaTrackSettings | null, wanted: MediaTrackConstraints): boolean {
  if (!settings) return true;
  for (const key of ["echoCancellation", "noiseSuppression"] as const) {
    const w = wanted[key];
    const s = settings[key];
    if (typeof w === "boolean" && typeof s === "boolean" && s !== w) return false;
  }
  return true;
}

// The constraints with the processing flags made exact, for the second request after a mismatch.
export function exactProcessing(wanted: MediaTrackConstraints): MediaTrackConstraints {
  const out: MediaTrackConstraints = { ...wanted };
  if (typeof wanted.echoCancellation === "boolean") out.echoCancellation = { exact: wanted.echoCancellation };
  if (typeof wanted.noiseSuppression === "boolean") out.noiseSuppression = { exact: wanted.noiseSuppression };
  return out;
}

// Opens the microphone for a source and checks the applied processing; on a mismatch it asks again with exact
// constraints (and keeps the plain stream if the device cannot honour them). Release any previous microphone first.
export async function openMicrophone(source: RecordingSource): Promise<AudioSource> {
  const wanted = microphoneConstraints(source);
  const mic = await microphoneSource(wanted);
  if (processingMatches(mic.settings(), wanted)) return mic;
  mic.stop();
  try {
    return await microphoneSource(exactProcessing(wanted));
  } catch {
    return microphoneSource(wanted);
  }
}

// The provider's microphone (in person, dictation, the speaker method with echo cancellation off).
export async function microphoneSource(constraints: MediaTrackConstraints): Promise<AudioSource> {
  let stream: MediaStream;
  try {
    stream = await navigator.mediaDevices.getUserMedia({ audio: constraints });
  } catch (e) {
    throw new CaptureError(microphoneErrorCode(e));
  }
  return streamSource("microphone", "microphone", stream);
}

// The call's audio: a browser tab, or the entire screen with system audio (Chromium on Windows). Must be called
// from a user gesture, before any other await, so the browser keeps the click's activation.
export async function displayAudioSource(): Promise<AudioSource> {
  if (typeof navigator.mediaDevices?.getDisplayMedia !== "function") throw new CaptureError("share_unsupported");
  let stream: MediaStream;
  try {
    stream = await navigator.mediaDevices.getDisplayMedia(DISPLAY_MEDIA_OPTIONS);
  } catch (e) {
    throw new CaptureError(shareErrorCode(e));
  }
  // Only the sound is wanted: the picture is dropped at once.
  stream.getVideoTracks().forEach((t) => {
    t.stop();
    stream.removeTrack(t);
  });
  if (stream.getAudioTracks().length === 0) {
    stream.getTracks().forEach((t) => t.stop());
    throw new CaptureError("share_without_audio");
  }
  return streamSource("call", "call", stream);
}

// ---------------------------------------------------------------------------------------------------------------
// Mixer

export class AudioMixer {
  private readonly ctx: AudioContext;
  private readonly bus: GainNode;
  private readonly entries = new Map<string, { source: AudioSource; input: AudioNode; gain: GainNode }>();
  private closed = false;

  // Mixes every source into one mono bus connected to `destination` (the recorder worklet).
  constructor(ctx: AudioContext, destination: AudioNode) {
    this.ctx = ctx;
    this.bus = ctx.createGain();
    this.bus.channelCount = 1;
    this.bus.channelCountMode = "explicit";
    this.bus.channelInterpretation = "speakers";
    this.bus.connect(destination);
  }

  // Adds (or replaces, by id) a source on the running chain; the recording is not restarted.
  // A closed mixer stops the source and throws, so nothing is left open without an owner.
  async add(source: AudioSource): Promise<void> {
    this.remove(source.id);
    let input: AudioNode;
    try {
      if (this.closed) throw new Error("mixer closed");
      input = await source.start(this.ctx);
      if (this.closed) throw new Error("mixer closed");
    } catch (e) {
      source.stop();
      throw e;
    }
    const gain = this.ctx.createGain();
    input.connect(gain);
    gain.connect(this.bus);
    this.entries.set(source.id, { source, input, gain });
  }

  // Disconnects the source and stops its tracks.
  remove(id: string): void {
    const entry = this.entries.get(id);
    if (!entry) return;
    this.entries.delete(id);
    entry.input.disconnect();
    entry.gain.disconnect();
    entry.source.stop();
  }

  has(id: string): boolean {
    return this.entries.has(id);
  }

  ids(): string[] {
    return [...this.entries.keys()];
  }

  levels(): Partial<Record<SourceKind, number>> {
    const out: Partial<Record<SourceKind, number>> = {};
    for (const { source } of this.entries.values()) out[source.kind] = Math.max(out[source.kind] ?? 0, source.level());
    return out;
  }

  // Removes every source and detaches the bus.
  close(): void {
    this.closed = true;
    for (const id of this.ids()) this.remove(id);
    this.bus.disconnect();
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Screen Wake Lock

// Keeps the screen awake while recording where the browser supports it; re-acquire after the page is visible
// again (the browser releases it when the page is hidden).
export function createWakeLock() {
  let sentinel: WakeLockSentinel | null = null;
  // One request at a time: overlapping acquire() calls share it, so no sentinel is left unreleased.
  let pending: Promise<void> | null = null;
  let wanted = false;
  return {
    acquire: (): Promise<void> => {
      wanted = true;
      if (sentinel && !sentinel.released) return Promise.resolve();
      if (pending) return pending;
      if (!("wakeLock" in navigator) || document.visibilityState !== "visible") return Promise.resolve();
      pending = (async () => {
        try {
          const s = await navigator.wakeLock.request("screen");
          if (!wanted) void s.release().catch(() => undefined);
          else sentinel = s;
        } catch {
          // Denied (battery saver, permissions policy): recording goes on without it.
        } finally {
          pending = null;
        }
      })();
      return pending;
    },
    release: (): void => {
      wanted = false;
      const s = sentinel;
      sentinel = null;
      if (s && !s.released) void s.release().catch(() => undefined);
    },
  };
}
