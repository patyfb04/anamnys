import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { Repeat } from "lucide-react";
import type { Appointment, CalendarOverlay } from "@anamnys/shared/lib/types";
import { PRACTICE_ZONE, formatTime, todayIn, zonedParts } from "./calendarTime";
import { blockLabel, overlayFor } from "./overlay";
import { STATUS_STYLE } from "./status";

const MAX_PER_DAY = 3;
// Monday first, as the week view.
const WEEKDAY_KEYS = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"] as const;

interface Props {
  days: string[]; // 42 dates, Monday first
  month: string; // "YYYY-MM" being shown
  appointments: Appointment[];
  overlay?: CalendarOverlay;
  onDayClick: (date: string) => void;
}

// Six-week grid: up to three appointments per day by time and "+N", a marker for a holiday or a block. Clicking a day
// opens it in the day view.
export default function MonthView({ days, month, appointments, overlay, onDayClick }: Props) {
  const { t } = useTranslation();
  const today = todayIn(PRACTICE_ZONE);
  const byDate = useMemo(() => {
    const map = new Map<string, Appointment[]>();
    for (const a of [...appointments].sort((x, y) => x.startsAt.localeCompare(y.startsAt))) {
      const date = zonedParts(new Date(a.startsAt), PRACTICE_ZONE).date;
      map.set(date, [...(map.get(date) ?? []), a]);
    }
    return map;
  }, [appointments]);

  return (
    <div className="overflow-hidden rounded-radii-xl border border-outlineVariant bg-surfaceContainerLowest">
      <div className="grid grid-cols-7 border-b border-outlineVariant">
        {WEEKDAY_KEYS.map((k) => (
          <div key={k} className="py-2 text-center text-label-md uppercase text-onSurfaceVariant">
            {t(`calendar.month.weekdays.${k}`)}
          </div>
        ))}
      </div>
      <div className="grid grid-cols-7">
        {days.map((date) => {
          const items = byDate.get(date) ?? [];
          const extra = items.length - MAX_PER_DAY;
          const day = overlayFor(date, overlay);
          const markers = [
            ...(day.holidayName ? [{ key: "holiday", label: t("calendar.overlay.holiday", { name: day.holidayName }), style: "bg-pastelBlue text-onPastelBlue" }] : []),
            ...day.bands.map((b) => ({
              key: b.block.id,
              label: blockLabel(t, b.block),
              style: b.block.kind === "vacation" ? "bg-pastelTeal text-onPastelTeal" : b.block.kind === "holiday" ? "bg-pastelBlue text-onPastelBlue" : "bg-pastelPink text-onPastelPink",
            })),
          ];
          const inMonth = date.startsWith(month);
          return (
            <button
              key={date}
              type="button"
              onClick={() => onDayClick(date)}
              aria-label={t("calendar.month.openDay", { date: new Intl.DateTimeFormat("pt-BR", { day: "numeric", month: "long", timeZone: "UTC" }).format(new Date(`${date}T12:00:00Z`)) })}
              className={`flex min-h-28 flex-col gap-1 border-b border-l border-outlineVariant p-1.5 text-left hover:bg-pastelLavender ${inMonth ? "" : "bg-surfaceContainerLow text-onSurfaceVariant/60"}`}
            >
              <span
                className={`self-start rounded-radii-full px-1.5 text-label-lg ${date === today ? "bg-primary text-onPrimary" : inMonth ? "text-onSurface" : ""}`}
              >
                {Number(date.slice(8))}
              </span>
              {markers.map((m) => (
                <span key={m.key} title={m.label} className={`truncate rounded-radii-sm px-1 text-label-sm ${m.style}`}>
                  {m.label}
                </span>
              ))}
              {items.slice(0, MAX_PER_DAY).map((a) => (
                <span
                  key={a.id}
                  className={`flex items-center gap-1 rounded-radii-sm border-l-2 px-1 text-label-sm ${STATUS_STYLE[a.status]}`}
                >
                  {a.seriesId && <Repeat size={10} className="shrink-0" aria-label={t("series.repeatIcon")} />}
                  <span className="truncate">{formatTime(new Date(a.startsAt), PRACTICE_ZONE)} {a.patientName}</span>
                </span>
              ))}
              {extra > 0 && <span className="px-1 text-label-sm text-onSurfaceVariant">{t("calendar.month.more", { count: extra })}</span>}
            </button>
          );
        })}
      </div>
    </div>
  );
}
