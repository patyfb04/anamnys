import { createFileRoute } from "@tanstack/react-router";

export const Route = createFileRoute("/_app/notes/$noteId")({
  component: NoteEditorPage,
});

function NoteEditorPage() {
  const { noteId } = Route.useParams();
  return (
    <div className="p-8 text-center text-onSurfaceVariant text-body-lg">
      Note {noteId} — coming soon.
    </div>
  );
}
