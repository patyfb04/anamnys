import { useEffect, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Loader2 } from "lucide-react";
import { patientQuotesApi } from "@anamnys/shared/api/analysis";
import { ApiError } from "@anamnys/shared/api/client";
import { patientsApi } from "@anamnys/shared/api/patients";
import type { QuoteSelection } from "@anamnys/shared/lib/types";
import TermSelect from "@/components/themes/TermSelect";

interface Props {
  noteId: string;
  patientId: string;
  /** Selected passage set by "Marcar como fala do paciente" on a section. */
  selection: QuoteSelection | null;
  onSelectionDone: () => void;
}

// "Falas que você registrou" on the note view: records the selected passage as a quote (optional theme) and
// lists this note's quotes, also while the note is in review. The text is the note's own passage.
export default function NoteQuotes({ noteId, patientId, selection, onSelectionDone }: Props) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const blockRef = useRef<HTMLElement>(null);
  const [termId, setTermId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const quotes = useQuery({ queryKey: ["quotes", "note", noteId], queryFn: () => patientQuotesApi.forNote(noteId) });
  const patient = useQuery({ queryKey: ["patient", patientId], queryFn: () => patientsApi.get(patientId) });
  const readOnly = !!patient.data?.archivedAt;

  useEffect(() => {
    if (!selection) return;
    setTermId(null);
    setError(null);
    blockRef.current?.scrollIntoView({ behavior: "smooth", block: "center" });
  }, [selection]);

  // The text as selected: if the section changes meanwhile, the form keeps showing it and the server refuses
  // it (invalid_selection), instead of a different passage being shown under the old offsets.
  const passage = selection?.quote ?? "";

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ["quotes"] });
  };
  const fail = (e: unknown) => {
    const code = e instanceof ApiError ? e.code : undefined;
    setError(code ? t(`patientQuotes.errors.${code}`, { defaultValue: t("patientQuotes.actionFailed") }) : t("patientQuotes.actionFailed"));
    refresh();
  };
  const close = () => {
    setTermId(null);
    setError(null);
    onSelectionDone();
  };

  const record = useMutation({
    mutationFn: () =>
      patientQuotesApi.record(noteId, {
        sectionKey: selection!.sectionKey,
        offsetStart: selection!.start ?? 0,
        offsetEnd: selection!.end ?? 0,
        quote: passage,
        ...(termId ? { termId } : {}),
      }),
    onSuccess: () => {
      close();
      refresh();
    },
    onError: fail,
  });
  const remove = useMutation({
    mutationFn: (id: string) => patientQuotesApi.remove(id),
    onSuccess: () => {
      setError(null);
      refresh();
    },
    onError: fail,
  });

  const items = quotes.data ?? [];

  return (
    <section ref={blockRef} aria-labelledby={`quotes-${noteId}`} className="mt-6 flex flex-col gap-3 border-t border-outlineVariant pt-4">
      <h3 id={`quotes-${noteId}`} className="text-title-md text-onSurface">
        {t("patientQuotes.noteTitle")}
      </h3>
      {quotes.isError && <p className="text-body-md text-error">{t("patientQuotes.loadFailed")}</p>}
      {quotes.isSuccess && items.length === 0 && <p className="text-body-md text-onSurfaceVariant">{t("patientQuotes.empty")}</p>}
      {items.length > 0 && (
        <ul className="flex flex-col divide-y divide-outlineVariant">
          {items.map((q) => (
            <li key={q.id} className="flex items-start gap-2 py-2">
              <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                <blockquote className="whitespace-pre-wrap break-words border-l-2 border-outlineVariant pl-3 text-body-md text-onSurface">
                  {q.text}
                </blockquote>
                <span className="text-body-sm text-onSurfaceVariant">
                  {[q.section && t(`noteView.sections.${q.section}`, { defaultValue: q.section }), q.term?.label].filter(Boolean).join(" · ")}
                </span>
              </div>
              {!readOnly && (
                <button
                  type="button"
                  disabled={remove.isPending}
                  onClick={() => remove.mutate(q.id)}
                  className="shrink-0 rounded-radii-md px-2 py-1 text-label-md text-primary hover:underline disabled:opacity-60"
                >
                  {t("patientQuotes.remove")}
                </button>
              )}
            </li>
          ))}
        </ul>
      )}

      {readOnly ? (
        <p className="text-body-sm text-onSurfaceVariant">{t("patientQuotes.archivedReadOnly")}</p>
      ) : (
        selection && (
          <form
            className="flex flex-col gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              if (!record.isPending && passage) record.mutate();
            }}
          >
            <p className="text-body-sm text-onSurfaceVariant">{t("patientQuotes.selectionHint")}</p>
            <blockquote className="whitespace-pre-wrap break-words border-l-2 border-outlineVariant pl-3 text-body-md text-onSurface">
              {passage}
            </blockquote>
            <TermSelect label={t("patientQuotes.termLabel")} value={termId} onChange={setTermId} />
            <div className="flex gap-2">
              <button
                type="submit"
                disabled={record.isPending || !passage}
                className="inline-flex items-center gap-2 rounded-radii-lg bg-primary px-4 py-2 text-label-lg text-onPrimary disabled:opacity-60"
              >
                {record.isPending && <Loader2 size={14} className="animate-spin" aria-hidden />}
                {t("patientQuotes.save")}
              </button>
              <button
                type="button"
                disabled={record.isPending}
                onClick={close}
                className="rounded-radii-lg px-4 py-2 text-label-lg text-primary hover:bg-pastelLavender disabled:opacity-60"
              >
                {t("common.cancel")}
              </button>
            </div>
          </form>
        )
      )}
      {error && (
        <p role="alert" className="text-body-sm text-error">
          {error}
        </p>
      )}
    </section>
  );
}
