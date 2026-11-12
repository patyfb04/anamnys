import type { TFunction } from "i18next";
import { ApiError } from "@anamnys/shared/api/client";
import type { SkippedOccurrence } from "@anamnys/shared/lib/types";
import { PRACTICE_ZONE, formatDayMonth, zonedParts } from "./calendarTime";

// Helpers for recurring series (design/specs/2026-11-09-appointment-series-design.md §5).

export type Repeat = "none" | "weekly" | "biweekly";
export type SeriesEnd = "never" | "onDate" | "afterCount";

// 0 = Sunday … 6 = Saturday for a "YYYY-MM-DD" date, as the server counts weekdays.
export function weekdayOf(date: string): number {
  const [y, m, d] = date.split("-").map(Number);
  return new Date(Date.UTC(y, m - 1, d)).getUTCDay();
}

// "toda terça às 14:00" / "terça sim, terça não, às 14:00", like the server's description.
export function patternText(t: TFunction, interval: 1 | 2, weekday: number, time: string): string {
  return interval === 1
    ? t("series.patternWeekly", { every: t(`series.every.${weekday}`), time })
    : t("series.patternBiweekly", { weekday: t(`series.weekday.${weekday}`), time });
}

// "24/11, 01/12": the practice-local days of the occurrences that were not created.
export function skippedDates(skipped: SkippedOccurrence[]): string {
  return skipped.map((s) => formatDayMonth(zonedParts(new Date(s.startsAt), PRACTICE_ZONE).date)).join(", ");
}

const CODES = ["invalid_interval", "invalid_end", "invalid_occurrence", "series_ended", "invalid_date", "start_in_past", "overlap"];

// A series endpoint's coded error in Portuguese, or the server's own message, or the fallback.
export function seriesErrorMessage(t: TFunction, err: unknown, fallback: string): string {
  if (err instanceof ApiError) {
    if (err.code && CODES.includes(err.code)) return t(`series.errors.${err.code}`);
    if ((err.status === 409 || err.status === 422) && err.message) return err.message;
  }
  return fallback;
}
