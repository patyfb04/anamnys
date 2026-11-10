import { useEffect, useRef, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Clock, Plus, X } from "lucide-react";
import { availabilityApi } from "@anamnys/shared/api/availability";
import { ApiError } from "@anamnys/shared/api/client";
import type { AvailabilityDay, AvailabilityInterval, AvailabilityResponse, AvailabilityVersion } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import DetailCard from "@/components/patients/detail/DetailCard";
import { inputClass } from "@/components/patients/FormField";
import { AVAILABILITY_KEY, OVERLAY_KEY, useAvailabilityQuery } from "@/hooks/useAvailability";
import { PRACTICE_ZONE, formatDate, minutesToTime, timeToMinutes, todayIn } from "../calendar/calendarTime";
import HolidaySettingsPanel from "./HolidaySettingsPanel";
import Switch from "./Switch";

// Monday first; weekday numbers are the server's (0 = Sunday).
const WEEK = [1, 2, 3, 4, 5, 6, 0];
const WORKDAYS = [1, 2, 3, 4, 5];
const MAX_INTERVALS = 6;
const LAST_END = "23:55";

type Grid = Record<number, AvailabilityInterval[]>;

function toGrid(version: AvailabilityVersion | null | undefined): Grid {
  const grid: Grid = {};
  for (const w of WEEK) grid[w] = version?.days.find((d) => d.weekday === w)?.intervals.map((i) => ({ ...i })) ?? [];
  return grid;
}

// With no saved hours, a suggestion: Monday to Friday 08:00–12:00 and 13:00–18:00. Nothing is saved until "Salvar".
function suggestedGrid(): Grid {
  const grid: Grid = {};
  for (const w of WEEK) grid[w] = WORKDAYS.includes(w) ? [{ start: "08:00", end: "12:00" }, { start: "13:00", end: "18:00" }] : [];
  return grid;
}

function initialGrid(data: AvailabilityResponse): Grid {
  return data.current ? toGrid(data.current) : data.upcoming.length ? toGrid(data.upcoming[data.upcoming.length - 1]) : suggestedGrid();
}

// Mirrors the server's rule (start < end, no overlap, 5-minute steps, at most 6 per day) so the provider sees the
// problem before saving; the server stays the authority.
function dayProblem(intervals: AvailabilityInterval[]): "order" | "overlap" | "step" | "count" | null {
  if (intervals.length > MAX_INTERVALS) return "count";
  const sorted = [...intervals].sort((a, b) => a.start.localeCompare(b.start));
  for (let i = 0; i < sorted.length; i++) {
    const start = timeToMinutes(sorted[i].start);
    const end = timeToMinutes(sorted[i].end);
    if (Number.isNaN(start) || Number.isNaN(end) || start % 5 !== 0 || end % 5 !== 0) return "step";
    if (start >= end) return "order";
    if (i > 0 && start < timeToMinutes(sorted[i - 1].end)) return "overlap";
  }
  return null;
}

function nextInterval(intervals: AvailabilityInterval[]): AvailabilityInterval {
  if (intervals.length === 0) return { start: "08:00", end: "12:00" };
  const lastEnd = Math.max(...intervals.map((i) => timeToMinutes(i.end)));
  const start = Math.min(lastEnd + 60, timeToMinutes(LAST_END) - 60);
  return { start: minutesToTime(start), end: minutesToTime(Math.min(start + 240, timeToMinutes(LAST_END))) };
}

