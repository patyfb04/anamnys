import { Link } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { patientQuotesApi } from "@anamnys/shared/api/analysis";
import Modal from "@/components/patients/Modal";
import { formatDay } from "@/components/timeline/timelineGeometry";
import type { CellItem } from "./analysisModel";

// What a cell records: the quote with its section (as stored when the theme was tagged), the other
// themes recorded in the same session, and a link to the note. Quotes are clinical text: shown here
// only, never put in a URL.
export default function OccurrenceModal({
  patientId,
  on,
  noteId,
  items,
  termIds,
  sameSession,
  onClose,
}: {
  patientId?: string;
  on: string;
  noteId: string;
  items: CellItem[];
  /** Terms whose quotes to list; given together with patientId. */
  termIds?: string[];
  sameSession: string[];
  onClose: () => void;
}) {
  const { t, i18n } = useTranslation();
  // The quotes recorded in this session for the cell's themes (the cell may stand for a parent and its children).
  const quotesQuery = useQuery({
    queryKey: ["quotes", "patient", patientId, "day", on],
    queryFn: () => patientQuotesApi.forDay(patientId!, on),
    enabled: !!patientId,
  });
  const cellTerms = new Set(termIds);
  const quotes = (quotesQuery.data ?? []).filter((q) => q.noteId === noteId && q.term && cellTerms.has(q.term.termId));
  return (
    <Modal
      title={t("analysis.passage.title", { date: formatDay(on, i18n.language) })}
      onClose={onClose}
      footer={
        <>
          {items.length <= 1 && (
            <Link
              to="/notes/$noteId"
              params={{ noteId }}
              search={{ excerpt: items[0]?.occurrence.excerptId ?? undefined }}
              className="rounded-radii-lg px-4 py-2 text-label-lg text-primary hover:underline"
            >
              {t("analysis.passage.openNote")}
            </Link>
          )}
          <button type="button" onClick={onClose} className="rounded-radii-lg px-4 py-2 text-label-lg text-primary hover:bg-pastelLavender">
            {t("analysis.passage.close")}
          </button>
        </>
      }
    >
      <div className="flex flex-col gap-4 p-5">
        {items.map(({ theme, occurrence: o }, i) => (
          <div key={`${theme.termId}-${i}`} className="flex flex-col gap-1">
            <p className="text-label-lg text-onSurface">
              {theme.label}
              {o.section && (
                <span className="text-onSurfaceVariant"> · {t(`noteView.sections.${o.section}`, { defaultValue: o.section })}</span>
              )}
            </p>
            {o.quote ? (
              <blockquote className="whitespace-pre-wrap break-words border-l-2 border-outlineVariant pl-3 text-body-md text-onSurface">
                {o.quote}
              </blockquote>
            ) : (
              <p className="text-body-md text-onSurfaceVariant">{t("analysis.passage.noQuote")}</p>
            )}
            {items.length > 1 && (
              <Link
                to="/notes/$noteId"
                params={{ noteId }}
                search={{ excerpt: o.excerptId ?? undefined }}
                className="self-start text-label-md text-primary hover:underline"
              >
                {t("analysis.passage.openNoteFor", { label: theme.label })}
              </Link>
            )}
          </div>
        ))}
        {quotesQuery.isError && <p className="text-body-sm text-error">{t("patientQuotes.loadFailed")}</p>}
        {quotes.length > 0 && (
          <div className="flex flex-col gap-2">
            <p className="text-label-lg text-onSurface">{t("patientQuotes.cell.title")}</p>
            {quotes.map((q) => (
              <blockquote key={q.id} className="whitespace-pre-wrap break-words border-l-2 border-outlineVariant pl-3 text-body-md text-onSurface">
                {q.text}
                <span className="block text-body-sm text-onSurfaceVariant">{q.term?.label}</span>
              </blockquote>
            ))}
          </div>
        )}
        {sameSession.length > 0 && (
          <p className="text-body-sm text-onSurfaceVariant">{t("analysis.passage.sameSession", { labels: sameSession.join(", ") })}</p>
        )}
      </div>
    </Modal>
  );
}
