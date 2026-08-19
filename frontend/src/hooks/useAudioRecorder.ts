"use client";

import { useState, useCallback, useRef } from "react";

export type RecordingState = "idle" | "recording" | "stopped";

interface UseAudioRecorderResult {
  state: RecordingState;
  durationMs: number;
  audioBlob: Blob | null;
  start: () => Promise<void>;
  stop: () => Promise<Blob | null>;
  reset: () => void;
  error: string | null;
}

// Picks the first MIME type the browser's MediaRecorder actually supports, preferring webm/opus
// (Chrome/Firefox/Edge) and falling back to mp4 (Safari).
function pickMimeType(): string {
  const candidates = ["audio/webm;codecs=opus", "audio/webm", "audio/mp4"];
  for (const type of candidates) {
    if (typeof MediaRecorder !== "undefined" && MediaRecorder.isTypeSupported(type)) return type;
  }
  return "";
}

// Manages microphone recording lifecycle (permissions, timer, in-memory blob) via the browser
// MediaRecorder API — the direct web equivalent of the source app's expo-av-based hook.
export function useAudioRecorder(): UseAudioRecorderResult {
  const [state, setState] = useState<RecordingState>("idle");
  const [durationMs, setDurationMs] = useState(0);
  const [audioBlob, setAudioBlob] = useState<Blob | null>(null);
  const [error, setError] = useState<string | null>(null);

  const recorderRef = useRef<MediaRecorder | null>(null);
  const streamRef = useRef<MediaStream | null>(null);
  const chunksRef = useRef<Blob[]>([]);
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const mimeTypeRef = useRef<string>("");

  const start = useCallback(async () => {
    try {
      setError(null);

      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      streamRef.current = stream;

      const mimeType = pickMimeType();
      mimeTypeRef.current = mimeType;
      const recorder = mimeType ? new MediaRecorder(stream, { mimeType }) : new MediaRecorder(stream);
      chunksRef.current = [];

      recorder.ondataavailable = (e) => {
        if (e.data.size > 0) chunksRef.current.push(e.data);
      };

      recorder.start();
      recorderRef.current = recorder;
      setState("recording");
      setDurationMs(0);

      timerRef.current = setInterval(() => {
        setDurationMs((prev) => prev + 1000);
      }, 1000);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Microphone permission denied or unavailable.");
    }
  }, []);

  const stop = useCallback((): Promise<Blob | null> => {
    return new Promise((resolve) => {
      const recorder = recorderRef.current;
      if (!recorder) {
        resolve(null);
        return;
      }

      if (timerRef.current) {
        clearInterval(timerRef.current);
        timerRef.current = null;
      }

      recorder.onstop = () => {
        const blob = new Blob(chunksRef.current, { type: mimeTypeRef.current || "audio/webm" });
        streamRef.current?.getTracks().forEach((t) => t.stop());
        streamRef.current = null;
        recorderRef.current = null;

        setState("stopped");
        setAudioBlob(blob);
        resolve(blob);
      };

      recorder.stop();
    });
  }, []);

  const reset = useCallback(() => {
    setState("idle");
    setDurationMs(0);
    setAudioBlob(null);
    setError(null);
    recorderRef.current = null;
  }, []);

  return { state, durationMs, audioBlob, start, stop, reset, error };
}
