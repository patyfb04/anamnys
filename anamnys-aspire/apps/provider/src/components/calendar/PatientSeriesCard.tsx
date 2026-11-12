import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Repeat } from "lucide-react";
import { appointmentSeriesApi } from "@anamnys/shared/api/appointmentSeries";
import type { SeriesSummary } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import DetailCard from "@/components/patients/detail/DetailCard";
import ConfirmDialog from "@/components/patients/ConfirmDialog";
import { usePatientSeriesQuery, useSeriesMutations } from "@/hooks/useAppointments";
import { PRACTICE_ZONE, formatDate, todayIn, zonedParts } from "./calendarTime";
import { seriesErrorMessage } from "./series";

// Patient page: the patient's active series with "Encerrar série" (design/specs/2026-11-09-appointment-series-design.md §5).
// Shown only when there is at least one; ending starts at the next session still ahead (or today when none is). For an
// archived patient (readOnly) the card only lists them: no "Encerrar série", and a note that generation is paused.
const capitalize = (s: string) => s.charAt(0).toUpperCase() + s.slice(1);

export default function PatientSeriesCard({ patientId, readOnly = false }: { patientId: string; readOnly?: boolean }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const query = usePatientSeriesQuery(patientId);
  const { end } = useSeriesMutations();
  const [confirm, setConfirm] = useState<{ series: SeriesSummary; fromDate: string } | null>(null);
  const [loading, setLoading] = useState<string | null>(null);

  const askEnd = async (series: SeriesSummary) => {
    setLoading(series.id);
    try {
      const detail = await queryClient.fetchQuery({
        queryKey: ["appointment-series", "detail", series.id],
        queryFn: () => appointmentSeriesApi.get(series.id),
      });
      const now = Date.now();
      const next = detail.upcoming.find(
        (o) => (o.status === "scheduled" || o.status === "confirmed") && Date.parse(o.startsAt) > now,
      );
      const fromDate = next ? zonedParts(new Date(next.startsAt), PRACTICE_ZONE).date : todayIn(PRACTICE_ZONE);
      setConfirm({ series, fromDate });
    } catch (err) {
      toast.error(seriesErrorMessage(t, err, t("series.patient.loadFailed")));
    } finally {
      setLoading(null);
    }
  };

  const doEnd = async () => {
    if (!confirm) return;
    try {
      const cancelled = await end.mutateAsync({ id: confirm.series.id, fromDate: confirm.fromDate });
      toast.success(t("series.end.done", { count: cancelled }));
    } catch (err) {
      toast.error(seriesErrorMessage(t, err, t("series.end.failed")));
    } finally {
      setConfirm(null);
    }
  };

  if (query.isError) {
    return (
      <DetailCard title={t("series.patient.title")} icon={<Repeat size={20} />}>
        <p className="text-body-md text-error">{t("series.patient.loadFailed")}</p>
      </DetailCard>
    );
  }
  if (!query.data || query.data.length === 0) return null;

  return (
    <DetailCard title={t("series.patient.title")} icon={<Repeat size={20} />}>
      <ul className="flex flex-col divide-y divide-outlineVariant">
        {query.data.map((s) => (
          <li key={s.id} className="flex flex-wrap items-center justify-between gap-2 py-2">
            <div className="flex flex-col">
              <span className="text-body-md text-onSurface first-letter:uppercase">{s.description}</span>
              <span className="text-body-sm text-onSurfaceVariant">
                {[
                  t("series.patient.since", { date: formatDate(s.startsOn) }),
                  s.endsOn ? t("series.patient.until", { date: formatDate(s.endsOn) }) : null,
                  s.occurrenceCount ? t("series.patient.count", { count: s.occurrenceCount }) : null,
                ].filter(Boolean).join(" · ")}
              </span>
            </div>
            {!readOnly && (
              <button
                type="button"
                disabled={loading === s.id || end.isPending}
                onClick={() => void askEnd(s)}
                className="px-3 py-2 rounded-radii-lg border border-outlineVariant text-label-lg text-onSurface hover:bg-pastelLavender disabled:opacity-60"
              >
                {t("series.patient.end")}
              </button>
            )}
          </li>
        ))}
      </ul>
      {readOnly && <p className="text-body-sm text-onSurfaceVariant">{t("series.patient.archivedNote")}</p>}
      <ConfirmDialog
        open={confirm !== null}
        onOpenChange={(open) => !open && setConfirm(null)}
        title={t("series.end.title")}
        description={confirm ? `${capitalize(confirm.series.description)}. ${t("series.patient.endBody", { date: formatDate(confirm.fromDate) })}` : ""}
        confirmLabel={t("series.end.confirm")}
        destructive
        pending={end.isPending}
        onConfirm={() => void doEnd()}
      />
    </DetailCard>
  );
}
