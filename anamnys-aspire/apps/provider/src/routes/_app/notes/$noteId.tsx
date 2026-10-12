import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { notesApi } from "@anamnys/shared/api/notes";
import type { LiveNoteFormat } from "@anamnys/shared/lib/types";
import NoteSections from "@/components/notes/NoteSections";
import RestructureControl from "@/components/notes/RestructureControl";

export const Route = createFileRoute("/_app/notes/$noteId")({
  component: NotePage,
});

// Delivery 2 of the live session: raw transcript plus AI-structured sections. The editor is delivery 3
// (design/specs/2026-10-10-live-session-transcription-design.md).
function NotePage() {
  const { t } = useTranslation();
  const { noteId } = Route.useParams();
  const [retrying, setRetrying] = useState(false);
  const query = useQuery({
    queryKey: ["note", noteId],
    queryFn: () => notesApi.getDetail(noteId),
    refetchInterval: (q) =>
      q.state.data?.status === "Processing" || q.state.data?.structureStatus === "pending" ? 5000 : false,
  });

  if (!query.data) return query.isError ? <p className="p-8 text-center text-body-lg text-error">{t("noteView.notFound")}</p> : null;
  const note = query.data;
  const hasSections = note.sections.length > 0;
  const canRestructure =
    note.status === "ReadyForReview" &&
    note.structureStatus !== "pending" &&
    note.structureFailure !== "too_long" &&
    !!note.rawTranscript &&
    !note.sections.some((s) => s.editedAt);

  const retry = async () => {
    setRetrying(true);
    try {
      await notesApi.restructure(noteId, note.format as LiveNoteFormat);
      await query.refetch();
    } catch {
      // The failure line stays; the provider can try again.
    } finally {
      setRetrying(false);
    }
  };

  return (
    <div className="mx-auto max-w-3xl p-4 md:p-8">
      <h1 className="text-headline-sm">{note.patientName}</h1>
      <p className="mb-6 text-label-md text-onSurfaceVariant">
        {new Date(note.createdAt).toLocaleString("pt-BR")} · {note.format}
      </p>
      {note.status === "Processing" && <p className="text-body-lg text-onSurfaceVariant">{t("noteView.processing")}</p>}
      {note.status === "Failed" && (
        <p className="text-body-lg text-error">
          {note.failureReason ? t(`liveSession.failed.${note.failureReason}`) : t("noteView.failed")}
        </p>
      )}
      {note.structureStatus === "pending" && <p className="mb-4 text-body-lg text-onSurfaceVariant">{t("noteView.structuring")}</p>}
      {note.structureStatus === "failed" && (
        <div className="mb-4 flex flex-col items-start gap-2">
          <p className="text-body-lg text-error">
            {note.structureFailure ? t(`noteView.structureFailed.${note.structureFailure}`) : t("noteView.structureFailed.model_error")}
          </p>
          {note.structureFailure !== "too_long" && (
            <button
              type="button"
              disabled={retrying}
              onClick={retry}
              className="px-4 py-2 rounded-radii-lg bg-primary text-label-lg text-onPrimary disabled:opacity-60"
            >
              {t("noteView.retry")}
            </button>
          )}
        </div>
      )}
      {hasSections && <NoteSections sections={note.sections} />}
      {note.rawTranscript &&
        (hasSections ? (
          <details className="mt-6">
            <summary className="cursor-pointer text-title-md">{t("noteView.transcript")}</summary>
            <p className="mt-2 whitespace-pre-wrap rounded-radii-md bg-surfaceVariant p-4 text-body-md">{note.rawTranscript}</p>
          </details>
        ) : (
          <section>
            <h2 className="mb-2 text-title-md">{t("noteView.transcript")}</h2>
            <p className="whitespace-pre-wrap rounded-radii-md bg-surfaceVariant p-4 text-body-md">{note.rawTranscript}</p>
          </section>
        ))}
      {canRestructure && <RestructureControl noteId={noteId} format={note.format} onRequested={() => void query.refetch()} />}
      {note.rawTranscript && <p className="mt-4 text-body-sm text-onSurfaceVariant">{t("noteView.editorSoon")}</p>}
    </div>
  );
}
