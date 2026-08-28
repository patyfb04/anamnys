import api from "@anamnys/shared/api/client";
import type { TranscribeJobResponse } from "@anamnys/shared/lib/types";

export const transcribeApi = {
  submitAudio: async (
    patientId: string,
    audioBlob: Blob,
    mimeType: string = "audio/webm"
  ): Promise<TranscribeJobResponse> => {
    const form = new FormData();
    form.append("patientId", patientId);
    form.append("audio", audioBlob, `recording.${mimeType.includes("webm") ? "webm" : "m4a"}`);

    const { data } = await api.post<TranscribeJobResponse>("/transcribe", form, {
      headers: { "Content-Type": "multipart/form-data" },
    });
    return data;
  },

  submitText: async (patientId: string, text: string): Promise<TranscribeJobResponse> => {
    const { data } = await api.post<TranscribeJobResponse>("/transcribe/text", { patientId, text });
    return data;
  },

  getJobStatus: async (jobId: string): Promise<TranscribeJobResponse> => {
    const { data } = await api.get<TranscribeJobResponse>(`/transcribe/jobs/${jobId}`);
    return data;
  },
};
