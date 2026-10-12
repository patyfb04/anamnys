import { useEffect, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Info, Mic, Pause, Play, Square } from "lucide-react";
import { MAX_MS, WARN_AT_MS, type LiveState } from "@/hooks/useLiveRecorder";

const BARS = 32;

interface Props {
  state: LiveState;
  connection: "connected" | "connecting";
  elapsedMs: number;
  level: number;
  canStart: boolean;
  onStart: () => void;
  onPause: () => void;
  onResume: () => void;
  onStop: () => void;
  // Shown instead of the controls while the recording is being sent and transcribed.
  processing?: ReactNode;
}

function clock(ms: number): string {
  const total = Math.floor(ms / 1000);
  const h = Math.floor(total / 3600);
  const m = String(Math.floor((total % 3600) / 60)).padStart(2, "0");
  const s = String(total % 60).padStart(2, "0");
  return h > 0 ? `${h}:${m}:${s}` : `${m}:${s}`;
}

// The recording panel: status pill, large timer, a rolling level meter and the controls.
export default function RecordingCard(props: Props) {
  const { state, connection, elapsedMs, level, canStart, onStart, onPause, onResume, onStop, processing } = props;
  const { t } = useTranslation();
  const live = state === "recording" || state === "paused";
  const [history, setHistory] = useState<number[]>(() => Array(BARS).fill(0));

  // Keeps the last BARS peaks while recording so the meter scrolls like a waveform.
  useEffect(() => {
    if (state !== "recording") return;
    setHistory((h) => [...h.slice(1), level]);
  }, [level, state]);

  const status = processing
    ? { label: t("liveSession.status.processing"), tone: "bg-surfaceContainerHighest text-onSurfaceVariant" }
    : state === "recording" && connection === "connecting"
      ? { label: t("liveSession.status.reconnecting"), tone: "bg-errorContainer text-onErrorContainer" }
      : state === "recording"
        ? { label: t("liveSession.status.recording"), tone: "bg-errorContainer text-onErrorContainer" }
        : state === "paused"
          ? { label: t("liveSession.status.paused"), tone: "bg-surfaceContainerHighest text-onSurfaceVariant" }
          : { label: t("liveSession.status.ready"), tone: "bg-primaryFixed text-onPrimaryFixedVariant" };

  return (
    <section className="bg-surfaceContainerLowest rounded-radii-xl border border-outlineVariant p-5 shadow-sm md:p-6">
      <div className="flex flex-wrap items-center gap-4">
        <span className={`inline-flex items-center gap-2 rounded-radii-full px-3 py-1 text-label-md uppercase ${status.tone}`}>
          {state === "recording" && !processing && (
            <span aria-hidden className="h-2 w-2 animate-pulse rounded-full bg-current" />
          )}
          {status.label}
        </span>
        <p className="text-display-sm tabular-nums text-onSurface" aria-live="off">
          {clock(elapsedMs)}
          <span className="ml-2 text-body-md text-onSurfaceVariant">{t("liveSession.ofMax", { max: clock(MAX_MS) })}</span>
        </p>
      </div>

      <div className="mt-5 flex h-24 items-center gap-1 rounded-radii-lg bg-surfaceContainerLow px-4" aria-hidden>
        {history.map((v, i) => (
          <span
            key={i}
            className={`flex-1 rounded-full transition-[height] duration-100 ${live ? "bg-primary" : "bg-outlineVariant"}`}
            style={{ height: `${Math.max(6, Math.min(100, v * 140))}%`, opacity: 0.35 + (i / BARS) * 0.65 }}
          />
        ))}
      </div>

      {connection === "connecting" && live && (
        <p className="mt-3 text-body-sm text-error">{t("liveSession.connecting")}</p>
      )}
      {elapsedMs >= WARN_AT_MS && live && <p className="mt-3 text-body-sm text-error">{t("liveSession.warnLimit")}</p>}

      <div className="mt-5">
        {processing ?? (
          <div className="flex flex-wrap items-center gap-3">
            {!live && (
              <button
                type="button"
                onClick={onStart}
                disabled={!canStart}
                className="inline-flex items-center gap-2 rounded-radii-lg bg-primary px-5 py-3 text-label-lg text-onPrimary disabled:opacity-60"
              >
                <Mic size={18} aria-hidden />
                {t("liveSession.start")}
              </button>
            )}
            {state === "recording" && (
              <button
                type="button"
                onClick={onPause}
                className="inline-flex items-center gap-2 rounded-radii-lg border border-outlineVariant px-4 py-3 text-label-lg text-onSurface"
              >
                <Pause size={18} aria-hidden />
                {t("liveSession.pause")}
              </button>
            )}
            {state === "paused" && (
              <button
                type="button"
                onClick={onResume}
                className="inline-flex items-center gap-2 rounded-radii-lg border border-outlineVariant px-4 py-3 text-label-lg text-onSurface"
              >
                <Play size={18} aria-hidden />
                {t("liveSession.resume")}
              </button>
            )}
            {live && (
              <button
                type="button"
                onClick={onStop}
                className="inline-flex items-center gap-2 rounded-radii-lg bg-primary px-5 py-3 text-label-lg text-onPrimary"
              >
                <Square size={16} aria-hidden />
                {t("liveSession.stop")}
              </button>
            )}
            {!live && !canStart && (
              <span className="text-body-sm text-onSurfaceVariant">{t("liveSession.choosePatientFirst")}</span>
            )}
          </div>
        )}
      </div>

      <p className="mt-5 flex items-start gap-2 text-body-sm text-onSurfaceVariant">
        <Info size={16} className="mt-0.5 shrink-0" aria-hidden />
        {t("liveSession.consent")}
      </p>
    </section>
  );
}
