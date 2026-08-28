import { useTranslation } from "react-i18next";
import type { Note } from "@anamnys/shared/lib/types";
import Card from "@anamnys/shared/ui/Card";
import Badge, { type BadgeTone } from "@anamnys/shared/ui/Badge";

const STATUS_TONE: Record<string, BadgeTone> = {
  draft: "neutral",
  processing: "pink",
  ready_for_review: "teal",
  signed: "primary",
  exported: "blue",
};

interface Props {
  note: Note;
  onClick: () => void;
}

// Summary card for a note, showing date, status badge, and a transcript preview.
export default function NoteCard({ note, onClick }: Props) {
  const { t } = useTranslation();
  return (
    <Card onClick={onClick}>
      <div className="flex items-center justify-between mb-2">
        <span className="text-label-md text-onSurfaceVariant normal-case">
          {new Date(note.createdAt).toLocaleDateString()}
        </span>
        <Badge label={t(`notes.status.${note.status}`)} tone={STATUS_TONE[note.status] ?? "neutral"} />
      </div>
      <p className="text-headline-sm text-[16px] text-onSurface mb-1">
        {note.structuredContent?.format ?? note.inputMode.replace(/_/g, " ")}
      </p>
      {note.rawTranscript && (
        <p className="text-body-md text-onSurfaceVariant line-clamp-2">{note.rawTranscript}</p>
      )}
    </Card>
  );
}
