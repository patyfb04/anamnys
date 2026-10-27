// Pure helpers for the timeline chart: dates are yyyy-MM-dd strings handled as UTC days.
export const DAY_MS = 86_400_000;

export type TimelinePeriod = "6m" | "1y" | "all";
export const PERIODS: TimelinePeriod[] = ["6m", "1y", "all"];

export function dayMs(iso: string): number {
  const [y, m, d] = iso.slice(0, 10).split("-").map(Number);
  return Date.UTC(y, m - 1, d);
}

export function toIso(ms: number): string {
  return new Date(ms).toISOString().slice(0, 10);
}

/** Today in the practice time zone, as yyyy-MM-dd. */
export function todayIso(): string {
  return new Intl.DateTimeFormat("en-CA", { year: "numeric", month: "2-digit", day: "2-digit", timeZone: "America/Sao_Paulo" }).format(new Date());
}

/** Start date for the period (dates only go to the server), or undefined for "all". */
export function periodFrom(period: TimelinePeriod, today: string): string | undefined {
  if (period === "all") return undefined;
  const d = new Date(dayMs(today));
  d.setUTCMonth(d.getUTCMonth() - (period === "6m" ? 6 : 12));
  return toIso(d.getTime());
}

export function formatDay(iso: string, locale: string): string {
  return new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeZone: "UTC" }).format(new Date(dayMs(iso) + DAY_MS / 2));
}

export interface Tick {
  ms: number;
  label: string;
}

/** Month ticks across the domain; coarser steps when the domain is long so labels never crowd. */
export function monthTicks(startMs: number, endMs: number, locale: string): Tick[] {
  const months = (endMs - startMs) / (DAY_MS * 30.4);
  const step = months > 48 ? 12 : months > 24 ? 6 : months > 12 ? 3 : 1;
  const fmt = new Intl.DateTimeFormat(locale, { month: "short", year: "2-digit", timeZone: "UTC" });
  const first = new Date(startMs);
  let y = first.getUTCFullYear();
  let m = first.getUTCMonth();
  if (Date.UTC(y, m, 1) < startMs) m += 1;
  if (step > 1) m = Math.ceil(m / step) * step;
  const ticks: Tick[] = [];
  for (let guard = 0; guard < 400; guard++) {
    const ms = Date.UTC(y, m, 1);
    if (ms > endMs) break;
    ticks.push({ ms, label: fmt.format(new Date(ms + DAY_MS / 2)).replace(".", "") });
    m += step;
    if (m >= 12) {
      y += Math.floor(m / 12);
      m %= 12;
    }
  }
  return ticks;
}
