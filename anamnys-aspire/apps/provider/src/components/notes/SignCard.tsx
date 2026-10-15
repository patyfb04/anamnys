import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "@tanstack/react-router";
import { isAxiosError } from "axios";
import { PenLine } from "lucide-react";
import { notesApi } from "@anamnys/shared/api/notes";
import type { NoteDetail } from "@anamnys/shared/lib/types";
import DetailCard from "@/components/patients/detail/DetailCard";
import ConfirmDialog from "@/components/patients/ConfirmDialog";

interface Props {
  note: NoteDetail;
  onSigned: () => void;
}

// Signing locks the note for good. Shown only once the note is ready for review.
export default function SignCard({ note, onSigned }: Props) {
  const { t } = useTranslation();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [confirming, setConfirming] = useState(false);

  const sign = async () => {
    setConfirming(false);
    setBusy(true);
    setError(null);
    try {
      await notesApi.sign(note.id);
      onSigned();
    } catch (e) {
      const reason = isAxiosError(e) ? (e.response?.data as { reason?: string } | undefined)?.reason : undefined;
      setError(reason === "locked" || reason === "no_sections" || reason === "missing_crp" ? reason : "generic");
    } finally {
      setBusy(false);
    }
  };

  if (note.signedAt) {
    return (
      <DetailCard title={t("noteView.signTitle")} icon={<PenLine size={20} />}>
        <p className="text-body-md text-onSurface">
          {t("noteView.signedAt", { date: new Date(note.signedAt).toLocaleString("pt-BR"), crp: note.signedByCrp ?? "—" })}
        </p>
      </DetailCard>
    );
  }

  return (
    <DetailCard title={t("noteView.signTitle")} icon={<PenLine size={20} />}>
      {!note.providerHasCrp && (
        <p className="mb-3 text-body-md text-onSurfaceVariant">
          {t("noteView.signNeedsCrp")}{" "}
          <Link to="/account/profile" className="text-primary hover:underline">
            {t("noteView.goToProfile")}
          </Link>
        </p>
      )}
      <button
        type="button"
        disabled={busy || !note.providerHasCrp || note.structureStatus === "pending" || note.sections.length === 0}
        onClick={() => setConfirming(true)}
        className="inline-flex w-full items-center justify-center gap-2 rounded-radii-lg bg-primary px-4 py-3 text-label-lg text-onPrimary disabled:opacity-60"
      >
        <PenLine size={16} aria-hidden />
        {t("noteView.signButton")}
      </button>
      {error && (
        <p role="alert" className="mt-2 text-body-sm text-error">
          {t(`noteView.signErrors.${error}`)}
        </p>
      )}
      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        title={t("noteView.signConfirmTitle")}
        description={t("noteView.signConfirm")}
        confirmLabel={t("noteView.signButton")}
        pending={busy}
        onConfirm={() => void sign()}
      />
    </DetailCard>
  );
}
