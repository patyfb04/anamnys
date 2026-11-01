import { useState, type FormEvent, type ReactNode } from "react";
import { Link } from "@tanstack/react-router";
import { useMutation } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Search } from "lucide-react";
import { recordSearchApi } from "@anamnys/shared/api/analysis";
import type { RecordSearchPassage } from "@anamnys/shared/lib/types";
import DetailCard from "@/components/patients/detail/DetailCard";
import { formatDay } from "@/components/timeline/timelineGeometry";

const MIN = 3;
const MAX = 300;

// The snippet is plain text: it is cut by the server offsets into text nodes and <mark> spans, never as HTML.
function Highlighted({ snippet, highlights }: Pick<RecordSearchPassage, "snippet" | "highlights">) {
  const parts: ReactNode[] = [];
  let at = 0;
  [...highlights]
    .sort((a, b) => a.start - b.start)
    .forEach((h, i) => {
      const start = Math.max(h.start, at);
      const end = Math.min(h.end, snippet.length);
      if (end <= start) return;
      if (start > at) parts.push(snippet.slice(at, start));
      parts.push(
        <mark key={i} className="rounded-radii-sm bg-pastelLavender px-0.5 text-onSurface">
          {snippet.slice(start, end)}
        </mark>,
      );
      at = end;
    });
  if (at < snippet.length) parts.push(snippet.slice(at));
  return <>{parts}</>;
}

// "Perguntar ao prontuário" (F-16): retrieval only. Returns passages of signed notes with their origin; it never
// summarises, interprets or ranks by need. The question travels in the POST body only and is kept in no URL,
// query key or storage; the result is held by the mutation and leaves with the page when the patient changes.
// It always covers the whole signed history, not the page period.
export default function RecordSearchCard({ patientId }: { patientId: string }) {
  const { t, i18n } = useTranslation();
  const [question, setQuestion] = useState("");
  const [error, setError] = useState(false);
  // The question asked, kept in memory only, so the pager repeats that one and not the edited field.
  const [asked, setAsked] = useState("");
  const search = useMutation({
    mutationFn: ({ q, page }: { q: string; page: number }) => recordSearchApi.search(patientId, { question: q, page }),
  });

  const trimmed = question.trim();
  const invalid = trimmed.length < MIN || trimmed.length > MAX;
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (invalid) {
      setError(true);
      return;
    }
    setError(false);
    setAsked(trimmed);
    search.mutate({ q: trimmed, page: 1 });
  };
  const data = search.data;
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const firstShown = data ? (data.page - 1) * data.pageSize + 1 : 0;
  const pagerButton =
    "rounded-radii-md px-3 py-1 text-label-md text-primary hover:bg-pastelLavender disabled:cursor-default disabled:text-onSurfaceVariant disabled:hover:bg-transparent";

  return (
    <DetailCard collapsibleId="analysis-search" title={t("recordSearch.title")} icon={<Search size={20} />}>
      <div className="flex flex-col gap-4">
        <p className="text-body-md text-onSurfaceVariant">{t("recordSearch.sentence")}</p>
        <form onSubmit={submit} className="flex flex-col gap-2 sm:flex-row sm:items-start" noValidate>
          <div className="flex flex-1 flex-col gap-1">
            <label htmlFor="record-search-question" className="text-label-md text-onSurfaceVariant">
              {t("recordSearch.inputLabel")}
            </label>
            <input
              id="record-search-question"
              type="text"
              value={question}
              maxLength={MAX + 100}
              autoComplete="off"
              placeholder={t("recordSearch.placeholder")}
              aria-invalid={error && invalid}
              aria-describedby="record-search-examples"
              onChange={(e) => {
                setQuestion(e.target.value);
                setError(false);
              }}
              className="w-full rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest px-3 py-2 text-body-md text-onSurface"
            />
            <p id="record-search-examples" className="text-body-sm text-onSurfaceVariant">
              {t("recordSearch.examples")}
            </p>
            {error && invalid && (
              <p role="alert" className="text-body-sm text-error">
                {trimmed.length < MIN ? t("recordSearch.tooShort", { min: MIN }) : t("recordSearch.tooLong", { max: MAX })}
              </p>
            )}
          </div>
          <button
            type="submit"
            disabled={search.isPending}
            className="mt-0 rounded-radii-md bg-primary px-4 py-2 text-label-lg text-onPrimary hover:underline disabled:opacity-60 sm:mt-6"
          >
            {search.isPending ? t("recordSearch.searching") : t("recordSearch.submit")}
          </button>
        </form>

        <div aria-live="polite" className="flex flex-col gap-3">
          {search.isError ? (
            <p role="alert" className="text-body-md text-error">
              {t("recordSearch.failed")}
            </p>
          ) : data && data.passages.length === 0 ? (
            <p className="text-body-md text-onSurfaceVariant">{t("recordSearch.empty")}</p>
          ) : data ? (
            <>
              <p className="text-body-sm text-onSurfaceVariant">
                {t("recordSearch.count", { count: data.total })}
                {" · "}
                {t(`recordSearch.order.${data.order}`)}
              </p>
              <ul className="flex flex-col gap-3">
                {data.passages.map((p, i) => (
                  <li key={`${p.noteId}-${p.excerptId ?? p.section}-${i}`} className="flex flex-col gap-2 rounded-radii-lg border border-outlineVariant p-4">
                    <p className="text-label-md text-onSurfaceVariant">
                      {[formatDay(p.on, i18n.language), t(`noteView.sections.${p.section}`, { defaultValue: p.section })].join(" · ")}
                    </p>
                    <p className="whitespace-pre-wrap break-words text-body-md text-onSurface">
                      <Highlighted snippet={p.snippet} highlights={p.highlights} />
                    </p>
                    <p className="text-body-sm text-onSurfaceVariant">
                      {t("recordSearch.matchedBy", {
                        how:
                          p.matchedBy === "theme" && p.term
                            ? t("recordSearch.matchedTheme", { label: p.term.label })
                            : t("recordSearch.matchedText"),
                      })}
                    </p>
                    <Link
                      to="/notes/$noteId"
                      params={{ noteId: p.noteId }}
                      search={{ excerpt: p.excerptId ?? undefined }}
                      className="self-start text-label-md text-primary hover:underline"
                    >
                      {t("recordSearch.openNote")}
                    </Link>
                  </li>
                ))}
              </ul>
              {pages > 1 && (
                <div className="flex flex-wrap items-center gap-2">
                  <button type="button" className={pagerButton} disabled={search.isPending || data.page === 1} onClick={() => search.mutate({ q: asked, page: data.page - 1 })}>
                    {t("recordSearch.previousPage")}
                  </button>
                  <span className="text-body-sm text-onSurfaceVariant">
                    {t("recordSearch.pageRange", { from: firstShown, to: firstShown + data.passages.length - 1, total: data.total })}
                  </span>
                  <button type="button" className={pagerButton} disabled={search.isPending || data.page >= pages} onClick={() => search.mutate({ q: asked, page: data.page + 1 })}>
                    {t("recordSearch.nextPage")}
                  </button>
                </div>
              )}
            </>
          ) : null}
        </div>
      </div>
    </DetailCard>
  );
}
