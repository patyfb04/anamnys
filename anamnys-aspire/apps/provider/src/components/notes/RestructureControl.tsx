import { useState } from "react";
import { useTranslation } from "react-i18next";
import { notesApi } from "@anamnys/shared/api/notes";
import type { LiveNoteFormat } from "@anamnys/shared/lib/types";
import { inputClass } from "@/components/patients/FormField";

const FORMATS: LiveNoteFormat[] = ["DAP", "SOAP", "Biopsychosocial"];

interface Props {
  noteId: string;
  format: string;
  onRequested: () => void;
}

export default function RestructureControl({ noteId, format, onRequested }: Props) {
  const { t } = useTranslation();
  const [selected, setSelected] = useState<LiveNoteFormat>(
    FORMATS.includes(format as LiveNoteFormat) ? (format as LiveNoteFormat) : "DAP",
  );
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(false);

  const run = async () => {
    if (!window.confirm(t("noteView.reorganizeConfirm"))) return;
    setBusy(true);
    setError(false);
    try {
      await notesApi.restructure(noteId, selected);
      onRequested();
    } catch {
      setError(true);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="mt-6 flex flex-col gap-2">
      <div className="flex flex-wrap items-end gap-3">
        <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
          {t("noteView.format")}
          <select
            className={inputClass}
            value={selected}
            disabled={busy}
            onChange={(e) => setSelected(e.target.value as LiveNoteFormat)}
          >
            {FORMATS.map((f) => <option key={f} value={f}>{t(`liveSession.formats.${f}`)}</option>)}
          </select>
        </label>
        <button
          type="button"
          disabled={busy}
          onClick={run}
          className="px-4 py-2 rounded-radii-lg bg-primary text-label-lg text-onPrimary disabled:opacity-60"
        >
          {t("noteView.reorganize")}
        </button>
      </div>
      {error && <p className="text-body-sm text-error">{t("noteView.reorganizeFailed")}</p>}
    </div>
  );
}
