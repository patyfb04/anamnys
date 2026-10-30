import { useMemo, useState } from "react";
import { Link } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { LineChart, X } from "lucide-react";
import { analysisApi } from "@anamnys/shared/api/analysis";
import { medicationTimelineApi } from "@anamnys/shared/api/medicationTimeline";
import DetailCard from "@/components/patients/detail/DetailCard";
import { dayMs, formatDay, periodFrom, todayIso, type TimelinePeriod } from "@/components/timeline/timelineGeometry";
import TrajectoryChart, { EventMark, type TrajectorySelection } from "./TrajectoryChart";
import {
  DEFAULT_TOGGLES,
  dashFor,
  EVENT_GROUPS,
  axisDomain,
  buildEvents,
  defaultInstrument,
  instrumentOptions,
  pointsFor,
  type EventGroup,
  type EventKind,
  type TrajectoryEvent,
} from "./trajectoryModel";

const LEGEND_KINDS: Record<EventGroup, EventKind[]> = {
  medication: ["medication_start", "medication_dose", "medication_stop"],
  notes: ["note"],
  absences: ["no_show", "cancelled_provider", "cancelled_patient"],
};

// "Trajetória de escala": one instrument's scores over the page period with recorded events on the same axis.
// Observation only: one neutral colour, no bands or trend line, and no statement relating events and scores.
// Uses the same queries (and keys) as the rest of the page, so nothing is fetched twice.
export default function TrajectoryCard({
  patientId,
  period,
  minimumSessions,
}: {
  patientId: string;
  period: TimelinePeriod;
  /** Set only when the analysis is unavailable: notes are then not listed. */
  minimumSessions?: number;
}) {
  const { t, i18n } = useTranslation();
  const locale = i18n.language;
  const today = todayIso();
  const from = periodFrom(period, today);
  const timeline = useQuery({
    queryKey: ["timeline", patientId, period, today],
    queryFn: () => medicationTimelineApi.timeline(patientId, from),
  });
  const analysis = useQuery({
    queryKey: ["analysis", patientId, period, today],
    queryFn: () => analysisApi.get(patientId, from),
  });
  const [chosen, setChosen] = useState<string | null>(null);
  const [toggles, setToggles] = useState<Record<EventGroup, boolean>>(DEFAULT_TOGGLES);
  const [selection, setSelection] = useState<TrajectorySelection | null>(null);

  const fromMs = from ? dayMs(from) : -Infinity;
  const toMs = dayMs(today);
  const data = timeline.data;

  // Only instruments with at least one point in the period can be picked, so the picker never strands the
  // provider on an empty chart.
  const options = useMemo(
    () => (data ? instrumentOptions(data, fromMs, toMs).filter((o) => o.count > 0) : []),
    [data, fromMs, toMs],
  );
  const instrumentId = options.some((o) => o.instrumentId === chosen) ? chosen : defaultInstrument(options);
  const scale = data?.scales.find((s) => s.instrumentId === instrumentId);
  const points = useMemo(() => (scale ? pointsFor(scale, fromMs, toMs) : []), [scale, fromMs, toMs]);
  const events = useMemo(() => buildEvents(data, analysis.data, toggles, fromMs, toMs), [data, analysis.data, toggles, fromMs, toMs]);
  const { startMs, endMs } = axisDomain(from, today, [...points.map((p) => p.ms), ...events.map((e) => e.ms)]);

  const eventLabel = (e: TrajectoryEvent): string => {
    const dose = e.dose ?? t("timeline.noDose");
    return t(`analysis.trajectory.events.${e.kind}`, { drug: e.drug ?? "", dose, date: formatDay(e.on, locale) });
  };

  const toggle = (g: EventGroup) => {
    setToggles((prev) => ({ ...prev, [g]: !prev[g] }));
    setSelection(null);
  };

  return (
    <DetailCard title={t("analysis.trajectory.title")} icon={<LineChart size={20} />}>
      {timeline.isPending ? null : timeline.isError || !data ? (
        <p role="alert" className="text-body-md text-error">
          {t("analysis.trajectory.loadFailed")}
        </p>
      ) : !scale || points.length === 0 ? (
        <p className="text-body-md text-onSurfaceVariant">{t("analysis.trajectory.empty")}</p>
      ) : (
        <div className="flex flex-col gap-4">
          <div className="flex flex-wrap items-end gap-x-6 gap-y-3">
            <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
              {t("analysis.trajectory.instrument")}
              <select
                value={scale.instrumentId}
                onChange={(e) => {
                  setChosen(e.target.value);
                  setSelection(null);
                }}
                className="rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest px-3 py-1.5 text-body-md text-onSurface"
              >
                {options.map((o) => (
                  <option key={o.instrumentId} value={o.instrumentId}>
                    {o.name} ({t("analysis.trajectory.applications", { count: o.count })})
                  </option>
                ))}
              </select>
            </label>
            <fieldset className="flex flex-wrap gap-x-2 gap-y-1">
              <legend className="mb-1 text-label-md text-onSurfaceVariant">{t("analysis.trajectory.eventsLabel")}</legend>
              {EVENT_GROUPS.map((g) => (
                <label key={g} className="flex cursor-pointer items-center gap-2 rounded-radii-md px-2 py-1 text-body-md text-onSurface hover:bg-pastelLavender">
                  <input type="checkbox" checked={toggles[g]} onChange={() => toggle(g)} />
                  {t(`analysis.trajectory.groups.${g}`)}
                </label>
              ))}
              {minimumSessions !== undefined && (
                <p className="basis-full px-2 text-body-sm text-onSurfaceVariant">{t("analysis.trajectory.notesUnavailable", { min: minimumSessions })}</p>
              )}
            </fieldset>
          </div>

          <TrajectoryChart
            scale={scale}
            points={points}
            events={events}
            startMs={startMs}
            endMs={endMs}
            selection={selection}
            eventLabel={eventLabel}
            onSelect={setSelection}
          />

          <ul className="flex flex-wrap gap-x-4 gap-y-1">
            {EVENT_GROUPS.filter((g) => toggles[g]).flatMap((g) =>
              LEGEND_KINDS[g].map((k) => (
                <li key={k} className="flex items-center gap-1.5 text-body-sm text-onSurfaceVariant">
                  <svg width={28} height={18} viewBox="0 0 28 18" aria-hidden>
                    <line x1={0} x2={9} y1={9} y2={9} className="stroke-onSurfaceVariant" strokeWidth={1.5} strokeDasharray={dashFor(k)} />
                    <EventMark kind={k} cx={19} cy={9} />
                  </svg>
                  {t(`analysis.trajectory.kinds.${k}`)}
                </li>
              )),
            )}
          </ul>

          {selection && <Details selection={selection} scaleName={scale.name} patientId={patientId} eventLabel={eventLabel} onClose={() => setSelection(null)} />}

          <div className="flex flex-col gap-1">
            <p className="text-label-lg text-onSurface">{t("analysis.trajectory.listTitle")}</p>
            {events.length === 0 ? (
              <p className="text-body-sm text-onSurfaceVariant">{t("analysis.trajectory.noEvents")}</p>
            ) : (
              <EventList events={events} locale={locale} eventLabel={eventLabel} />
            )}
          </div>
        </div>
      )}
    </DetailCard>
  );
}

