import { useTranslation } from "react-i18next";
import { WARN_AT_MS, type LiveState } from "@/hooks/useLiveRecorder";

interface Props {
  state: LiveState;
  connection: "connected" | "connecting";
  elapsedMs: number;
  level: number;
  onPause: () => void;
  onResume: () => void;
  onStop: () => void;
}

function clock(ms: number): string {
  const total = Math.floor(ms / 1000);
  const h = Math.floor(total / 3600);
  const m = String(Math.floor((total % 3600) / 60)).padStart(2, "0");
  const s = String(total % 60).padStart(2, "0");
  return h > 0 ? `${h}:${m}:${s}` : `${m}:${s}`;
}

export default function RecordingStep({ state, connection, elapsedMs, level, onPause, onResume, onStop }: Props) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col items-center gap-6">
      <p className={connection === "connected" ? "text-label-md text-onSurfaceVariant" : "text-label-md text-error"}>
        {connection === "connected" ? t("liveSession.connected") : t("liveSession.connecting")}
      </p>
      <p className="text-display-sm tabular-nums">{clock(elapsedMs)}</p>
      <div className="h-2 w-64 overflow-hidden rounded-full bg-surfaceVariant" aria-hidden>
        <div className="h-full bg-primary transition-[width]" style={{ width: `${Math.min(100, level * 140)}%` }} />
      </div>
      {state === "paused" && <p className="text-label-md">{t("liveSession.paused")}</p>}
      {elapsedMs >= WARN_AT_MS && <p className="text-body-md text-error">{t("liveSession.warnLimit")}</p>}
      <div className="flex gap-3">
        {state === "recording"
          ? <button type="button" onClick={onPause} className="rounded-radii-lg border border-outlineVariant px-4 py-2 text-label-lg">{t("liveSession.pause")}</button>
          : <button type="button" onClick={onResume} className="rounded-radii-lg border border-outlineVariant px-4 py-2 text-label-lg">{t("liveSession.resume")}</button>}
        <button type="button" onClick={onStop} className="px-4 py-2 rounded-radii-lg bg-primary text-label-lg text-onPrimary">{t("liveSession.stop")}</button>
      </div>
    </div>
  );
}
