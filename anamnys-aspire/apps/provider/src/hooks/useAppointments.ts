import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { appointmentsApi } from "@anamnys/shared/api/appointments";
import type {
  AppointmentStatus,
  CreateAppointmentRequest,
  UpdateAppointmentRequest,
} from "@anamnys/shared/lib/types";
import { PRACTICE_ZONE, addDays, zonedToInstant } from "@/components/calendar/calendarTime";

const KEY = "appointments";

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
  const onSuccess = () => queryClient.invalidateQueries({ queryKey: [KEY] });
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
