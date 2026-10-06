import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import type { AppointmentStatus } from "@anamnys/shared/lib/types";
import CalendarHeader from "@/components/calendar/CalendarHeader";
import CalendarGrid from "@/components/calendar/CalendarGrid";
import { PRACTICE_ZONE, addDays, todayIn, visibleDays, type CalendarView } from "@/components/calendar/calendarTime";
import { isStatus } from "@/components/calendar/status";
import { useAppointmentsQuery } from "@/hooks/useAppointments";
import { useIsMobile } from "@/hooks/useIsMobile";

// No PHI here: only the view, a date and a status filter.
interface CalendarSearch {
  view?: CalendarView;
  date?: string;
  status?: AppointmentStatus;
}

export const Route = createFileRoute("/_app/calendar")({
  validateSearch: (search: Record<string, unknown>): CalendarSearch => ({
    view: search.view === "day" || search.view === "week" ? search.view : undefined,
    date: typeof search.date === "string" && /^\d{4}-\d{2}-\d{2}$/.test(search.date) ? search.date : undefined,
    status: isStatus(search.status) ? search.status : undefined,
  }),
  component: CalendarPage,
});

// Provider calendar: week/day grid of appointments.
// See design/specs/2026-10-05-provider-calendar-design.md.
function CalendarPage() {
  const { t } = useTranslation();
  const navigate = useNavigate({ from: Route.fullPath });
  const search = Route.useSearch();
  const isMobile = useIsMobile();
  const view: CalendarView = isMobile ? "day" : (search.view ?? "week");
  const date = search.date ?? todayIn(PRACTICE_ZONE);
  const days = visibleDays(view, date);
  const query = useAppointmentsQuery(days, search.status);

  const setSearch = (next: Partial<CalendarSearch>) =>
    navigate({ search: (prev) => ({ ...prev, ...next }), replace: true });
  const step = view === "day" ? 1 : 7;

  return (
    <div className="p-4 md:p-8">
      <CalendarHeader
        days={days}
        view={view}
        showViewToggle={!isMobile}
        status={search.status}
        onPrevious={() => setSearch({ date: addDays(date, -step) })}
        onNext={() => setSearch({ date: addDays(date, step) })}
        onToday={() => setSearch({ date: undefined })}
        onViewChange={(v) => setSearch({ view: v })}
        onStatusChange={(s) => setSearch({ status: s })}
        onNew={() => {}}
      />
      {query.isError && <p className="mb-4 text-body-md text-error">{t("calendar.loadFailed")}</p>}
      <CalendarGrid
        days={days}
        appointments={query.data ?? []}
        onSlotClick={() => {}}
        onAppointmentClick={() => {}}
      />
    </div>
  );
}
