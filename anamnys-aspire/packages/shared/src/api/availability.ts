import api from "@anamnys/shared/api/client";
import type {
  AffectedAppointment,
  AvailabilityDay,
  AvailabilityResponse,
  BlockItem,
  BlockRequest,
  CalendarOverlay,
  HolidaySettings,
} from "@anamnys/shared/lib/types";

// design/specs/2026-11-09-availability-design.md §4. Instants go out with "Z" (toISOString), dates as "yyyy-MM-dd".
const BASE = "/phi/providers/me";

export const availabilityApi = {
  get: async (): Promise<AvailabilityResponse> => {
    const { data } = await api.get<AvailabilityResponse>(`${BASE}/availability`);
    return data;
  },
  save: async (effectiveFrom: string, days: AvailabilityDay[]): Promise<AvailabilityResponse> => {
    const { data } = await api.put<AvailabilityResponse>(`${BASE}/availability`, { effectiveFrom, days });
    return data;
  },
  cancelUpcoming: async (effectiveFrom: string): Promise<void> => {
    await api.delete(`${BASE}/availability/upcoming/${effectiveFrom}`);
  },
  getHolidays: async (): Promise<HolidaySettings> => {
    const { data } = await api.get<HolidaySettings>(`${BASE}/holiday-settings`);
    return data;
  },
  saveHolidays: async (worksOnHolidays: boolean, disabledHolidays: string[]): Promise<HolidaySettings> => {
    const { data } = await api.put<HolidaySettings>(`${BASE}/holiday-settings`, { worksOnHolidays, disabledHolidays });
    return data;
  },
  overlay: async (from: string, to: string): Promise<CalendarOverlay> => {
    const { data } = await api.get<CalendarOverlay>(`${BASE}/calendar-overlay`, { params: { from, to } });
    return data;
  },
};

export const blocksApi = {
  list: async (from: Date, to: Date): Promise<BlockItem[]> => {
    const { data } = await api.get<BlockItem[]>(`${BASE}/blocks`, {
      params: { from: from.toISOString(), to: to.toISOString() },
    });
    return data;
  },
  preview: async (request: BlockRequest): Promise<AffectedAppointment[]> => {
    const { data } = await api.post<AffectedAppointment[]>(`${BASE}/blocks/preview`, request);
    return data;
  },
  // A 409 `changed` carries `appointments` (the new preview list) in ApiError.body.
  create: async (request: BlockRequest): Promise<{ id: string; cancelledAppointmentIds: string[] }> => {
    const { data } = await api.post<{ id: string; cancelledAppointmentIds: string[] }>(`${BASE}/blocks`, request);
    return data;
  },
  remove: async (id: string): Promise<void> => {
    await api.delete(`${BASE}/blocks/${id}`);
  },
};
