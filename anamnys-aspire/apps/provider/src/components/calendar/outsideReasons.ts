import type { TFunction } from "i18next";
import type { OutsideAvailabilityReason } from "@anamnys/shared/lib/types";
import { PRACTICE_ZONE, addDays, formatDayMonth, formatTime, zonedParts } from "./calendarTime";

// A block's bounds as the provider thinks of them: "de 10/01 a 20/01" when both ends are local midnights (the stored
// end is the midnight after the last day), "em 10/01" for one such day, else "de 10/01 14:00 a 10/01 16:00".
function blockPeriod(t: TFunction, from: string, to: string): string {
  const start = zonedParts(new Date(from), PRACTICE_ZONE);
  const end = zonedParts(new Date(to), PRACTICE_ZONE);
  if (start.minutes === 0 && end.minutes === 0) {
    const last = addDays(end.date, -1);
    return last <= start.date
      ? t("availability.reasons.onDay", { date: formatDayMonth(start.date) })
      : t("availability.reasons.fromTo", { from: formatDayMonth(start.date), to: formatDayMonth(last) });
  }
  return t("availability.reasons.fromTo", {
    from: `${formatDayMonth(start.date)} ${formatTime(new Date(from), PRACTICE_ZONE)}`,
    to: `${formatDayMonth(end.date)} ${formatTime(new Date(to), PRACTICE_ZONE)}`,
  });
}

// One Portuguese sentence per reason of a 409 outside_availability.
export function describeReason(t: TFunction, reason: OutsideAvailabilityReason): string {
  switch (reason.kind) {
    case "outside_hours":
      return t("availability.reasons.outsideHours");
    case "holiday":
      return t("availability.reasons.holiday", { name: reason.holidayName ?? "" });
    case "block": {
      const period = reason.from && reason.to ? blockPeriod(t, reason.from, reason.to) : "";
      return t(`availability.reasons.block.${reason.blockKind ?? "block"}`, { period }).trim();
    }
    default:
      return t("availability.reasons.outsideHours");
  }
}
