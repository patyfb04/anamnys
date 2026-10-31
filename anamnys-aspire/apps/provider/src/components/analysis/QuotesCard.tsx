import { useState } from "react";
import { Link } from "@tanstack/react-router";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Quote } from "lucide-react";
import { patientQuotesApi } from "@anamnys/shared/api/analysis";
import DetailCard from "@/components/patients/detail/DetailCard";
import ThemeFilter from "@/components/themes/ThemeFilter";
import { formatDay, periodFrom, todayIso, type TimelinePeriod } from "@/components/timeline/timelineGeometry";

// "Falas do paciente que você registrou": the quotes the provider marked in signed notes, in date order,
// beside their dates. Observation only: no interpretation, no colours. The theme filter and the page period
// are the only controls; only the period and the term id travel in the URL.
export default function QuotesCard({ patientId, period }: { patientId: string; period: TimelinePeriod }) {
  const { t, i18n } = useTranslation();
  const today = todayIso();
  const from = periodFrom(period, today);
  const [termId, setTermId] = useState("");
  const [page, setPage] = useState(1);
  const [filterKey, setFilterKey] = useState(`${period}|`);
  // The page restarts when the filter or the period changes.
  const key = `${period}|${termId}`;
  if (filterKey !== key) {
    setFilterKey(key);
    setPage(1);
  }

  const query = useQuery({
    queryKey: ["quotes", "patient", patientId, from ?? "", termId, page],
    queryFn: () => patientQuotesApi.forPatient(patientId, { from, termId: termId || undefined, page }),
    placeholderData: keepPreviousData,
  });
  const data = query.data;
  const pagerButton =
    "rounded-radii-md px-3 py-1 text-label-md text-primary hover:bg-pastelLavender disabled:cursor-default disabled:text-onSurfaceVariant disabled:hover:bg-transparent";
  // A removal can leave the page past the end: go back to the last page that has quotes.
  if (data && !query.isPlaceholderData && data.items.length === 0 && data.total > 0 && page > 1) {
    setPage(Math.ceil(data.total / data.pageSize));
  }

  return (
    <DetailCard collapsibleId="analysis-quotes" title={t("patientQuotes.title")} icon={<Quote size={20} />}>
      <div className="flex flex-col gap-4">
        <p className="text-body-md text-onSurfaceVariant">{t("patientQuotes.card.sentence")}</p>
        <ThemeFilter value={termId} onChange={setTermId} />
        {query.isPending ? null : query.isError || !data ? (
          <p role="alert" className="text-body-md text-error">
            {t("patientQuotes.loadFailed")}
          </p>
        ) : data.items.length === 0 ? (
          <p className="text-body-md text-onSurfaceVariant">{t("patientQuotes.card.empty")}</p>
        ) : (
          <>
            <p className="text-body-sm text-onSurfaceVariant">{t("patientQuotes.card.count", { count: data.total })}</p>
            <ul className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
              {data.items.map((q) => (
                <li key={q.id} className="flex flex-col gap-2 rounded-radii-lg border border-outlineVariant p-4">
                  <p className="text-label-md text-onSurfaceVariant">{formatDay(q.on, i18n.language)}</p>
                  <blockquote className="whitespace-pre-wrap break-words text-body-md text-onSurface">{q.text}</blockquote>
                  <p className="text-body-sm text-onSurfaceVariant">
                    {[q.section && t(`noteView.sections.${q.section}`, { defaultValue: q.section }), q.term?.label ?? t("patientQuotes.card.withoutTerm")]
                      .filter(Boolean)
                      .join(" · ")}
                  </p>
                  <Link
                    to="/notes/$noteId"
                    params={{ noteId: q.noteId }}
                    search={{ excerpt: q.excerptId }}
                    className="self-start text-label-md text-primary hover:underline"
                  >
                    {t("patientQuotes.card.openNote")}
                  </Link>
                </li>
              ))}
            </ul>
            {data.total > data.pageSize && (
              <div className="flex flex-wrap items-center gap-2">
                <button type="button" className={pagerButton} disabled={page <= 1} onClick={() => setPage(page - 1)}>
                  {t("patientQuotes.card.previousPage")}
                </button>
                <span className="text-body-sm text-onSurfaceVariant" aria-live="polite">
                  {t("patientQuotes.card.pageRange", {
                    from: (data.page - 1) * data.pageSize + 1,
                    to: (data.page - 1) * data.pageSize + data.items.length,
                    total: data.total,
                  })}
                </span>
                <button
                  type="button"
                  className={pagerButton}
                  disabled={data.page * data.pageSize >= data.total}
                  onClick={() => setPage(page + 1)}
                >
                  {t("patientQuotes.card.nextPage")}
                </button>
              </div>
            )}
          </>
        )}
      </div>
    </DetailCard>
  );
}
