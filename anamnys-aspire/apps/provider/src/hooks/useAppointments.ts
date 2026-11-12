import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { appointmentsApi } from "@anamnys/shared/api/appointments";
import { appointmentSeriesApi } from "@anamnys/shared/api/appointmentSeries";
import type {
  AppointmentStatus,
  CreateAppointmentRequest,
  CreateSeriesRequest,
  SplitSeriesRequest,
  UpdateAppointmentRequest,
} from "@anamnys/shared/lib/types";
import { PRACTICE_ZONE, addDays, zonedToInstant } from "@/components/calendar/calendarTime";

const KEY = "appointments";
const SERIES_KEY = "appointment-series";

export function useAppointmentsQuery(days: string[], status?: AppointmentStatus) {
  const from = zonedToInstant(days[0], 0, PRACTICE_ZONE);
  const to = zonedToInstant(addDays(days[days.length - 1], 1), 0, PRACTICE_ZONE);
  return useQuery({
    queryKey: [KEY, from.toISOString(), to.toISOString(), status ?? "all"],
    queryFn: () => appointmentsApi.list(from, to, status ? [status] : []),
    placeholderData: keepPreviousData,
  });
}

export function useAppointmentMutations() {
  const queryClient = useQueryClient();
  // The patient id is not known here, so the patient-record attendance and timeline are invalidated by prefix.
  const onSuccess = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: [KEY] }),
      // A single change to an occurrence shows in its series' upcoming list.
      queryClient.invalidateQueries({ queryKey: [SERIES_KEY] }),
      queryClient.invalidateQueries({ queryKey: ["attendance"] }),
      queryClient.invalidateQueries({ queryKey: ["timeline"] }),
    ]);
  return {
    create: useMutation({ mutationFn: (r: CreateAppointmentRequest) => appointmentsApi.create(r), onSuccess }),
    update: useMutation({
      mutationFn: ({ id, request }: { id: string; request: UpdateAppointmentRequest }) => appointmentsApi.update(id, request),
      onSuccess,
    }),
    setStatus: useMutation({
      mutationFn: ({ id, status, reason }: { id: string; status: AppointmentStatus; reason?: string }) =>
        appointmentsApi.setStatus(id, status, reason),
      onSuccess,
    }),
  };
}

// Recurring series (design/specs/2026-11-09-appointment-series-design.md §5).

export function useSeriesQuery(seriesId: string | null | undefined) {
  return useQuery({
    queryKey: [SERIES_KEY, "detail", seriesId],
    queryFn: () => appointmentSeriesApi.get(seriesId!),
    enabled: !!seriesId,
  });
}

export function usePatientSeriesQuery(patientId: string) {
  return useQuery({
    queryKey: [SERIES_KEY, "patient", patientId],
    queryFn: () => appointmentSeriesApi.listForPatient(patientId),
  });
}

export function useSeriesMutations() {
  const queryClient = useQueryClient();
  // Any series change creates or cancels appointments, so everything that shows them is refreshed.
  const onSuccess = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: [KEY] }),
      queryClient.invalidateQueries({ queryKey: [SERIES_KEY] }),
      queryClient.invalidateQueries({ queryKey: ["attendance"] }),
      queryClient.invalidateQueries({ queryKey: ["timeline"] }),
      queryClient.invalidateQueries({ queryKey: ["patient"] }),
    ]);
  return {
    create: useMutation({ mutationFn: (r: CreateSeriesRequest) => appointmentSeriesApi.create(r), onSuccess }),
    // A preview writes nothing.
    splitPreview: useMutation({
      mutationFn: ({ id, request }: { id: string; request: SplitSeriesRequest }) => appointmentSeriesApi.splitPreview(id, request),
    }),
    split: useMutation({
      mutationFn: ({ id, request }: { id: string; request: SplitSeriesRequest }) => appointmentSeriesApi.split(id, request),
      onSuccess,
    }),
    end: useMutation({
      mutationFn: ({ id, fromDate }: { id: string; fromDate: string }) => appointmentSeriesApi.end(id, fromDate),
      onSuccess,
    }),
  };
}
