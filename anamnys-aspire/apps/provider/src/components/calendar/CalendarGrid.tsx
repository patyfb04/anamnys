import { useEffect, useMemo, useRef, useState, type MouseEvent } from "react";
import { useTranslation } from "react-i18next";
import type { Appointment, BlockItem, CalendarOverlay } from "@anamnys/shared/lib/types";
import AppointmentCard from "./AppointmentCard";
import { PRACTICE_ZONE, isWeekend, offsetLabel, todayIn, zonedParts } from "./calendarTime";
import { BAND_STYLE, HOLIDAY_BAND_STYLE, blockLabel, outsideSegments, overlayFor, type DayOverlay } from "./overlay";

const HOUR_PX = 56;
const DEFAULT_START_HOUR = 7;
const DEFAULT_END_HOUR = 21;
const SNAP_MINUTES = 15;

interface Props {
  days: string[];
  appointments: Appointment[];
  overlay?: CalendarOverlay;
  // Whether saved hours apply on the date; without them nothing is shaded.
  hasHours: (date: string) => boolean;
  onSlotClick: (date: string, minutes: number) => void;
  onAppointmentClick: (appointment: Appointment) => void;
}

export default function CalendarGrid({ days, appointments, overlay, hasHours, onSlotClick, onAppointmentClick }: Props) {
  const { t } = useTranslation();
  const scrollRef = useRef<HTMLDivElement>(null);
  const dayOverlays = useMemo(() => new Map(days.map((d) => [d, overlayFor(d, overlay)])), [days, overlay]);
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const timer = setInterval(() => setNow(new Date()), 60_000);
    return () => clearInterval(timer);
  }, []);
  const today = todayIn(PRACTICE_ZONE);

  // Place each appointment on its start day; extend the hour range to fit any outliers.
  const placed = useMemo(
    () =>
      appointments.map((a) => {
        const start = zonedParts(new Date(a.startsAt), PRACTICE_ZONE);
        const minutes = (new Date(a.endsAt).getTime() - new Date(a.startsAt).getTime()) / 60000;
        return { a, date: start.date, startMin: start.minutes, endMin: start.minutes + minutes };
      }),
    [appointments],
  );
  const startHour = Math.min(DEFAULT_START_HOUR, ...placed.map((p) => Math.floor(p.startMin / 60)));
  const endHour = Math.min(24, Math.max(DEFAULT_END_HOUR, ...placed.map((p) => Math.ceil(p.endMin / 60))));
  const hours = Array.from({ length: endHour - startHour }, (_, i) => startHour + i);
  const toPx = (minutes: number) => ((minutes - startHour * 60) / 60) * HOUR_PX;

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: (DEFAULT_START_HOUR - startHour) * HOUR_PX });
  }, [startHour]);

  const nowParts = zonedParts(now, PRACTICE_ZONE);
  const weekday = new Intl.DateTimeFormat("pt-BR", { weekday: "short", timeZone: "UTC" });

  const handleColumnClick = (date: string, e: MouseEvent<HTMLDivElement>) => {
    const y = e.clientY - e.currentTarget.getBoundingClientRect().top;
    const minutes = startHour * 60 + Math.floor(((y / HOUR_PX) * 60) / SNAP_MINUTES) * SNAP_MINUTES;
    onSlotClick(date, minutes);
  };

  return (
    <div className="rounded-radii-xl border border-outlineVariant bg-surfaceContainerLowest overflow-hidden">
      <div className="grid border-b border-outlineVariant" style={{ gridTemplateColumns: `4rem repeat(${days.length}, minmax(0, 1fr))` }}>
        <div className="flex items-end justify-center pb-2 text-label-md text-onSurfaceVariant">{offsetLabel(PRACTICE_ZONE, now)}</div>
        {days.map((date) => (
          <div
            key={date}
            className={`py-3 text-center ${date === today ? "bg-primaryFixed/40 text-primary" : isWeekend(date) ? "text-onSurfaceVariant/60" : "text-onSurface"}`}
          >
            <div className="text-label-md uppercase">{weekday.format(new Date(`${date}T12:00:00Z`))}</div>
            <div className="text-headline-md">{Number(date.slice(8))}</div>
            {dayOverlays.get(date)?.holidayName && (
              <div className="mx-1 mt-1 truncate rounded-radii-sm bg-pastelBlue px-1 text-label-sm text-onPastelBlue">
                {t("calendar.overlay.holiday", { name: dayOverlays.get(date)!.holidayName })}
              </div>
            )}
          </div>
        ))}
      </div>
      <div ref={scrollRef} className="max-h-[70vh] overflow-y-auto">
        <div className="relative grid" style={{ gridTemplateColumns: `4rem repeat(${days.length}, minmax(0, 1fr))` }}>
          <div>
            {hours.map((h) => (
              <div key={h} style={{ height: HOUR_PX }} className="pr-2 text-right text-label-md text-onSurfaceVariant">
                {String(h).padStart(2, "0")}:00
              </div>
            ))}
          </div>
          {days.map((date) => (
            <div
              key={date}
              onClick={(e) => handleColumnClick(date, e)}
              style={{ height: hours.length * HOUR_PX }}
              className={`relative cursor-pointer border-l border-outlineVariant ${date === today ? "bg-primaryFixed/15" : isWeekend(date) ? "bg-surfaceContainerLow" : ""}`}
            >
              {hours.map((h) => (
                <div key={h} style={{ top: (h - startHour) * HOUR_PX }} className="absolute inset-x-0 border-t border-outlineVariant/60" />
              ))}
              <DayOverlayLayer
                overlay={dayOverlays.get(date)!}
                shade={dayOverlays.get(date)!.known && (hasHours(date) || dayOverlays.get(date)!.windows.length > 0)}
                from={startHour * 60}
                to={endHour * 60}
                toPx={toPx}
                holidayLabel={(name) => t("calendar.overlay.holiday", { name })}
                bandLabel={(b) => blockLabel(t, b)}
              />
              {placed
                .filter((p) => p.date === date)
                .map((p) => (
                  <AppointmentCard
                    key={p.a.id}
                    appointment={p.a}
                    top={toPx(p.startMin)}
                    height={Math.max(toPx(Math.min(p.endMin, endHour * 60)) - toPx(p.startMin), 24)}
                    onClick={() => onAppointmentClick(p.a)}
                  />
                ))}
              {date === today && nowParts.minutes >= startHour * 60 && nowParts.minutes <= endHour * 60 && (
                <div style={{ top: toPx(nowParts.minutes) }} className="pointer-events-none absolute inset-x-0 z-10 flex items-center">
                  <span className="-ml-1 size-2 rounded-full bg-primary" />
                  <span className="h-px flex-1 bg-primary" />
                </div>
              )}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

interface LayerProps {
  overlay: DayOverlay;
  shade: boolean;
  from: number;
  to: number;
  toPx: (minutes: number) => number;
  holidayLabel: (name: string) => string;
  bandLabel: (block: BlockItem) => string;
}

// Under the cards and click-through: shaded hours outside the windows, then the holiday and the block bands.
function DayOverlayLayer({ overlay, shade, from, to, toPx, holidayLabel, bandLabel }: LayerProps) {
  const clamp = (m: number) => Math.min(Math.max(m, from), to);
  const box = (start: number, end: number) => ({ top: toPx(clamp(start)), height: toPx(clamp(end)) - toPx(clamp(start)) });
  return (
    <div className="pointer-events-none absolute inset-0 z-0" aria-hidden>
      {shade &&
        outsideSegments(overlay.windows, from, to).map((s) => (
          <div key={`o${s.start}`} style={box(s.start, s.end)} className="absolute inset-x-0 bg-onSurface/[0.06]" />
        ))}
      {overlay.holidayName && (
        <div style={box(from, to)} className={`absolute inset-x-0.5 rounded-radii-sm px-1 pt-0.5 text-label-sm ${HOLIDAY_BAND_STYLE}`}>
          <span className="block truncate">{holidayLabel(overlay.holidayName)}</span>
        </div>
      )}
      {overlay.bands
        .filter((b) => b.end > from && b.start < to)
        .map((b) => (
          <div key={b.block.id} style={box(b.start, b.end)} className={`absolute inset-x-0.5 rounded-radii-sm px-1 pt-0.5 text-label-sm ${BAND_STYLE[b.block.kind]}`}>
            <span className="block truncate">{bandLabel(b.block)}</span>
          </div>
        ))}
    </div>
  );
}
