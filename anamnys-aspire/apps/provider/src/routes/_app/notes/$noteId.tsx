import { useQuery } from "@tanstack/react-query";
import { createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { notesApi } from "@anamnys/shared/api/notes";

export const Route = createFileRoute("/_app/notes/$noteId")({
  component: NotePage,
});

// Delivery 1 of the live session: state and raw transcript only. The editor is delivery 3
// (design/specs/2026-10-10-live-session-transcription-design.md).
function NotePage() {
  const { t } = useTranslation();
  const { noteId } = Route.useParams();
  const query = useQuery({
    queryKey: ["note", noteId],
    queryFn: () => notesApi.getDetail(noteId),
    refetchInterval: (q) => (q.state.data?.status === "Processing" ? 5000 : false),
  });

  if (!query.data) return query.isError ? <p className="p-8 text-center text-body-lg text-error">{t("noteView.notFound")}</p> : null;
  const note = query.data;

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
      {note.rawTranscript && (
        <section>
          <h2 className="mb-2 text-title-md">{t("noteView.transcript")}</h2>
          <p className="whitespace-pre-wrap rounded-radii-md bg-surfaceVariant p-4 text-body-md">{note.rawTranscript}</p>
          <p className="mt-4 text-body-sm text-onSurfaceVariant">{t("noteView.editorSoon")}</p>
        </section>
      )}
    </div>
  );
}
