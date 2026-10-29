import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { GanttChartSquare, X } from "lucide-react";
import { medicationTimelineApi } from "@anamnys/shared/api/medicationTimeline";
import DetailCard from "@/components/patients/detail/DetailCard";
import DocumentPassageModal from "@/components/externalDocuments/DocumentPassageModal";
import { AttendanceLegend } from "@/components/attendance/AttendanceMark";
import TimelineChart, { type TimelineSelection } from "./TimelineChart";
import { DAY_MS, PERIODS, dayMs, formatDay, periodFrom, todayIso, type TimelinePeriod } from "./timelineGeometry";

// Patient record card: medications (a lane per treatment) and applied-scale scores on one shared date
// axis. It only places facts side by side in time; it never states a relation between them.
// The period goes to the server as dates only (no PHI in the URL).
export default function TimelineCard({ patientId, period: fixedPeriod }: { patientId: string; period?: TimelinePeriod }) {
  const { t, i18n } = useTranslation();
  const [ownPeriod, setPeriod] = useState<TimelinePeriod>("1y");
  // When the page owns the period (analysis page), the card follows it and hides its own selector.
  const period = fixedPeriod ?? ownPeriod;
  const [selection, setSelection] = useState<TimelineSelection | null>(null);
  const [passageId, setPassageId] = useState<string | null>(null);
  const today = todayIso();
  const from = periodFrom(period, today);
  const query = useQuery({
    queryKey: ["timeline", patientId, period, today],
    queryFn: () => medicationTimelineApi.timeline(patientId, from),
  });
  const data = query.data;
  const hasAppts = (data?.appointments.length ?? 0) > 0;
  const hasMeds = (data?.medications.length ?? 0) > 0;
  const hasScales = (data?.scales.length ?? 0) > 0;

  // The axis runs from the period start (or the earliest date, for "Tudo") to today.
  let startMs = Infinity;
  let endMs = dayMs(today);
  if (data) {
    for (const a of data.appointments) {
      startMs = Math.min(startMs, dayMs(a.on));
      endMs = Math.max(endMs, dayMs(a.on) + DAY_MS);
    }
    for (const m of data.medications)
      for (const s of m.segments) {
        startMs = Math.min(startMs, dayMs(s.startedOn));
        endMs = Math.max(endMs, dayMs(s.endedOn ?? s.startedOn) + DAY_MS);
      }
    for (const sc of data.scales)
      for (const p of sc.points) {
        startMs = Math.min(startMs, dayMs(p.appliedOn));
        endMs = Math.max(endMs, dayMs(p.appliedOn) + DAY_MS);
      }
  }
  if (from) startMs = dayMs(from);
  if (!Number.isFinite(startMs)) startMs = endMs - 30 * DAY_MS;

  const periodButton = (p: TimelinePeriod) =>
    `rounded-radii-full px-3 py-1 text-label-md transition-colors ${
      period === p ? "bg-primary text-onPrimary" : "text-onSurfaceVariant hover:bg-pastelLavender"
    }`;

  return (
    <DetailCard collapsibleId="timeline"
      title={t("timeline.title")}
      icon={<GanttChartSquare size={20} />}
      action={
        fixedPeriod ? undefined : <div role="group" aria-label={t("timeline.periodLabel")} className="flex items-center gap-1">
          {PERIODS.map((p) => (
            <button
              key={p}
              type="button"
              aria-pressed={period === p}
              className={periodButton(p)}
              onClick={() => {
                setPeriod(p);
                setSelection(null);
              }}
            >
              {t(`timeline.period.${p}`)}
            </button>
          ))}
        </div>
      }
    >
      {query.isPending ? null : query.isError || !data ? (
        <p className="text-body-md text-error">{t("timeline.loadFailed")}</p>
      ) : !hasAppts && !hasMeds && !hasScales ? (
        <p className="text-body-md text-onSurfaceVariant">{t("timeline.empty")}</p>
      ) : (
        <div className="flex flex-col gap-3">
          <TimelineChart data={data} startMs={startMs} endMs={endMs} selection={selection} onSelect={setSelection} />
          {hasAppts && <AttendanceLegend label={(k) => t(`attendance.kinds.${k}`)} />}
          {!hasMeds && <p className="text-body-sm text-onSurfaceVariant">{t("timeline.noMedications")}</p>}
          {!hasScales && <p className="text-body-sm text-onSurfaceVariant">{t("timeline.noScales")}</p>}
          {selection && (
            <Details selection={selection} locale={i18n.language} onClose={() => setSelection(null)} onViewDocument={setPassageId} />
          )}
        </div>
      )}
      {passageId && <DocumentPassageModal excerptId={passageId} onClose={() => setPassageId(null)} />}
    </DetailCard>
  );
}

