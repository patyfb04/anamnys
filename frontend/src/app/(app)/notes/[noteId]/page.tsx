export default async function NoteEditorPage({ params }: PageProps<"/notes/[noteId]">) {
  const { noteId } = await params;
  return (
    <div className="p-8 text-center text-onSurfaceVariant text-body-lg">
      Note {noteId} — coming soon.
    </div>
  );
}
