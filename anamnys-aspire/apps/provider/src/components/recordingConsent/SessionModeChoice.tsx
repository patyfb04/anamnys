import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { Mic, UserRound } from "lucide-react";
import type { RecordingConsent } from "@anamnys/shared/api/recordingConsent";
import Checkbox from "@anamnys/shared/ui/Checkbox";
import type { LiveMode } from "@/lib/signalr";

interface Props {
  patientId: string;
  // undefined while loading (or if loading failed): the session mode stays off, as the server would refuse it.
  consent: RecordingConsent | undefined;
  loadFailed: boolean;
  mode: LiveMode;
  onModeChange: (mode: LiveMode) => void;
  dictationConfirmed: boolean;
  onDictationConfirmedChange: (value: boolean) => void;
  locked: boolean;
}

// Sessão ao vivo: "Gravar a sessão" (only with a valid consent; the server enforces it) or "Ditar resumo (só eu falo)"
// (after the provider confirms the patient is not being recorded). Design spec 2026-11-15 §6.
export default function SessionModeChoice(props: Props) {
  const { patientId, consent, loadFailed, mode, onModeChange, dictationConfirmed, onDictationConfirmedChange, locked } = props;
  const { t } = useTranslation();
  const sessionAllowed = !!consent?.canRecord;

  const optionClass = (selected: boolean, disabled: boolean) =>
    `flex items-start gap-3 rounded-radii-lg border p-4 ${
      selected ? "border-primary bg-surfaceContainerLow" : "border-outlineVariant"
    } ${disabled ? "opacity-70" : "cursor-pointer"}`;

  return (
    <section className="bg-surfaceContainerLowest rounded-radii-xl border border-outlineVariant p-5 shadow-sm">
      <fieldset className="flex flex-col gap-3" disabled={locked}>
        <legend className="mb-1 text-title-md text-onSurface">{t("recordingConsent.session.modeLabel")}</legend>

        <label className={optionClass(mode === "session", !sessionAllowed)}>
          <input
            type="radio"
            name="liveMode"
            checked={mode === "session"}
            disabled={!sessionAllowed}
            onChange={() => onModeChange("session")}
            className="mt-1"
          />
          <span className="flex flex-col gap-1">
            <span className="flex items-center gap-2 text-label-lg text-onSurface">
              <Mic size={16} aria-hidden />
              {t("recordingConsent.session.record")}
            </span>
            <span className="text-body-sm text-onSurfaceVariant">{t("recordingConsent.session.recordHint")}</span>
            {!consent && !loadFailed && <span className="text-body-sm text-onSurfaceVariant">{t("recordingConsent.session.checking")}</span>}
            {!consent && loadFailed && (
              <span className="flex flex-col gap-1 text-body-sm text-error">
                <span role="alert">{t("recordingConsent.card.loadFailed")}</span>
                <Link
                  to="/patients/$patientId"
                  params={{ patientId }}
                  search={{ tab: "avaliacao" }}
                  className="self-start text-label-md text-primary underline"
                >
                  {t("recordingConsent.session.sendTerm")}
                </Link>
              </span>
            )}
            {consent && !sessionAllowed && (
              <span className="flex flex-col gap-1 text-body-sm text-onSurface">
                <span>{t(`recordingConsent.card.capture.${consent.reason}`, { defaultValue: t("recordingConsent.card.capture.none") })}</span>
                <Link
                  to="/patients/$patientId"
                  params={{ patientId }}
                  search={{ tab: "avaliacao" }}
                  className="self-start text-label-md text-primary underline"
                >
                  {t("recordingConsent.session.sendTerm")}
                </Link>
              </span>
            )}
          </span>
        </label>

        <label className={optionClass(mode === "dictation", false)}>
          <input
            type="radio"
            name="liveMode"
            checked={mode === "dictation"}
            onChange={() => onModeChange("dictation")}
            className="mt-1"
          />
          <span className="flex flex-col gap-1">
            <span className="flex items-center gap-2 text-label-lg text-onSurface">
              <UserRound size={16} aria-hidden />
              {t("recordingConsent.session.dictation")}
            </span>
            <span className="text-body-sm text-onSurfaceVariant">{t("recordingConsent.session.dictationHint")}</span>
          </span>
        </label>

        {mode === "dictation" && (
          <Checkbox checked={dictationConfirmed} onToggle={() => !locked && onDictationConfirmedChange(!dictationConfirmed)}>
            <span className="text-body-md text-onSurface">{t("recordingConsent.session.confirmDictation")}</span>
          </Checkbox>
        )}
      </fieldset>
    </section>
  );
}
