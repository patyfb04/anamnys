import { useTranslation } from "react-i18next";
import { Info, MonitorSpeaker, Users, Video } from "lucide-react";
import type { CaptureSupport, OnlineMethod, SessionPlace } from "@/lib/audioCapture";

interface Props {
  place: SessionPlace;
  onPlaceChange: (place: SessionPlace) => void;
  method: OnlineMethod;
  onMethodChange: (method: OnlineMethod) => void;
  support: CaptureSupport;
  locked: boolean;
}

const optionClass = (selected: boolean, disabled = false) =>
  `flex items-start gap-3 rounded-radii-lg border p-4 ${
    selected ? "border-primary bg-surfaceContainerLow" : "border-outlineVariant"
  } ${disabled ? "opacity-70" : "cursor-pointer"}`;

// Session mode only: "Presencial / Online" and, online, how the patient's voice is captured: the call's tab or
// screen (where the browser can) or the loudspeaker (always). Design spec 2026-11-16 §3.
export default function OnlineMethodChoice({ place, onPlaceChange, method, onMethodChange, support, locked }: Props) {
  const { t } = useTranslation();
  const effective: OnlineMethod = support.tab ? method : "speaker";

  return (
    <section className="bg-surfaceContainerLowest rounded-radii-xl border border-outlineVariant p-5 shadow-sm">
      <fieldset className="flex flex-col gap-3" disabled={locked}>
        <legend className="mb-1 text-title-md text-onSurface">{t("liveSession.online.placeLabel")}</legend>
        <div className="grid gap-3 sm:grid-cols-2">
          {(["in_person", "online"] as const).map((p) => (
            <label key={p} className={optionClass(place === p)}>
              <input type="radio" name="sessionPlace" checked={place === p} onChange={() => onPlaceChange(p)} className="mt-1" />
              <span className="flex flex-col gap-1">
                <span className="flex items-center gap-2 text-label-lg text-onSurface">
                  {p === "online" ? <Video size={16} aria-hidden /> : <Users size={16} aria-hidden />}
                  {t(`liveSession.online.place.${p}`)}
                </span>
                <span className="text-body-sm text-onSurfaceVariant">{t(`liveSession.online.placeHint.${p}`)}</span>
              </span>
            </label>
          ))}
        </div>

        {place === "online" && (
          <div className="flex flex-col gap-3">
            <p className="text-label-lg text-onSurface">{t("liveSession.online.methodLabel")}</p>
            {support.tab ? (
              (["capture", "speaker"] as const).map((m) => {
                const code = m === "capture" ? "online_capture" : "online_speaker";
                return (
                  <label key={m} className={optionClass(effective === m)}>
                    <input type="radio" name="onlineMethod" checked={effective === m} onChange={() => onMethodChange(m)} className="mt-1" />
                    <span className="flex flex-col gap-1">
                      <span className="flex items-center gap-2 text-label-lg text-onSurface">
                        {m === "capture" ? <Video size={16} aria-hidden /> : <MonitorSpeaker size={16} aria-hidden />}
                        {t(`liveSession.online.method.${code}`)}
                      </span>
                      <span className="text-body-sm text-onSurfaceVariant">{t(`liveSession.online.methodHint.${code}`)}</span>
                    </span>
                  </label>
                );
              })
            ) : (
              <p className="flex items-start gap-2 rounded-radii-lg bg-surfaceContainerLow p-3 text-body-sm text-onSurface">
                <MonitorSpeaker size={16} className="mt-0.5 shrink-0" aria-hidden />
                {t("liveSession.online.unsupported")}
              </p>
            )}

            {effective === "capture" && (
              <div className="flex items-start gap-2 rounded-radii-lg bg-surfaceContainerLow p-3 text-body-sm text-onSurface">
                <Info size={16} className="mt-0.5 shrink-0 text-primary" aria-hidden />
                <span className="flex flex-col gap-1">
                  <span className="text-label-md">{t("liveSession.online.instructionsTitle")}</span>
                  <span>{t("liveSession.online.instructionsTab")}</span>
                  {support.systemAudio && <span>{t("liveSession.online.instructionsScreen")}</span>}
                </span>
              </div>
            )}
          </div>
        )}
      </fieldset>
    </section>
  );
}
