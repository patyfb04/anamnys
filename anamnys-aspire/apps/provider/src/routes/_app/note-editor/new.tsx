import { useEffect, useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Loader2 } from "lucide-react";
import { ApiError } from "@anamnys/shared/api/client";
import { notesApi } from "@anamnys/shared/api/notes";
import { patientsApi } from "@anamnys/shared/api/patients";
import type { LiveNoteFormat, NoteMode } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import FormatCards from "@/components/live-session/FormatCards";
import NoteTargetCard, { type PickedPatient } from "@/components/notes/NoteTargetCard";
import NoteModeCards from "@/components/notes/NoteModeCards";
import { inputClass } from "@/components/patients/FormField";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const MAX_TEXT = 20_000;

interface NewNoteSearch {
  patientId?: string;
  appointmentId?: string;
}

export const Route = createFileRoute("/_app/note-editor/new")({
  // Only opaque ids in the URL; the patient's name is loaded, never passed.
  validateSearch: (search: Record<string, unknown>): NewNoteSearch => ({
    patientId: typeof search.patientId === "string" && UUID.test(search.patientId) ? search.patientId : undefined,
    appointmentId: typeof search.appointmentId === "string" && UUID.test(search.appointmentId) ? search.appointmentId : undefined,
  }),
  component: NewNotePage,
});

// Typed note (design/specs/2026-10-15-note-editor-design.md §5.2).
function NewNotePage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const search = Route.useSearch();
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const [appointmentId, setAppointmentId] = useState(search.appointmentId ?? "");
  const [format, setFormat] = useState<LiveNoteFormat>("DAP");
  const [mode, setMode] = useState<NoteMode>("ai");
  const [text, setText] = useState("");
  const [errors, setErrors] = useState<Record<string, string[]>>({});

  const preselected = useQuery({
    queryKey: ["patient", search.patientId],
    queryFn: () => patientsApi.get(search.patientId!),
    enabled: !!search.patientId,
  });
  useEffect(() => {
    if (preselected.data && !patient) {
      setPatient({ id: preselected.data.id, name: `${preselected.data.firstName} ${preselected.data.lastName}` });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [preselected.data]);

  const create = useMutation({
    mutationFn: () =>
      notesApi.create({
        patientId: patient!.id,
        appointmentId: appointmentId || null,
        format,
        mode,
        text: mode === "ai" ? text.trim() || null : null,
      }),
    onSuccess: (noteId) => {
      void queryClient.invalidateQueries({ queryKey: ["notes"] });
      void queryClient.invalidateQueries({ queryKey: ["appointments"] });
      navigate({ to: "/notes/$noteId", params: { noteId } });
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 400 && err.errors) {
        setErrors(err.errors);
        // Errors on fields this page does not render (format, mode) would otherwise be invisible.
        if (!("patientId" in err.errors) && !("text" in err.errors)) toast.error(t("notes.create.failed"));
      } else toast.error(err instanceof ApiError && err.status === 409 ? err.message : t("notes.create.failed"));
    },
  });

  const canSubmit = !!patient && (mode === "manual" || text.trim().length > 0) && text.length <= MAX_TEXT && !create.isPending;

  return (
    <form
      className="flex flex-col gap-6 p-4 md:p-8"
      onSubmit={(e) => {
        e.preventDefault();
        setErrors({});
        if (canSubmit) create.mutate();
      }}
    >
      <h1 className="text-headline-lg text-onSurface">{t("notes.create.title")}</h1>
      <NoteTargetCard
        patient={patient}
        onPatientChange={setPatient}
        appointmentId={appointmentId}
        onAppointmentChange={setAppointmentId}
        error={errors.patientId}
      />
      <FormatCards value={format} onChange={setFormat} />
      <section className="flex flex-col gap-2">
        <h2 className="text-title-md text-onSurface">{t("notes.create.mode")}</h2>
        <NoteModeCards value={mode} onChange={setMode} />
      </section>
      {mode === "ai" && (
        <label className="flex flex-col gap-1">
          <span className="text-label-lg text-onSurface">{t("notes.create.text")}</span>
          <span className="text-body-sm text-onSurfaceVariant">{t("notes.create.textHint")}</span>
          <textarea
            className={`${inputClass} min-h-64`}
            value={text}
            onChange={(e) => setText(e.target.value)}
            maxLength={MAX_TEXT}
            aria-invalid={!!errors.text}
          />
          <span className="self-end text-body-sm text-onSurfaceVariant">
            {t("notes.create.counter", { count: text.length, max: MAX_TEXT })}
          </span>
          {errors.text && <span role="alert" className="text-body-sm text-error">{errors.text[0]}</span>}
        </label>
      )}
      <button
        type="submit"
        disabled={!canSubmit}
        className="self-end inline-flex items-center gap-2 rounded-radii-lg bg-primary px-5 py-2.5 text-label-lg text-onPrimary disabled:opacity-60"
      >
        {create.isPending && <Loader2 size={16} className="animate-spin" aria-hidden />}
        {t("notes.create.submit")}
      </button>
    </form>
  );
}
