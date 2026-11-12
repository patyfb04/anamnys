import { useEffect, useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import type { Appointment, AppointmentStatus } from "@anamnys/shared/lib/types";
import { patientsApi } from "@anamnys/shared/api/patients";
import CalendarHeader from "@/components/calendar/CalendarHeader";
import CalendarGrid from "@/components/calendar/CalendarGrid";
import AppointmentFormModal, { type FormMode } from "@/components/calendar/AppointmentFormModal";
import AppointmentDetailsModal from "@/components/calendar/AppointmentDetailsModal";
import SeriesSplitModal from "@/components/calendar/SeriesSplitModal";
import MonthView from "@/components/calendar/MonthView";
import BlockPeriodModal from "@/components/calendar/BlockPeriodModal";
import UpcomingBlocksPanel from "@/components/calendar/UpcomingBlocksPanel";
import { PRACTICE_ZONE, addDays, addMonths, todayIn, visibleDays, type CalendarView } from "@/components/calendar/calendarTime";
import { isStatus } from "@/components/calendar/status";
import { useAppointmentsQuery } from "@/hooks/useAppointments";
import { hasHoursOn, useAvailabilityQuery, useCalendarOverlay } from "@/hooks/useAvailability";
import { useIsMobile } from "@/hooks/useIsMobile";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

// No PHI here: the view, a date, a status filter and an opaque patient id (as in /patients/$patientId).
interface CalendarSearch {
  view?: CalendarView;
  date?: string;
  status?: AppointmentStatus;
  patientId?: string;
}

export const Route = createFileRoute("/_app/calendar")({
  validateSearch: (search: Record<string, unknown>): CalendarSearch => ({
    view: search.view === "day" || search.view === "week" || search.view === "month" ? search.view : undefined,
    date: typeof search.date === "string" && /^\d{4}-\d{2}-\d{2}$/.test(search.date) ? search.date : undefined,
    status: isStatus(search.status) ? search.status : undefined,
    patientId: typeof search.patientId === "string" && UUID.test(search.patientId) ? search.patientId : undefined,
  }),
  component: CalendarPage,
});

// Provider calendar: week/day grid of appointments with the availability overlay, month view, blocks.
// See design/specs/2026-10-05-provider-calendar-design.md and 2026-11-09-availability-design.md §5.
function CalendarPage() {
  const { t } = useTranslation();
  const navigate = useNavigate({ from: Route.fullPath });
  const search = Route.useSearch();
  const isMobile = useIsMobile();
  const view: CalendarView = isMobile ? "day" : (search.view ?? "week");
  const date = search.date ?? todayIn(PRACTICE_ZONE);
  const days = visibleDays(view, date);
  const query = useAppointmentsQuery(days, search.status);
  const overlay = useCalendarOverlay(days);
  const availability = useAvailabilityQuery();
  const [form, setForm] = useState<FormMode | null>(null);
  const [selected, setSelected] = useState<Appointment | null>(null);
  const [blocking, setBlocking] = useState(false);
  const [splitting, setSplitting] = useState<Appointment | null>(null);
  // "Nova consulta" from the header: today at 09:00 when today is visible, else the first visible day (the 1st in the
  // month view, whose grid starts in the previous month).
  const defaultSlot = (): Extract<FormMode, { kind: "create" }> => {
    const today = todayIn(PRACTICE_ZONE);
    const first = view === "month" ? `${date.slice(0, 7)}-01` : days[0];
    return { kind: "create", date: days.includes(today) && today >= first ? today : first, minutes: 9 * 60 };
  };

  const setSearch = (next: Partial<CalendarSearch>) =>
    navigate({ search: (prev) => ({ ...prev, ...next }), replace: true });
  const move = (direction: 1 | -1) =>
    setSearch({ date: view === "month" ? addMonths(date, direction) : addDays(date, direction * (view === "day" ? 1 : 7)) });

  // "Agendar consulta" from a patient's page: open the form with that patient once it loads.
  const preselected = useQuery({
    queryKey: ["patient", search.patientId],
    queryFn: () => patientsApi.get(search.patientId!),
    enabled: !!search.patientId,
  });
  useEffect(() => {
    if (!search.patientId || preselected.isPending) return;
    const p = preselected.data;
    setForm({ ...defaultSlot(), patient: p ? { id: p.id, name: `${p.firstName} ${p.lastName}` } : undefined });
    // Only when the preselected patient resolves; the id leaves the URL when the form closes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search.patientId, preselected.isPending]);
  const closeForm = () => {
    setForm(null);
    if (search.patientId) setSearch({ patientId: undefined });
  };

  return (
    <div className="p-4 md:p-8">
      <CalendarHeader
        days={days}
        date={date}
        view={view}
        showViewToggle={!isMobile}
        status={search.status}
        onPrevious={() => move(-1)}
        onNext={() => move(1)}
        onToday={() => setSearch({ date: undefined })}
        onViewChange={(v) => setSearch({ view: v })}
        onStatusChange={(s) => setSearch({ status: s })}
        onNew={() => setForm(defaultSlot())}
        onBlock={() => setBlocking(true)}
      />
      {query.isError && <p className="mb-4 text-body-md text-error">{t("calendar.loadFailed")}</p>}
      {overlay.isError && <p className="mb-4 text-body-md text-error">{t("calendar.overlay.loadFailed")}</p>}
      {view === "month" ? (
        <MonthView
          days={days}
          month={date.slice(0, 7)}
          appointments={query.data ?? []}
          overlay={overlay.data}
          onDayClick={(d) => setSearch({ view: "day", date: d })}
        />
      ) : (
        <CalendarGrid
          days={days}
          appointments={query.data ?? []}
          overlay={overlay.data}
          hasHours={(d) => hasHoursOn(d, availability.data)}
          onSlotClick={(d, minutes) => setForm({ kind: "create", date: d, minutes })}
          onAppointmentClick={setSelected}
        />
      )}
      <UpcomingBlocksPanel />
      {blocking && <BlockPeriodModal onClose={() => setBlocking(false)} />}
      {form && <AppointmentFormModal mode={form} onClose={closeForm} />}
      {splitting && <SeriesSplitModal appointment={splitting} onClose={() => setSplitting(null)} />}
      {selected && (
        <AppointmentDetailsModal
          appointment={selected}
          onClose={() => setSelected(null)}
          onEdit={() => {
            setForm({ kind: "edit", appointment: selected });
            setSelected(null);
          }}
          onSplit={() => {
            setSplitting(selected);
            setSelected(null);
          }}
        />
      )}
    </div>
  );
}
