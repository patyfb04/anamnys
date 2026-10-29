import api from "@anamnys/shared/api/client";
import type { AttendanceSummary } from "@anamnys/shared/lib/types";

// Attendance statistics per patient (F-04). Only the patient id and dates travel in the URL.
export const attendanceApi = {
  summary: async (patientId: string, from?: string, to?: string): Promise<AttendanceSummary> => {
    const { data } = await api.get<AttendanceSummary>(`/phi/providers/me/patients/${patientId}/attendance`, { params: { from, to } });
    return data;
  },
};
