import { useEffect, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { X } from "lucide-react";
import Checkbox from "@anamnys/shared/ui/Checkbox";
import type { NoteStatusGroup, PatientFilters } from "@anamnys/shared/lib/types";

const STATUSES: NoteStatusGroup[] = ["pending", "signed", "none"];

interface Props {
  filters: PatientFilters;
  onApply: (filters: PatientFilters) => void;
  onClose: () => void;
}

// Mount only while open: the draft starts from the applied filters on every open, and
// nothing reaches the list until "Aplicar". A popover under the Filtrar button at md+, a
// full-screen sheet below md. The parent positions it (relative wrapper).
export default function PatientFilterPanel({ filters, onApply, onClose }: Props) {
  const { t } = useTranslation();
  const [draft, setDraft] = useState<PatientFilters>(filters);

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  const set = (patch: Partial<PatientFilters>) => setDraft((current) => ({ ...current, ...patch }));

  const toggleStatus = (status: NoteStatusGroup) => {
    const current = draft.noteStatus ?? [];
    set({ noteStatus: current.includes(status) ? current.filter((s) => s !== status) : [...current, status] });
  };

  const inputClass =
    "w-full bg-surfaceContainerLowest border border-outlineVariant focus:border-primary rounded-radii-md px-3 py-2 text-body-md text-onSurface outline-none";

  return (
    <>
      <div className="fixed inset-0 z-40 bg-black/30 md:bg-transparent" onClick={onClose} aria-hidden />
      <div
        role="dialog"
        aria-modal="true"
        aria-label={t("patients.list.filters.title")}
        className="fixed inset-0 z-50 flex flex-col bg-surfaceContainerLowest md:absolute md:inset-auto md:right-0 md:top-full md:mt-2 md:w-96 md:rounded-radii-lg md:border md:border-outlineVariant md:shadow-lg"
      >
        <div className="flex items-center justify-between px-4 py-3 border-b border-outlineVariant">
          <h2 className="text-headline-sm text-onSurface">{t("patients.list.filters.title")}</h2>
          <button
            type="button"
            onClick={onClose}
            aria-label={t("patients.list.filters.close")}
            className="p-1.5 rounded-radii-full text-onSurfaceVariant hover:bg-surfaceContainer"
          >
            <X size={20} />
          </button>
        </div>

        <div className="flex-1 overflow-y-auto px-4 py-4 flex flex-col gap-4">
          <Field label={t("patients.list.filters.name")}>
            <input className={inputClass} value={draft.name ?? ""} onChange={(e) => set({ name: e.target.value })} />
          </Field>
          <Field label={t("patients.list.filters.email")}>
            <input className={inputClass} value={draft.email ?? ""} onChange={(e) => set({ email: e.target.value })} />
          </Field>

          <fieldset>
            <legend className="text-label-md text-onSurfaceVariant mb-2">{t("patients.list.filters.status")}</legend>
            <div className="flex flex-col gap-2">
              {STATUSES.map((status) => (
                <Checkbox
                  key={status}
                  checked={draft.noteStatus?.includes(status) ?? false}
                  onToggle={() => toggleStatus(status)}
                >
                  <span className="text-body-md text-onSurface">{t(`patients.list.status.${status}`)}</span>
                </Checkbox>
              ))}
            </div>
          </fieldset>

          <DateRange
            legend={t("patients.list.filters.lastVisit")}
            from={draft.lastVisitFrom}
            to={draft.lastVisitTo}
            onChange={(from, to) => set({ lastVisitFrom: from, lastVisitTo: to })}
            inputClass={inputClass}
          />
          <DateRange
            legend={t("patients.list.filters.nextVisit")}
            from={draft.nextVisitFrom}
            to={draft.nextVisitTo}
            onChange={(from, to) => set({ nextVisitFrom: from, nextVisitTo: to })}
            inputClass={inputClass}
          />

          <Checkbox checked={draft.archived ?? false} onToggle={() => set({ archived: !draft.archived })}>
            <span className="text-body-md text-onSurface">{t("patients.list.filters.archivedOnly")}</span>
          </Checkbox>
        </div>

        <div className="flex gap-2 px-4 py-3 border-t border-outlineVariant">
          <button
            type="button"
            onClick={() => setDraft({})}
            className="flex-1 px-4 py-2 border border-outlineVariant rounded-radii-md text-label-lg text-onSurface hover:bg-surfaceContainerLow"
          >
            {t("patients.list.filters.clear")}
          </button>
          <button
            type="button"
            onClick={() => {
              onApply(draft);
              onClose();
            }}
            className="flex-1 px-4 py-2 bg-primary text-onPrimary rounded-radii-md text-label-lg hover:opacity-90"
          >
            {t("patients.list.filters.apply")}
          </button>
        </div>
      </div>
    </>
  );
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
      {label}
      {children}
    </label>
  );
}

interface DateRangeProps {
  legend: string;
  from?: string;
  to?: string;
  onChange: (from?: string, to?: string) => void;
  inputClass: string;
}

function DateRange({ legend, from, to, onChange, inputClass }: DateRangeProps) {
  const { t } = useTranslation();
  return (
    <fieldset>
      <legend className="text-label-md text-onSurfaceVariant mb-2">{legend}</legend>
      <div className="grid grid-cols-2 gap-2">
        <Field label={t("patients.list.filters.from")}>
          <input
            type="date"
            className={inputClass}
            value={from ?? ""}
            max={to || undefined}
            onChange={(e) => onChange(e.target.value || undefined, to)}
          />
        </Field>
        <Field label={t("patients.list.filters.to")}>
          <input
            type="date"
            className={inputClass}
            value={to ?? ""}
            min={from || undefined}
            onChange={(e) => onChange(from, e.target.value || undefined)}
          />
        </Field>
      </div>
    </fieldset>
  );
}