function Details({
  selection,
  locale,
  onClose,
  onViewDocument,
}: {
  selection: TimelineSelection;
  locale: string;
  onClose: () => void;
  onViewDocument: (excerptId: string) => void;
}) {
  const { t } = useTranslation();
  const row = (label: string, value: string) => (
    <div className="flex flex-col">
      <dt className="text-label-md text-onSurfaceVariant">{label}</dt>
      <dd className="break-words text-body-md text-onSurface">{value}</dd>
    </div>
  );

  let title: string;
  let body: React.ReactNode;
  if (selection.kind === "appointment") {
    title = t("timeline.appointments");
    body = (
      <dl className="grid gap-3 sm:grid-cols-3">
        {row(t("timeline.date"), formatDay(selection.appointment.on, locale))}
        {row(t("timeline.status"), t(`attendance.kinds.${selection.appointment.kind}`))}
      </dl>
    );
  } else if (selection.kind === "point") {
    title = selection.scale.name;
    body = (
      <dl className="grid gap-3 sm:grid-cols-3">
        {row(t("timeline.score"), `${selection.point.score} (${selection.scale.minScore}–${selection.scale.maxScore})`)}
        {row(t("timeline.appliedOn"), formatDay(selection.point.appliedOn, locale))}
      </dl>
    );
  } else {
    const { segment: s } = selection;
    const o = s.origin;
    const kind = o.documentKind ? t(`externalDocuments.kinds.${o.documentKind}`) : "";
    const date = o.documentDate ? formatDay(o.documentDate, locale) : "—";
    const origin =
      o.kind === "manual"
        ? t("timeline.originManual")
        : o.kind === "document_deleted"
          ? t("timeline.originDeleted")
          : o.page
            ? t("timeline.originDocumentPage", { kind, date, page: o.page })
            : t("timeline.originDocument", { kind, date });
    title = selection.drug;
    body = (
      <>
        <dl className="grid gap-3 sm:grid-cols-3">
          {row(t("timeline.dose"), s.dose ?? t("timeline.noDose"))}
          {s.posology && row(t("timeline.posology"), s.posology)}
          {row(t("timeline.from"), formatDay(s.startedOn, locale))}
          {row(t("timeline.to"), s.endedOn ? formatDay(s.endedOn, locale) : t("timeline.ongoing"))}
          {row(t("timeline.origin"), origin)}
        </dl>
        {o.kind === "document" && o.excerptId && (
          <button
            type="button"
            onClick={() => onViewDocument(o.excerptId as string)}
            className="self-start text-label-lg text-primary hover:underline"
          >
            {t("timeline.viewInDocument")}
          </button>
        )}
      </>
    );
  }

  return (
    <div className="flex flex-col gap-3 rounded-radii-lg border border-outlineVariant p-4">
      <div className="flex items-center justify-between gap-2">
        <p className="break-words text-label-lg text-onSurface">{title}</p>
        <button
          type="button"
          onClick={onClose}
          aria-label={t("timeline.detailsClose")}
          className="rounded-radii-full p-1.5 text-onSurfaceVariant hover:bg-pastelLavender"
        >
          <X size={16} />
        </button>
      </div>
      {body}
    </div>
  );
}