const EVENTS_PER_PAGE = 10;

// The period's events as text, ten per page; the page resets when the list itself changes.
function EventList({
  events,
  locale,
  eventLabel,
}: {
  events: TrajectoryEvent[];
  locale: string;
  eventLabel: (e: TrajectoryEvent) => string;
}) {
  const { t } = useTranslation();
  const [page, setPage] = useState(0);
  const [listKey, setListKey] = useState(events);
  if (listKey !== events) {
    setListKey(events);
    setPage(0);
  }
  const pages = Math.max(1, Math.ceil(events.length / EVENTS_PER_PAGE));
  const current = Math.min(page, pages - 1);
  const first = current * EVENTS_PER_PAGE;
  const shown = events.slice(first, first + EVENTS_PER_PAGE);
  const pagerButton =
    "rounded-radii-md px-3 py-1 text-label-md text-primary hover:bg-pastelLavender disabled:cursor-default disabled:text-onSurfaceVariant disabled:hover:bg-transparent";

  return (
    <div className="flex flex-col gap-2">
      <ul className="flex flex-col">
        {shown.map((e) => (
          <li key={e.id} className="text-body-sm text-onSurfaceVariant">
            <span className="text-onSurface">{formatDay(e.on, locale)}</span> {eventLabel(e)}
          </li>
        ))}
      </ul>
      {pages > 1 && (
        <div className="flex flex-wrap items-center gap-2">
          <button type="button" className={pagerButton} disabled={current === 0} onClick={() => setPage(current - 1)}>
            {t("analysis.trajectory.previousPage")}
          </button>
          <span className="text-body-sm text-onSurfaceVariant" aria-live="polite">
            {t("analysis.trajectory.pageRange", { from: first + 1, to: first + shown.length, total: events.length })}
          </span>
          <button type="button" className={pagerButton} disabled={current >= pages - 1} onClick={() => setPage(current + 1)}>
            {t("analysis.trajectory.nextPage")}
          </button>
        </div>
      )}
    </div>
  );
}

function Details({
  selection,
  scaleName,
  patientId,
  eventLabel,
  onClose,
}: {
  selection: TrajectorySelection;
  scaleName: string;
  patientId: string;
  eventLabel: (e: TrajectoryEvent) => string;
  onClose: () => void;
}) {
  const { t, i18n } = useTranslation();
  const locale = i18n.language;
  const row = (label: string, value: string) => (
    <div className="flex flex-col">
      <dt className="text-label-md text-onSurfaceVariant">{label}</dt>
      <dd className="break-words text-body-md text-onSurface">{value}</dd>
    </div>
  );
  const linkClass = "self-start text-label-lg text-primary hover:underline";

  let title: string;
  let body: React.ReactNode;
  if (selection.kind === "point") {
    title = scaleName;
    body = (
      <dl className="grid gap-3 sm:grid-cols-3">
        {row(t("timeline.score"), String(selection.point.score))}
        {row(t("timeline.appliedOn"), formatDay(selection.point.on, locale))}
      </dl>
    );
  } else {
    const e = selection.event;
    title = t(`analysis.trajectory.kinds.${e.kind}`);
    body = (
      <>
        <dl className="grid gap-3 sm:grid-cols-3">
          {row(t("timeline.date"), formatDay(e.on, locale))}
          {e.drug && row(t("analysis.trajectory.drug"), e.drug)}
          {e.group === "medication" && e.kind !== "medication_stop" && row(t("timeline.dose"), e.dose ?? t("timeline.noDose"))}
          {e.group === "absences" && row(t("timeline.status"), t(`attendance.kinds.${e.kind}`))}
        </dl>
        {e.group === "notes" && e.noteId && (
          <Link to="/notes/$noteId" params={{ noteId: e.noteId }} className={linkClass} aria-label={eventLabel(e)}>
            {t("analysis.trajectory.openNote")}
          </Link>
        )}
        {e.group === "medication" && (
          <Link to="/patients/$patientId" params={{ patientId }} className={linkClass}>
            {t("analysis.trajectory.openRecord")}
          </Link>
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
