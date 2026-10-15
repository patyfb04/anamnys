import { useState } from "react";
import { useTranslation } from "react-i18next";
import { RefreshCw } from "lucide-react";
import { notesApi } from "@anamnys/shared/api/notes";
import type { LiveNoteFormat } from "@anamnys/shared/lib/types";
import FormatCards from "@/components/live-session/FormatCards";
import ConfirmDialog from "@/components/patients/ConfirmDialog";

const FORMATS: LiveNoteFormat[] = ["DAP", "SOAP", "Biopsychosocial"];

interface Props {
  noteId: string;
  format: string;
  onRequested: () => void;
}

// Regenerates the AI sections in a chosen format; only rendered while nothing has been edited.
export default function RestructureControl({ noteId, format, onRequested }: Props) {
  const { t } = useTranslation();
  const [selected, setSelected] = useState<LiveNoteFormat>(
    FORMATS.includes(format as LiveNoteFormat) ? (format as LiveNoteFormat) : "DAP",
  );
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(false);
  const [confirming, setConfirming] = useState(false);

  const run = async () => {
    setConfirming(false);
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
    <FormatCards value={selected} onChange={setSelected} disabled={busy}>
      <button
        type="button"
        disabled={busy}
        onClick={() => setConfirming(true)}
        className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-radii-lg bg-primary px-4 py-3 text-label-lg text-onPrimary disabled:opacity-60"
      >
        <RefreshCw size={16} aria-hidden />
        {t("noteView.reorganize")}
      </button>
      {error && <p className="mt-2 text-body-sm text-error">{t("noteView.reorganizeFailed")}</p>}
      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        title={t("noteView.reorganizeConfirmTitle")}
        description={t("noteView.reorganizeConfirm")}
        confirmLabel={t("noteView.reorganize")}
        pending={busy}
        onConfirm={() => void run()}
      />
    </FormatCards>
  );
}
