import { useState } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { CalendarCheck } from "lucide-react";
import { attendanceApi } from "@anamnys/shared/api/attendance";
import type { AttendanceSummary } from "@anamnys/shared/lib/types";
import DetailCard from "@/components/patients/detail/DetailCard";
import { PERIODS, periodFrom, todayIso, type TimelinePeriod } from "@/components/timeline/timelineGeometry";

// Patient record card: attendance counts and intervals for a period. Observation only: numbers and
// neutral labels, no colours by value and no judgement. See design/specs/2026-10-27-attendance-stats-design.md §4.
export default function AttendanceCard({ patientId }: { patientId: string }) {
  const { t, i18n } = useTranslation();
  const locale = i18n.language;
  const [period, setPeriod] = useState<TimelinePeriod>("1y");
  const today = todayIso();
  const from = periodFrom(period, today);
  const query = useQuery({
    queryKey: ["attendance", patientId, period, today],
    queryFn: () => attendanceApi.summary(patientId, from),
    placeholderData: keepPreviousData,
  });
  const data = query.data;

  const periodButton = (p: TimelinePeriod) =>
    `rounded-radii-full px-3 py-1 text-label-md transition-colors ${
      period === p ? "bg-primary text-onPrimary" : "text-onSurfaceVariant hover:bg-pastelLavender"
    }`;

  return (
    <DetailCard
      collapsibleId="attendance"
      title={t("attendance.title")}
      icon={<CalendarCheck size={20} />}
      action={
        <div role="group" aria-label={t("timeline.periodLabel")} className="flex items-center gap-1">
          {PERIODS.map((p) => (
            <button key={p} type="button" aria-pressed={period === p} className={periodButton(p)} onClick={() => setPeriod(p)}>
              {t(`timeline.period.${p}`)}
            </button>
          ))}
        </div>
      }
    >
      {query.isPending ? null : query.isError || !data ? (
        <p className="text-body-md text-error">{t("attendance.loadFailed")}</p>
      ) : Object.values(data.counts).every((n) => n === 0) ? (
        <p className="text-body-md text-onSurfaceVariant">{t("attendance.empty")}</p>
      ) : (
        <Summary data={data} locale={locale} />
      )}
    </DetailCard>
  );
}

function Summary({ data, locale }: { data: AttendanceSummary; locale: string }) {
  const { t } = useTranslation();
  const nf = new Intl.NumberFormat(locale, { maximumFractionDigits: 1 });
  const dash = "—";
  const hours = (v: number) => t("attendance.hours", { value: nf.format(v) });
  const days = (v: number) => t("attendance.days", { value: nf.format(v) });
  const stat = (label: string, value: string) => (
    <div key={label}>
      <dt className="text-label-md text-onSurfaceVariant">{label}</dt>
      <dd className="text-body-md text-onSurface">{value}</dd>
    </div>
  );
  const lead = data.cancellationLeadHours;
  const interval = data.intervalDays;
  const counts = data.counts;

  return (
    <div className="flex flex-col gap-5">
      <section>
        <h3 className="mb-2 text-label-lg text-onSurface">{t("attendance.countsTitle")}</h3>
        <dl className="grid grid-cols-2 gap-3 sm:grid-cols-3">
          {stat(t("attendance.kinds.attended"), String(counts.attended))}
          {stat(t("attendance.kinds.attended_unscheduled"), String(counts.attendedUnscheduled))}
          {stat(t("attendance.kinds.no_show"), String(counts.noShow))}
          {stat(t("attendance.kinds.cancelled_provider"), String(counts.cancelledByProvider))}
          {stat(t("attendance.kinds.cancelled_patient"), String(counts.cancelledByPatient))}
          {stat(t("attendance.kinds.unrecorded"), String(counts.unrecorded))}
        </dl>
      </section>
      <section>
        <h3 className="mb-2 text-label-lg text-onSurface">{t("attendance.leadTitle")}</h3>
        <dl className="grid grid-cols-2 gap-3 sm:grid-cols-3">
          {stat(t("attendance.median"), lead ? hours(lead.median) : dash)}
          {stat(t("attendance.range"), lead ? `${nf.format(lead.min)}–${hours(lead.max)}` : dash)}
          {stat(t("attendance.cancellations"), lead ? String(lead.count) : dash)}
        </dl>
      </section>
      <section>
        <h3 className="mb-2 text-label-lg text-onSurface">{t("attendance.intervalTitle")}</h3>
        <dl className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          {stat(t("attendance.median"), interval ? days(interval.median) : dash)}
          {stat(t("attendance.range"), interval ? `${nf.format(interval.min)}–${days(interval.max)}` : dash)}
          {stat(t("attendance.lastInterval"), interval ? days(interval.last) : dash)}
          {stat(t("attendance.daysSinceLast"), data.daysSinceLast === null ? dash : days(data.daysSinceLast))}
        </dl>
      </section>
    </div>
  );
}
