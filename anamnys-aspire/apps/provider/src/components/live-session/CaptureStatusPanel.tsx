import { useTranslation } from "react-i18next";
import { AlertTriangle, Megaphone, MonitorSpeaker, Play, RefreshCw, ScreenShare, Square } from "lucide-react";
import { isOnlineSource, switchTargets, type CaptureSupport, type RecordingSource } from "@/lib/audioCapture";
import type { LiveState, SourceError, SourceLevels } from "@/hooks/useLiveRecorder";

interface Props {
  state: LiveState;
  source: RecordingSource | null;
  levels: SourceLevels;
  sourceError: SourceError | null;
  // A share-again or method switch is in flight: those controls are off until it ends.
  busy: boolean;
  support: CaptureSupport;
  onReshare: () => void;
  onSwitch: (target: RecordingSource) => void;
  onResume: () => void;
  onEnd: () => void;
}

const buttonClass = "inline-flex items-center gap-2 rounded-radii-lg border border-outlineVariant px-4 py-2 text-label-lg text-onSurface disabled:opacity-60";
const primaryClass = "inline-flex items-center gap-2 rounded-radii-lg bg-primary px-4 py-2 text-label-lg text-onPrimary disabled:opacity-60";

// The page-level banner of an online recording ("Informe o paciente verbalmente…"), kept in view while scrolling.
export function InformPatientBanner() {
  const { t } = useTranslation();
  return (
    <p
      role="status"
      className="sticky top-0 z-10 flex items-center gap-2 rounded-radii-lg bg-errorContainer px-4 py-3 text-label-lg text-onErrorContainer shadow-sm"
    >
      <Megaphone size={18} className="shrink-0" aria-hidden />
      {t("liveSession.online.informBanner")}
    </p>
  );
}

function Meter({ label, value }: { label: string; value: number }) {
  const pct = Math.max(0, Math.min(100, value * 140));
  return (
    <div className="flex min-w-0 flex-1 items-center gap-3">
      <span className="w-16 shrink-0 text-label-md text-onSurfaceVariant">{label}</span>
      <span className="h-2 flex-1 overflow-hidden rounded-full bg-surfaceContainerHighest" aria-hidden>
        <span className="block h-full rounded-full bg-primary transition-[width] duration-100" style={{ width: `${pct}%` }} />
      </span>
    </div>
  );
}

// What the provider sees while a recording runs: the online banners, the per-source meters, the method switch, the
// share-lost panel and the "Retomar?" notice after the browser suspended the recording. Design spec 2026-11-16 §3.
export default function CaptureStatusPanel(props: Props) {
  const { state, source, levels, sourceError, busy, support, onReshare, onSwitch, onResume, onEnd } = props;
  const { t } = useTranslation();
  const capturing = state === "recording" || state === "paused" || state === "suspended" || state === "share_lost";
  if (!capturing || !source) return null;
  const online = isOnlineSource(source);
  const targets = switchTargets(source, support);

  return (
    <div className="flex flex-col gap-3">
      {state === "suspended" && (
        <div role="alert" className="flex flex-wrap items-center gap-3 rounded-radii-lg border border-outlineVariant bg-surfaceContainerLow p-4">
          <span className="flex-1 text-body-md text-onSurface">{t("liveSession.online.suspended")}</span>
          <button type="button" onClick={onResume} className={primaryClass}>
            <Play size={16} aria-hidden />
            {t("liveSession.resume")}
          </button>
        </div>
      )}

      {state === "share_lost" && (
        <div role="alert" className="flex flex-col gap-3 rounded-radii-lg border border-error bg-surfaceContainerLow p-4">
          <p className="flex items-center gap-2 text-title-sm text-error">
            <AlertTriangle size={18} aria-hidden />
            {t("liveSession.online.shareLost.title")}
          </p>
          <p className="text-body-md text-onSurface">{t("liveSession.online.shareLost.body")}</p>
          <div className="flex flex-wrap gap-3">
            <button type="button" onClick={onReshare} disabled={busy} className={primaryClass}>
              <ScreenShare size={16} aria-hidden />
              {t("liveSession.online.shareLost.reshare")}
            </button>
            {targets.map((target) => (
              <button key={target} type="button" onClick={() => onSwitch(target)} disabled={busy} className={buttonClass}>
                <MonitorSpeaker size={16} aria-hidden />
                {t(`liveSession.online.switchTo.${target}`)}
              </button>
            ))}
            <button type="button" onClick={onEnd} className={buttonClass}>
              <Square size={14} aria-hidden />
              {t("liveSession.online.shareLost.end")}
            </button>
          </div>
        </div>
      )}

      {online && state !== "share_lost" && (
        <section className="flex flex-col gap-3 rounded-radii-xl border border-outlineVariant bg-surfaceContainerLowest p-4 shadow-sm">
          {source === "online_speaker" && (
            <p className="flex items-center gap-2 text-body-md text-onSurface">
              <MonitorSpeaker size={16} className="shrink-0 text-primary" aria-hidden />
              {t("liveSession.online.speakerBanner")}
            </p>
          )}
          <div className="flex flex-col gap-2 sm:flex-row sm:gap-6">
            <Meter label={t("liveSession.online.meters.you")} value={levels.mic} />
            {source === "online_capture" && <Meter label={t("liveSession.online.meters.call")} value={levels.call ?? 0} />}
          </div>
          <div className="flex flex-wrap items-center gap-3">
            <span className="text-body-sm text-onSurfaceVariant">
              {t("liveSession.online.currentMethod", { method: t(`liveSession.online.method.${source}`) })}
            </span>
            {targets.map((target) => (
              <button key={target} type="button" onClick={() => onSwitch(target)} disabled={busy} className={buttonClass}>
                <RefreshCw size={16} aria-hidden />
                {t(`liveSession.online.switchTo.${target}`)}
              </button>
            ))}
          </div>
        </section>
      )}

      {sourceError && (
        <p role="alert" className="text-body-md text-error">{t(`liveSession.online.errors.${sourceError}`)}</p>
      )}
    </div>
  );
}
