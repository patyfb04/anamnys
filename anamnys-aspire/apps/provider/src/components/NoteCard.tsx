import { useTranslation } from "react-i18next";
import type { NoteListItem } from "@anamnys/shared/lib/types";
import Card from "@anamnys/shared/ui/Card";
import Badge, { type BadgeTone } from "@anamnys/shared/ui/Badge";

const STATUS_TONE: Record<string, BadgeTone> = {
  Draft: "neutral",
  Processing: "pink",
  ReadyForReview: "teal",
  Signed: "primary",
  Exported: "blue",
  Failed: "neutral",
};

interface Props {
  note: NoteListItem;
  showPatient?: boolean;
  onClick: () => void;
}

// One row of a note list: date, format, origin and status; the patient's name in the all-notes list.
export default function NoteCard({ note, showPatient, onClick }: Props) {
  const { t } = useTranslation();
  const structureFailed = note.structureStatus === "failed" && note.status !== "Failed";
  return (
    <Card onClick={onClick}>
      <div className="flex items-center justify-between gap-2 mb-2">
        <span className="text-label-md text-onSurfaceVariant normal-case">
          {new Date(note.createdAt).toLocaleDateString("pt-BR")}
        </span>
        <Badge
          label={structureFailed ? t("notes.detailStatus.structureFailed") : t(`notes.detailStatus.${note.status}`)}
          tone={structureFailed ? "neutral" : STATUS_TONE[note.status] ?? "neutral"}
        />
      </div>
      {showPatient && <p className="text-headline-sm text-[16px] text-onSurface mb-1 truncate">{note.patientName}</p>}
      <p className="text-body-md text-onSurfaceVariant">
        {t(`liveSession.formats.${note.format}`, { defaultValue: note.format })}
        {" · "}
        {t(`notes.list.origin.${note.inputMode}`, { defaultValue: note.inputMode })}
        {note.appointmentStartsAt &&
          ` · ${t("notes.list.appointment", { date: new Date(note.appointmentStartsAt).toLocaleDateString("pt-BR") })}`}
      </p>
    </Card>
  );
}