// "Horários de atendimento" (spec §5): the weekly hours from a date, the upcoming change with "Cancelar mudança", and
// the national-holiday preferences (saved on their own).
export default function AvailabilitySection() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { data, isError } = useAvailabilityQuery();
  const today = todayIn(PRACTICE_ZONE);
  const [grid, setGrid] = useState<Grid>(() => suggestedGrid());
  const [effectiveFrom, setEffectiveFrom] = useState(today);
  const [error, setError] = useState<string | null>(null);
  const loaded = useRef(false);

  useEffect(() => {
    // Once: after a save the grid keeps what the provider just entered.
    if (data && !loaded.current) {
      loaded.current = true;
      setGrid(initialGrid(data));
    }
  }, [data]);

  const refresh = (next?: AvailabilityResponse) => {
    if (next) queryClient.setQueryData([AVAILABILITY_KEY], next);
    else queryClient.invalidateQueries({ queryKey: [AVAILABILITY_KEY] });
    queryClient.invalidateQueries({ queryKey: [OVERLAY_KEY] });
  };

  const save = useMutation({
    mutationFn: () => {
      const days: AvailabilityDay[] = WEEK.map((weekday) => ({
        weekday,
        intervals: [...grid[weekday]].sort((a, b) => a.start.localeCompare(b.start)),
      }));
      return availabilityApi.save(effectiveFrom, days);
    },
    onSuccess: (next) => {
      setError(null);
      refresh(next);
      toast.success(t("availability.saved"));
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 400) setError(err.message);
      else toast.error(t("availability.saveFailed"));
    },
  });

  const cancelUpcoming = useMutation({
    mutationFn: (date: string) => availabilityApi.cancelUpcoming(date),
    onSuccess: () => {
      refresh();
      toast.success(t("availability.upcomingCancelled"));
    },
    onError: () => toast.error(t("availability.upcomingCancelFailed")),
  });

  const setDay = (weekday: number, intervals: AvailabilityInterval[]) => setGrid((g) => ({ ...g, [weekday]: intervals }));
  const copyToWorkdays = (weekday: number) =>
    setGrid((g) => {
      const next = { ...g };
      for (const w of WORKDAYS) next[w] = g[weekday].map((i) => ({ ...i }));
      return next;
    });

  const problems = WEEK.map((w) => [w, dayProblem(grid[w])] as const).filter(([, p]) => p);
  const replaced = (data?.upcoming ?? []).filter((v) => v.effectiveFrom >= effectiveFrom);

  const summary = (version: AvailabilityVersion) =>
    WEEK.map((w) => {
      const intervals = version.days.find((d) => d.weekday === w)?.intervals ?? [];
      const hours = intervals.length ? intervals.map((i) => `${i.start}–${i.end}`).join(", ") : t("availability.closed");
      return `${t(`availability.weekdaysShort.${w}`)} ${hours}`;
    }).join(" · ");

  return (
    <DetailCard title={t("availability.title")} icon={<Clock size={20} />}>
      <div className="flex flex-col gap-5">
        <p className="text-body-md text-onSurfaceVariant">{t("availability.description")}</p>
        {isError && <p className="text-body-md text-error">{t("availability.loadFailed")}</p>}
        {data && !data.current && data.upcoming.length === 0 && (
          <p className="rounded-radii-md bg-pastelLavender px-3 py-2 text-body-md text-onPastelLavender">{t("availability.noneYet")}</p>
        )}
        {data?.current && (
          <p className="text-body-sm text-onSurfaceVariant">
            {t("availability.currentSince", { date: formatDate(data.current.effectiveFrom) })}
          </p>
        )}
        {data?.upcoming.map((v) => (
          <div key={v.effectiveFrom} className="flex flex-col gap-2 rounded-radii-md border border-outlineVariant bg-surfaceContainerLow px-3 py-2 md:flex-row md:items-center md:justify-between">
            <div className="flex min-w-0 flex-col">
              <span className="text-label-lg text-onSurface">{t("availability.upcoming", { date: formatDate(v.effectiveFrom) })}</span>
              <span className="text-body-sm text-onSurfaceVariant">{summary(v)}</span>
            </div>
            <button
              type="button"
              disabled={cancelUpcoming.isPending}
              onClick={() => cancelUpcoming.mutate(v.effectiveFrom)}
              className="self-start rounded-radii-md border border-outlineVariant px-3 py-1.5 text-label-lg text-onSurface hover:bg-pastelLavender disabled:opacity-50 md:self-center"
            >
              {t("availability.cancelUpcoming")}
            </button>
          </div>
        ))}

        <form
          className="flex flex-col gap-4"
          onSubmit={(e) => {
            e.preventDefault();
            if (problems.length > 0) return;
            save.mutate();
          }}
        >
          <ul className="flex flex-col divide-y divide-outlineVariant rounded-radii-md border border-outlineVariant">
            {WEEK.map((weekday) => {
              const intervals = grid[weekday];
              const open = intervals.length > 0;
              const problem = dayProblem(intervals);
              return (
                <li key={weekday} className="flex flex-col gap-2 px-3 py-3 md:flex-row md:items-start">
                  <div className="flex items-center gap-3 md:w-48 md:pt-1.5">
                    <Switch
                      checked={open}
                      onChange={(next) => setDay(weekday, next ? [nextInterval([])] : [])}
                      label={t("availability.worksOn", { day: t(`availability.weekdays.${weekday}`) })}
                    />
                    <span className="text-label-lg text-onSurface">{t(`availability.weekdays.${weekday}`)}</span>
                  </div>
                  <div className="flex flex-1 flex-col gap-2">
                    {!open && <span className="text-body-md text-onSurfaceVariant md:pt-1.5">{t("availability.closed")}</span>}
                    {intervals.map((interval, i) => (
                      <div key={i} className="flex flex-wrap items-center gap-2">
                        <input
                          type="time"
                          step={300}
                          required
                          aria-label={t("availability.start")}
                          className={`${inputClass} w-32`}
                          value={interval.start}
                          onChange={(e) => setDay(weekday, intervals.map((x, j) => (j === i ? { ...x, start: e.target.value } : x)))}
                        />
                        <span className="text-onSurfaceVariant">–</span>
                        <input
                          type="time"
                          step={300}
                          required
                          max={LAST_END}
                          aria-label={t("availability.end")}
                          className={`${inputClass} w-32`}
                          value={interval.end}
                          onChange={(e) => setDay(weekday, intervals.map((x, j) => (j === i ? { ...x, end: e.target.value } : x)))}
                        />
                        <button
                          type="button"
                          onClick={() => setDay(weekday, intervals.filter((_, j) => j !== i))}
                          aria-label={t("availability.removeInterval")}
                          title={t("availability.removeInterval")}
                          className="rounded-radii-full p-1.5 text-onSurfaceVariant hover:bg-pastelLavender"
                        >
                          <X size={16} />
                        </button>
                      </div>
                    ))}
                    {problem && <span className="text-label-md text-error">{t(`availability.problems.${problem}`)}</span>}
                    <div className="flex flex-wrap gap-4">
                      {open && intervals.length < MAX_INTERVALS && (
                        <button
                          type="button"
                          onClick={() => setDay(weekday, [...intervals, nextInterval(intervals)])}
                          className="flex items-center gap-1 text-label-lg text-primary hover:underline"
                        >
                          <Plus size={14} />
                          {t("availability.addInterval")}
                        </button>
                      )}
                      <button type="button" onClick={() => copyToWorkdays(weekday)} className="text-label-lg text-primary hover:underline">
                        {t("availability.copyToWorkdays")}
                      </button>
                    </div>
                  </div>
                </li>
              );
            })}
          </ul>
          <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
            {t("availability.effectiveFrom")}
            <input
              type="date"
              required
              min={today}
              className={`${inputClass} w-48`}
              value={effectiveFrom}
              onChange={(e) => setEffectiveFrom(e.target.value)}
            />
          </label>
          {replaced.length > 0 && (
            <p className="text-body-sm text-onSurfaceVariant">
              {t("availability.replacesUpcoming", { dates: replaced.map((v) => formatDate(v.effectiveFrom)).join(", ") })}
            </p>
          )}
          {error && <p role="alert" className="text-label-md text-error">{error}</p>}
          <button
            type="submit"
            disabled={save.isPending || problems.length > 0}
            className="self-start rounded-radii-md bg-primary px-4 py-2 text-label-lg text-onPrimary disabled:opacity-50"
          >
            {t("availability.save")}
          </button>
        </form>

        <HolidaySettingsPanel />
      </div>
    </DetailCard>
  );
}
