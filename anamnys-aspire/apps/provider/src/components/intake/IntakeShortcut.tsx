import { useTranslation } from "react-i18next";
import { ClipboardList, Loader2 } from "lucide-react";
import { usePatientIntakes, useStartIntake } from "./intakeModel";

// Note Editor and Sessão ao vivo: while the patient has no intake, a reminder with a shortcut to start one. It never
// blocks the note or the session (patients created before F-31, or arriving with history from another provider).
// Hidden for archived patients (the caller passes enabled=false) and once any intake exists.
export default function IntakeShortcut({ patientId, enabled = true }: { patientId: string; enabled?: boolean }) {
  const { t } = useTranslation();
  const intakes = usePatientIntakes(enabled ? patientId : undefined);
  const start = useStartIntake(patientId);

  if (!enabled || !intakes.data || intakes.data.length > 0) return null;

  return (
    <section className="flex flex-col gap-3 rounded-radii-xl border border-error/30 bg-errorContainer/65 p-4 sm:flex-row sm:items-center sm:justify-between">
      <div className="flex items-start gap-3">
        <ClipboardList size={20} className="mt-0.5 shrink-0 text-onErrorContainer" aria-hidden />
        <div>
          <h2 className="text-label-lg text-onErrorContainer">{t("intake.shortcut.title")}</h2>
          <p className="text-body-sm text-onSurface">{t("intake.shortcut.hint")}</p>
        </div>
      </div>
      <button
        type="button"
        disabled={start.isPending}
        onClick={() => start.mutate()}
        className="inline-flex shrink-0 items-center justify-center gap-2 rounded-radii-md bg-primary px-4 py-2 text-label-lg text-onPrimary hover:opacity-90 disabled:opacity-60"
      >
        {start.isPending && <Loader2 size={16} className="animate-spin" aria-hidden />}
        {t("intake.shortcut.action")}
      </button>
    </section>
  );
}
