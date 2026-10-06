import { useTranslation } from "react-i18next";
import { ChevronLeft, ChevronRight, Plus } from "lucide-react";
import type { AppointmentStatus } from "@anamnys/shared/lib/types";
import type { CalendarView } from "./calendarTime";
import { ALL_STATUSES } from "./status";

interface Props {
  days: string[];
  view: CalendarView;
  showViewToggle: boolean;
  status?: AppointmentStatus;
  onPrevious: () => void;
  onNext: () => void;
  onToday: () => void;
  onViewChange: (view: CalendarView) => void;
  onStatusChange: (status?: AppointmentStatus) => void;
  onNew: () => void;
}

function periodLabel(days: string[]): string {
  const fmt = (date: string, opts: Intl.DateTimeFormatOptions) =>
    new Intl.DateTimeFormat("pt-BR", { ...opts, timeZone: "UTC" }).format(new Date(`${date}T12:00:00Z`));
  const first = days[0];
  const last = days[days.length - 1];
  if (first === last) return fmt(first, { weekday: "long", day: "numeric", month: "long", year: "numeric" });
  return `${fmt(first, { day: "numeric", month: "short" })} – ${fmt(last, { day: "numeric", month: "short", year: "numeric" })}`;
}

export default function CalendarHeader(props: Props) {
  const { t } = useTranslation();
  const { days, view, showViewToggle, status } = props;
  const toggleClass = (active: boolean) =>
    `px-4 py-1.5 rounded-radii-md text-label-lg ${active ? "bg-surfaceContainerLowest text-primary shadow-sm" : "text-onSurfaceVariant"}`;

  return (
    <div className="mb-6 flex flex-col gap-4">
      <div className="flex flex-col gap-4 md:flex-row md:items-center md:justify-between">
        <div className="flex flex-wrap items-center gap-4">
          <h1 className="text-headline-lg text-onSurface">{t("calendar.title")}</h1>
          <div className="flex items-center gap-1 rounded-radii-lg border border-outlineVariant bg-surfaceContainerLowest px-2 py-1">
            <button type="button" onClick={props.onPrevious} aria-label={t("calendar.previous")} className="p-1.5 rounded-radii-full hover:bg-surfaceContainer">
              <ChevronLeft size={18} />
            </button>
            <span className="min-w-48 text-center text-label-lg text-onSurface">{periodLabel(days)}</span>
            <button type="button" onClick={props.onNext} aria-label={t("calendar.next")} className="p-1.5 rounded-radii-full hover:bg-surfaceContainer">
              <ChevronRight size={18} />
            </button>
          </div>
          <button type="button" onClick={props.onToday} className="rounded-radii-lg border border-outlineVariant px-3 py-1.5 text-label-lg text-onSurface hover:bg-surfaceContainer">
            {t("calendar.today")}
          </button>
        </div>
        <div className="flex items-center gap-3">
          {showViewToggle && (
            <div className="flex rounded-radii-lg bg-surfaceContainerHigh p-1">
              <button type="button" className={toggleClass(view === "day")} onClick={() => props.onViewChange("day")}>{t("calendar.views.day")}</button>
              <button type="button" className={toggleClass(view === "week")} onClick={() => props.onViewChange("week")}>{t("calendar.views.week")}</button>
            </div>
          )}
          <button type="button" onClick={props.onNew} className="flex items-center gap-2 rounded-radii-lg bg-primary px-4 py-2 text-label-lg text-onPrimary hover:brightness-110">
            <Plus size={18} />
            {t("calendar.newAppointment")}
          </button>
        </div>
      </div>
      <div className="flex items-center gap-3 rounded-radii-lg border border-outlineVariant bg-surfaceContainerLowest px-4 py-2">
        <label className="flex items-center gap-2 text-label-md text-onSurfaceVariant">
          {t("calendar.statusFilter.label")}
          <select
            value={status ?? ""}
            onChange={(e) => props.onStatusChange((e.target.value || undefined) as AppointmentStatus | undefined)}
            className="rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest px-2 py-1 text-body-md text-onSurface"
          >
            <option value="">{t("calendar.statusFilter.all")}</option>
            {ALL_STATUSES.map((s) => (
              <option key={s} value={s}>{t(`calendar.status.${s}`)}</option>
            ))}
          </select>
        </label>
      </div>
    </div>
  );
}
