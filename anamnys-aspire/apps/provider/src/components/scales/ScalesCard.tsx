import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Activity, ChevronLeft, ChevronRight, HelpCircle } from "lucide-react";
import { ApiError } from "@anamnys/shared/api/client";
import { scalesApi } from "@anamnys/shared/api/scales";
import { toast } from "@anamnys/shared/ui/Toaster";
import type { ScaleEntry, ScaleSeries } from "@anamnys/shared/lib/types";
import DetailCard from "@/components/patients/detail/DetailCard";
import ConfirmDialog from "@/components/patients/ConfirmDialog";
import { formatDate } from "@/components/patients/format";
import ScaleChart from "./ScaleChart";
import ScaleHelpPanel from "./ScaleHelpPanel";
import ScaleRecordModal from "./ScaleRecordModal";

const actionClass = "inline-flex items-center gap-1 px-3 py-1.5 text-label-lg text-primary hover:underline";

// Patient record card: one small chart plus the entry list per instrument used. Shows scores and
// dates only, never an interpretation (design/specs/2026-10-18-psychometric-scales-design.md §6).
export default function ScalesCard({ patientId, readOnly }: { patientId: string; readOnly: boolean }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [recording, setRecording] = useState(false);
  const [helping, setHelping] = useState(false);
  const [removing, setRemoving] = useState<ScaleEntry | null>(null);
  const series = useQuery({ queryKey: ["scales", patientId], queryFn: () => scalesApi.series(patientId) });

  const remove = useMutation({
    mutationFn: (entry: ScaleEntry) => scalesApi.remove(patientId, entry.id),
    onSuccess: async () => {
      setRemoving(null);
      await queryClient.invalidateQueries({ queryKey: ["scales", patientId] });
      toast.success(t("scales.removed"));
    },
    onError: (e) => {
      setRemoving(null);
      toast.error(e instanceof ApiError ? e.message : t("scales.removeFailed"));
    },
  });

  const data = series.data ?? [];

  return (
    <DetailCard
      title={t("scales.title")}
      icon={<Activity size={20} />}
      action={
        <div className="flex items-center gap-1">
          <button type="button" onClick={() => setHelping(true)} aria-label={t("scales.helpLabel")} className="rounded-radii-full p-1.5 text-onSurfaceVariant hover:bg-pastelLavender">
            <HelpCircle size={18} />
          </button>
          {!readOnly && (
            <button type="button" onClick={() => setRecording(true)} className={actionClass}>
              {t("scales.record")}
            </button>
          )}
        </div>
      }
    >
      {series.isPending ? null : series.isError ? (
        <p className="text-body-md text-error">{t("scales.seriesLoadFailed")}</p>
      ) : data.length === 0 ? (
        <p className="text-body-md text-onSurfaceVariant">{t("scales.empty")}</p>
      ) : (
        <div className="grid gap-6 md:grid-cols-2 xl:grid-cols-3">
          {data.map((s) => (
            <SeriesBlock key={s.instrumentId} series={s} readOnly={readOnly} onRemove={setRemoving} />
          ))}
        </div>
      )}

      {recording && <ScaleRecordModal patientId={patientId} onClose={() => setRecording(false)} />}
      {helping && <ScaleHelpPanel onClose={() => setHelping(false)} />}
      <ConfirmDialog
        open={removing !== null}
        onOpenChange={(open) => !open && setRemoving(null)}
        title={t("scales.removeTitle")}
        description={t("scales.removeConfirm")}
        confirmLabel={t("scales.remove")}
        destructive
        pending={remove.isPending}
        onConfirm={() => removing && remove.mutate(removing)}
      />
    </DetailCard>
  );
}

const ENTRIES_PAGE_SIZE = 3;

// One instrument: the chart shows the whole series; the entry list below is paged, newest first.
function SeriesBlock({ series, readOnly, onRemove }: { series: ScaleSeries; readOnly: boolean; onRemove: (entry: ScaleEntry) => void }) {
  const { t, i18n } = useTranslation();
  const [page, setPage] = useState(1);
  const entries = [...series.entries].sort((a, b) => new Date(b.appliedAt).getTime() - new Date(a.appliedAt).getTime());
  const pages = Math.max(1, Math.ceil(entries.length / ENTRIES_PAGE_SIZE));
  // A removal can empty the last page; fall back to the last one that still exists.
  const current = Math.min(page, pages);
  const visible = entries.slice((current - 1) * ENTRIES_PAGE_SIZE, current * ENTRIES_PAGE_SIZE);
  const pagerButton =
    "p-1 rounded-radii-md text-onSurfaceVariant hover:bg-pastelLavender disabled:opacity-40 disabled:hover:bg-transparent transition-colors";

  return (
    <div className="flex flex-col gap-2 rounded-radii-lg border border-outlineVariant p-4">
      <p className="text-label-lg text-onSurface">
        {series.name} <span className="text-label-md text-onSurfaceVariant">{series.code}</span>
      </p>
      <ScaleChart series={series} />
      <ul className="flex flex-col divide-y divide-outlineVariant">
        {visible.map((e) => (
          <li key={e.id} className="flex items-center justify-between gap-2 py-2">
            <div className="min-w-0 text-body-md text-onSurface">
              {formatDate(e.appliedAt, i18n.language)}: <span className="text-label-lg">{e.score}</span>
              {e.appointmentStartsAt && (
                <span className="ml-2 text-label-md text-outline">
                  {t("scales.linkedAppointment", { date: formatDate(e.appointmentStartsAt, i18n.language) })}
                </span>
              )}
            </div>
            {!readOnly && (
              <button type="button" onClick={() => onRemove(e)} className="text-label-md text-onSurfaceVariant hover:underline">
                {t("scales.remove")}
              </button>
            )}
          </li>
        ))}
      </ul>
      {pages > 1 && (
        <div className="flex items-center justify-between">
          <p className="text-body-sm text-onSurfaceVariant">{t("scales.entriesPage", { page: current, pages })}</p>
          <div className="flex gap-1">
            <button
              type="button"
              className={pagerButton}
              disabled={current <= 1}
              onClick={() => setPage(current - 1)}
              aria-label={t("patients.list.previousPage")}
            >
              <ChevronLeft size={18} />
            </button>
            <button
              type="button"
              className={pagerButton}
              disabled={current >= pages}
              onClick={() => setPage(current + 1)}
              aria-label={t("patients.list.nextPage")}
            >
              <ChevronRight size={18} />
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
