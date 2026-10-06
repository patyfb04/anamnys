import { useEffect, useMemo, useRef, useState, type MouseEvent } from "react";
import type { Appointment } from "@anamnys/shared/lib/types";
import AppointmentCard from "./AppointmentCard";
import { PRACTICE_ZONE, isWeekend, offsetLabel, todayIn, zonedParts } from "./calendarTime";

const HOUR_PX = 56;
const DEFAULT_START_HOUR = 7;
const DEFAULT_END_HOUR = 21;
const SNAP_MINUTES = 15;

interface Props {
  days: string[];
  appointments: Appointment[];
  onSlotClick: (date: string, minutes: number) => void;
  onAppointmentClick: (appointment: Appointment) => void;
}

export default function CalendarGrid({ days, appointments, onSlotClick, onAppointmentClick }: Props) {
  const scrollRef = useRef<HTMLDivElement>(null);
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
