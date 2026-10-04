import type { PortalSession } from "@anamnys/shared/lib/types";

// "qui., 9 de out. · 14:00–14:50 · Online", in the appointment's own time zone: that is
// the time the commitment was made in.
export function sessionWhen(session: PortalSession, locale: string, modalityLabel: string): string {
  const zone = session.timezone || "America/Sao_Paulo";
  const day = new Intl.DateTimeFormat(locale, { weekday: "short", day: "numeric", month: "short", timeZone: zone })
    .format(new Date(session.startsAt));
  const time = new Intl.DateTimeFormat(locale, { hour: "2-digit", minute: "2-digit", timeZone: zone });
  return `${day} · ${time.format(new Date(session.startsAt))}–${time.format(new Date(session.endsAt))} · ${modalityLabel}`;
}
