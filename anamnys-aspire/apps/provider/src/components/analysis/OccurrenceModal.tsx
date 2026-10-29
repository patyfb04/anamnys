import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import Modal from "@/components/patients/Modal";
import { formatDay } from "@/components/timeline/timelineGeometry";
import type { CellItem } from "./analysisModel";

// What a cell records: the quote with its section (as stored when the theme was tagged), the other
// themes recorded in the same session, and a link to the note. Quotes are clinical text: shown here
// only, never put in a URL.
export default function OccurrenceModal({
  on,
  noteId,
  items,
  sameSession,
  onClose,
}: {
  on: string;
  noteId: string;
  items: CellItem[];
  sameSession: string[];
  onClose: () => void;
}) {
  const { t, i18n } = useTranslation();
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
        {sameSession.length > 0 && (
          <p className="text-body-sm text-onSurfaceVariant">{t("analysis.passage.sameSession", { labels: sameSession.join(", ") })}</p>
        )}
      </div>
    </Modal>
  );
}
