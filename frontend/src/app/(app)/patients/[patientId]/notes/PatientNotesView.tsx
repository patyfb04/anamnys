"use client";

import { useRouter } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Loader2 } from "lucide-react";
import { notesApi } from "@/api/notes";
import TopBar from "@/components/ui/TopBar";
import NoteCard from "@/components/NoteCard";

// Lists a single patient's notes; selecting one opens it in the editor.
export default function PatientNotesView({ patientId }: { patientId: string }) {
  const router = useRouter();
  const { t } = useTranslation();

  const { data: notes, isLoading, error } = useQuery({
    queryKey: ["notes", patientId],
    queryFn: () => notesApi.list(patientId),
  });

  return (
    <div className="min-h-full flex flex-col">
      <TopBar title={t("patients.notes.title")} />
      <div className="p-4 pb-10 max-w-2xl mx-auto w-full">
        {isLoading ? (
          <div className="flex justify-center py-12">
            <Loader2 className="animate-spin text-primary" size={32} />
          </div>
        ) : error ? (
          <p className="text-error text-center py-12">{t("patients.notes.failedToLoad")}</p>
        ) : !notes || notes.length === 0 ? (
          <p className="text-body-md text-onSurfaceVariant text-center mt-8">{t("patients.notes.noNotes")}</p>
        ) : (
          <div className="flex flex-col gap-2.5">
            {notes.map((note) => (
              <NoteCard key={note.id} note={note} onClick={() => router.push(`/notes/${note.id}`)} />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
