import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { createFileRoute, Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { isAxiosError } from "axios";
import { AlertCircle, ArrowLeft, ChevronDown, FileText, Loader2, PenLine, ScrollText } from "lucide-react";
import { notesApi } from "@anamnys/shared/api/notes";
import type { LiveNoteFormat, NoteDetail } from "@anamnys/shared/lib/types";
import Avatar from "@anamnys/shared/ui/Avatar";
import Badge from "@anamnys/shared/ui/Badge";
import DetailCard from "@/components/patients/detail/DetailCard";
import NoteSections from "@/components/notes/NoteSections";
import RestructureControl from "@/components/notes/RestructureControl";
import SignCard from "@/components/notes/SignCard";
import SessionScalesCard from "@/components/scales/SessionScalesCard";

export const Route = createFileRoute("/_app/notes/$noteId")({
  component: NotePage,
});

// The note produced by a live session: AI sections, raw transcript, regeneration, per-section
// editing and signing (design/specs/2026-10-13-note-review-and-signing-design.md §5).
function NotePage() {
  const { t } = useTranslation();
  const { noteId } = Route.useParams();
  const query = useQuery({
    queryKey: ["note", noteId],
    queryFn: () => notesApi.getDetail(noteId),
    refetchInterval: (q) =>
      q.state.data?.status === "Processing" || q.state.data?.structureStatus === "pending" ? 5000 : false,
  });

  if (!query.data) {
    return query.isError ? <p className="p-8 text-body-lg text-error">{t("noteView.notFound")}</p> : null;
  }
  const note = query.data;
  const hasSections = note.sections.length > 0;
  const editable = note.status === "ReadyForReview" && !note.signedAt && note.structureStatus !== "pending";
  const aiDraft = note.sectionsByAi && hasSections && !note.sections.some((s) => s.editedAt);
  const canRestructure =
    editable &&
    note.structureFailure !== "too_long" &&
    !!note.rawTranscript &&
    !note.sections.some((s) => s.editedAt);

  return (
    <div className="flex flex-col gap-6 p-4 md:p-8">
      <Link
        to="/patients/$patientId"
        params={{ patientId: note.patientId }}
        className="inline-flex items-center gap-1 self-start text-label-lg text-primary hover:underline"
      >
        <ArrowLeft size={16} />
        {t("noteView.backToPatient")}
      </Link>

      <header className="flex items-center gap-4 min-w-0">
        <Avatar name={note.patientName} size={64} />
        <div className="min-w-0">
          <h1 className="truncate text-headline-lg text-onSurface">{note.patientName}</h1>
          <div className="mt-1 flex flex-wrap items-center gap-2 text-body-md text-onSurfaceVariant">
            <span>{new Date(note.createdAt).toLocaleString("pt-BR")}</span>
            <Badge label={t(`liveSession.formats.${note.format}`, { defaultValue: note.format })} tone="primary" />
            {note.signedAt ? (
              <Badge label={t("noteView.signed")} tone="mint" />
            ) : aiDraft ? (
              <Badge label={t("noteView.aiDraft")} tone="lavender" />
            ) : (
              <Badge label={t("noteView.inReview")} tone="blue" />
            )}
          </div>
        </div>
      </header>

      <div className="grid gap-6 lg:grid-cols-3">
        <div className="flex flex-col gap-6 lg:col-span-2">
          <DetailCard title={t("noteView.note")} icon={<FileText size={20} />}>
            <NoteBody note={note} onRetried={() => void query.refetch()} />
            {hasSections && (
              <NoteSections sections={note.sections} editable={editable} noteId={noteId} onSaved={() => void query.refetch()} />
            )}
            {!hasSections && editable && <WriteManually noteId={noteId} onCreated={() => void query.refetch()} />}
          </DetailCard>
          {note.rawTranscript && (
            <TranscriptCard
              text={note.rawTranscript}
              defaultOpen={!hasSections}
              title={note.inputMode === "Text" ? t("noteView.originalText") : t("noteView.transcript")}
            />
          )}
        </div>

        <div className="flex flex-col gap-6">
          {(note.status === "ReadyForReview" || !!note.signedAt) && <SignCard note={note} onSigned={() => query.refetch()} />}
          {canRestructure && (
            <RestructureControl noteId={noteId} format={note.format} onRequested={() => void query.refetch()} />
          )}
          {note.sessionId && <SessionScalesCard patientId={note.patientId} sessionId={note.sessionId} />}
        </div>
      </div>
    </div>
  );
}

// Shown when the AI produced no sections: the provider can start from blank sections instead.
function WriteManually({ noteId, onCreated }: { noteId: string; onCreated: () => void }) {
  const { t } = useTranslation();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(false);

  const create = async () => {
    setBusy(true);
    setError(false);
    try {
      await notesApi.createBlankSections(noteId);
      onCreated();
    } catch (e) {
      // 409: the note changed meanwhile (sections already exist, structuring started, signed) — show it.
      if (isAxiosError(e) && e.response?.status === 409) onCreated();
      else setError(true);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex flex-col items-start gap-3 rounded-radii-lg bg-surfaceContainerLow p-4">
      <p className="text-body-md text-onSurfaceVariant">{t("noteView.writeManuallyHint")}</p>
      <button
        type="button"
        disabled={busy}
        onClick={create}
        className="inline-flex items-center gap-2 rounded-radii-lg bg-primary px-4 py-2 text-label-lg text-onPrimary disabled:opacity-60"
      >
        <PenLine size={16} aria-hidden />
        {t("noteView.writeManually")}
      </button>
      {error && (
        <p role="alert" className="text-body-sm text-error">
          {t("noteView.editErrors.generic")}
        </p>
      )}
    </div>
  );
}

// The raw transcript can be long (up to ~35 min of speech), so it starts collapsed once the note
// has sections; with no sections it is the note, so it starts open.
function TranscriptCard({ text, defaultOpen, title }: { text: string; defaultOpen: boolean; title: string }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(defaultOpen);
  return (
    <DetailCard
      title={title}
      icon={<ScrollText size={20} />}
      className={open ? undefined : "[&>div]:mb-0"}
      action={
        <button
          type="button"
          onClick={() => setOpen((v) => !v)}
          aria-expanded={open}
          aria-controls="note-transcript"
          className="inline-flex items-center gap-1 rounded-radii-md px-2 py-1 text-label-md text-primary hover:bg-surfaceContainerLow"
        >
          {open ? t("noteView.hideTranscript") : t("noteView.showTranscript")}
          <ChevronDown size={16} aria-hidden className={`transition-transform ${open ? "rotate-180" : ""}`} />
        </button>
      }
    >
      {open && (
        <p id="note-transcript" className="max-h-96 overflow-y-auto whitespace-pre-wrap text-body-md text-onSurface">
          {text}
        </p>
      )}
    </DetailCard>
  );
}

// Processing, structuring and failure states shown at the top of the note card.
function NoteBody({ note, onRetried }: { note: NoteDetail; onRetried: () => void }) {
  const { t } = useTranslation();
  const [retrying, setRetrying] = useState(false);

  const retry = async () => {
    setRetrying(true);
    try {
      await notesApi.restructure(note.id, note.format as LiveNoteFormat);
      onRetried();
    } catch {
      // The failure message stays; the provider can try again.
    } finally {
      setRetrying(false);
    }
  };

  if (note.status === "Processing" || note.structureStatus === "pending") {
    return (
      <p className="mb-4 flex items-center gap-2 rounded-radii-lg bg-surfaceContainerLow p-4 text-body-md text-onSurfaceVariant">
        <Loader2 size={18} className="animate-spin text-primary" aria-hidden />
        {note.status === "Processing" ? t("noteView.processing") : t("noteView.structuring")}
      </p>
    );
  }
  if (note.status === "Failed") {
    return (
      <p className="flex items-center gap-2 rounded-radii-lg bg-errorContainer p-4 text-body-md text-onErrorContainer">
        <AlertCircle size={18} aria-hidden />
        {note.failureReason ? t(`liveSession.failed.${note.failureReason}`) : t("noteView.failed")}
      </p>
    );
  }
  if (note.structureStatus === "failed") {
    return (
      <div className="mb-4 flex flex-col items-start gap-3 rounded-radii-lg bg-errorContainer p-4 text-onErrorContainer">
        <p className="flex items-center gap-2 text-body-md">
          <AlertCircle size={18} aria-hidden />
          {t(`noteView.structureFailed.${note.structureFailure ?? "model_error"}`)}
        </p>
        {note.structureFailure !== "too_long" && (
          <button
            type="button"
            disabled={retrying}
            onClick={retry}
            className="rounded-radii-lg bg-primary px-4 py-2 text-label-lg text-onPrimary disabled:opacity-60"
          >
            {t("noteView.retry")}
          </button>
        )}
      </div>
    );
  }
  return null;
}
