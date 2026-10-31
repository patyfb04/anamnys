import { useEffect, useRef, useState } from "react";
import { Link } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Plus } from "lucide-react";
import { planAdherenceApi } from "@anamnys/shared/api/analysis";
import { ApiError } from "@anamnys/shared/api/client";
import { patientsApi } from "@anamnys/shared/api/patients";
import type { ThemeSelection } from "@anamnys/shared/lib/types";
import { inputClass } from "@/components/patients/FormField";
import { formatInstant } from "@/components/analysis/planModel";

const MAX_DESCRIPTION = 500;

interface Props {
  noteId: string;
  patientId: string;
  /** Creation instant of this note: only follow-ups marked earlier can be resumed here. */
  noteCreatedAt: string;
  sections: { key: string; content: string }[];
  /** Selected text to keep as the origin passage (set by "Marcar para retomar" on a section). */
  selection: ThemeSelection | null;
  onSelectionDone: () => void;
}

// "Pontos para retomar" on the note view: marks a point to resume (with the selected passage as its origin)
// and lists the patient's open follow-ups, each resolvable from this note. Observation only, neutral colours.
export default function NoteFollowUps({ noteId, patientId, noteCreatedAt, sections, selection, onSelectionDone }: Props) {
  // The local day of this note, compared with the origin note's day; the server has the final word (invalid_note).
  const noteDay = new Date(noteCreatedAt).toLocaleDateString("en-CA");
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const blockRef = useRef<HTMLElement>(null);
  const [adding, setAdding] = useState(false);
  const [description, setDescription] = useState("");
  const [error, setError] = useState<string | null>(null);

  const adherence = useQuery({ queryKey: ["plan-adherence", patientId], queryFn: () => planAdherenceApi.get(patientId) });
  const patient = useQuery({ queryKey: ["patient", patientId], queryFn: () => patientsApi.get(patientId) });
  const readOnly = !!patient.data?.archivedAt;

  // A selection opens the form, prefilled with the selected text.
  useEffect(() => {
    if (!selection) return;
    const content = sections.find((s) => s.key === selection.sectionKey)?.content ?? "";
    setDescription(content.slice(selection.start ?? 0, selection.end ?? 0).slice(0, MAX_DESCRIPTION));
    setAdding(true);
    setError(null);
    blockRef.current?.scrollIntoView({ behavior: "smooth", block: "center" });
  }, [selection, sections]);

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ["plan-adherence", patientId] });
    void queryClient.invalidateQueries({ queryKey: ["notes"] });
  };
  const fail = (e: unknown) => {
    const field = e instanceof ApiError ? (e.errors?.description?.[0] ?? e.errors?.offsetStart?.[0]) : undefined;
    const code = e instanceof ApiError ? e.code : undefined;
    const generic = e instanceof ApiError && e.status === 409 ? t("followUps.errors.conflict") : t("followUps.actionFailed");
    setError(
      code ? t(`followUps.errors.${code}`, { defaultValue: field ?? generic }) : (field ?? generic),
    );
    refresh();
  };
  const close = () => {
    setAdding(false);
    setDescription("");
    setError(null);
    onSelectionDone();
  };

  const create = useMutation({
    mutationFn: () =>
      planAdherenceApi.createFollowUp(noteId, {
        description: description.trim(),
        ...(selection ? { sectionKey: selection.sectionKey, offsetStart: selection.start, offsetEnd: selection.end } : {}),
      }),
    onSuccess: () => {
      close();
      refresh();
    },
    onError: fail,
  });
  const resolve = useMutation({
    mutationFn: (id: string) => planAdherenceApi.resolve(id, noteId),
    onSuccess: () => {
      setError(null);
      refresh();
    },
    onError: fail,
  });
  const reopen = useMutation({
    mutationFn: (id: string) => planAdherenceApi.reopen(id),
    onSuccess: () => {
      setError(null);
      refresh();
    },
    onError: fail,
  });

  // Open items, and the ones resolved from this very note (so the choice can be undone).
  const items = (adherence.data?.followUps ?? []).filter((f) => !f.resolvedAt || f.resolvedNoteId === noteId);
  const actionClass = "inline-flex items-center gap-1 rounded-radii-md px-2 py-1 text-label-md text-primary hover:underline disabled:opacity-60";

  return (
    <section ref={blockRef} aria-labelledby={`followups-${noteId}`} className="mt-6 flex flex-col gap-3 border-t border-outlineVariant pt-4">
      <h3 id={`followups-${noteId}`} className="text-title-md text-onSurface">
        {t("followUps.title")}
      </h3>
      {adherence.isError && <p className="text-body-md text-error">{t("followUps.loadFailed")}</p>}
      {adherence.isSuccess && items.length === 0 && (
        <p className="text-body-md text-onSurfaceVariant">{t("followUps.empty")}</p>
      )}
      {items.length > 0 && (
        <ul className="flex flex-col divide-y divide-outlineVariant">
          {items.map((f) => (
            <li key={f.id} className="flex items-start gap-2 py-2">
              <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                <span className="break-words text-body-md text-onSurface">{f.description}</span>
                <span className="text-body-sm text-onSurfaceVariant">
                  {f.resolvedAt
                    ? t("followUps.resolvedHere")
                    : t("planAdherence.markedLine", {
                        date: formatInstant(f.createdAt),
                        since: t("planAdherence.followSince", { count: f.sessionsSince }),
                      })}
                </span>
                {f.noteId && f.noteId !== noteId && f.excerpt && (
                  <Link
                    to="/notes/$noteId"
                    params={{ noteId: f.noteId }}
                    search={{ excerpt: f.excerpt.excerptId }}
                    className="self-start text-label-md text-primary hover:underline"
                  >
                    {t("planAdherence.viewPassage")}
                  </Link>
                )}
              </div>
              {!readOnly && (f.resolvedAt || (f.noteId !== noteId && (!f.originOn || f.originOn <= noteDay))) && (
                <button
                  type="button"
                  disabled={resolve.isPending || reopen.isPending}
                  onClick={() => (f.resolvedAt ? reopen.mutate(f.id) : resolve.mutate(f.id))}
                  className={`${actionClass} shrink-0`}
                >
                  {f.resolvedAt ? t("planAdherence.reopen") : t("followUps.resolveHere")}
                </button>
              )}
            </li>
          ))}
        </ul>
      )}

      {readOnly ? (
        <p className="text-body-sm text-onSurfaceVariant">{t("followUps.archivedReadOnly")}</p>
      ) : adding ? (
        <form
          className="flex flex-col gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            if (description.trim()) create.mutate();
          }}
        >
          {selection && <p className="text-body-sm text-onSurfaceVariant">{t("followUps.forSelection")}</p>}
          <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
            {t("followUps.descriptionLabel")}
            <input
              className={inputClass}
              value={description}
              maxLength={MAX_DESCRIPTION}
              onChange={(e) => setDescription(e.target.value)}
              autoFocus
            />
          </label>
          <div className="flex gap-2">
            <button
              type="submit"
              disabled={create.isPending || !description.trim()}
              className="rounded-radii-md bg-primary px-4 py-1.5 text-label-lg text-onPrimary hover:opacity-90 disabled:opacity-60"
            >
              {t("followUps.save")}
            </button>
            <button type="button" onClick={close} className="px-3 py-1.5 text-label-lg text-onSurfaceVariant hover:text-onSurface">
              {t("common.cancel")}
            </button>
          </div>
        </form>
      ) : (
        <div>
          <button type="button" onClick={() => setAdding(true)} className={actionClass}>
            <Plus size={16} aria-hidden />
            {t("followUps.mark")}
          </button>
        </div>
      )}
      {error && (
        <p role="alert" className="text-body-sm text-error">
          {error}
        </p>
      )}
      <p className="text-body-sm text-onSurfaceVariant">{t("followUps.help")}</p>
    </section>
  );
}
