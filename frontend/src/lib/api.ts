import axios, { AxiosInstance, AxiosError } from "axios";
import {
  Patient,
  CreatePatientRequest,
  Note,
  TranscribeJobResponse,
  LoginRequest,
  RegisterRequest,
  LoginResponse,
  AuthUser,
  PaginatedResponse,
  TwoFactorStatus,
  TwoFactorSetup,
  TwoFactorVerifyResult,
} from "@/lib/types";

const BASE_URL = process.env.NEXT_PUBLIC_API_BASE_URL ?? "http://localhost:5000/api";

const api: AxiosInstance = axios.create({
  baseURL: BASE_URL,
  timeout: 30_000,
  headers: { "Content-Type": "application/json" },
  // The API sets an HttpOnly `auth_token` cookie on login/register; this makes the browser
  // actually send it (and accept the Set-Cookie response) on cross-origin requests. There is
  // no JS-readable token anywhere in this app — that's the point: an XSS'd page can't
  // exfiltrate it.
  withCredentials: true,
});

api.interceptors.response.use(
  (r) => r,
  (error: AxiosError) => {
    const msg =
      (error.response?.data as { message?: string } | undefined)?.message ?? error.message ?? "An unexpected error occurred.";
    return Promise.reject(new Error(msg));
  }
);

export const authApi = {
  login: async (req: LoginRequest): Promise<LoginResponse> => {
    const { data } = await api.post<LoginResponse>("/auth/login", req);
    return data;
  },
  register: async (req: RegisterRequest): Promise<LoginResponse> => {
    const { data } = await api.post<LoginResponse>("/auth/register", req);
    return data;
  },
  completeTwoFactorLogin: async (challengeToken: string, code: string): Promise<LoginResponse> => {
    const { data } = await api.post<LoginResponse>("/auth/login/2fa", { challengeToken, code });
    return data;
  },
  logout: async (): Promise<void> => {
    await api.post("/auth/logout").catch(() => undefined);
  },
  me: async (): Promise<AuthUser> => {
    const { data } = await api.get<AuthUser>("/auth/me");
    return data;
  },
};

export const accountApi = {
  changePassword: async (currentPassword: string, newPassword: string): Promise<void> => {
    await api.post("/account/change-password", { currentPassword, newPassword });
  },
  twoFactorStatus: async (): Promise<TwoFactorStatus> => {
    const { data } = await api.get<TwoFactorStatus>("/account/2fa/status");
    return data;
  },
  twoFactorSetup: async (): Promise<TwoFactorSetup> => {
    const { data } = await api.post<TwoFactorSetup>("/account/2fa/setup");
    return data;
  },
  twoFactorVerify: async (code: string): Promise<TwoFactorVerifyResult> => {
    const { data } = await api.post<TwoFactorVerifyResult>("/account/2fa/verify", { code });
    return data;
  },
  twoFactorDisable: async (password: string, code: string): Promise<void> => {
    await api.post("/account/2fa/disable", { password, code });
  },
};

export const patientsApi = {
  list: async (page = 1, pageSize = 20): Promise<PaginatedResponse<Patient>> => {
    const { data } = await api.get<PaginatedResponse<Patient>>("/patients", {
      params: { page, pageSize },
    });
    return data;
  },
  get: async (id: string): Promise<Patient> => {
    const { data } = await api.get<Patient>(`/patients/${id}`);
    return data;
  },
  create: async (patient: CreatePatientRequest): Promise<Patient> => {
    const { data } = await api.post<Patient>("/patients", patient);
    return data;
  },
};

export const notesApi = {
  list: async (patientId: string): Promise<Note[]> => {
    const { data } = await api.get<Note[]>(`/notes`, { params: { patientId } });
    return data;
  },
  get: async (noteId: string): Promise<Note> => {
    const { data } = await api.get<Note>(`/notes/${noteId}`);
    return data;
  },
  update: async (noteId: string, patch: Partial<Note>): Promise<Note> => {
    const { data } = await api.patch<Note>(`/notes/${noteId}`, patch);
    return data;
  },
  sign: async (noteId: string): Promise<Note> => {
    const { data } = await api.post<Note>(`/notes/${noteId}/sign`);
    return data;
  },
  export: async (noteId: string): Promise<{ url: string }> => {
    const { data } = await api.post<{ url: string }>(`/notes/${noteId}/export`);
    return data;
  },
};

// Public "Contact / Support" form (unauthenticated — no cookie/session needed, unlike every
// other API here). Backed by POST /api/contact on ClinicalDraft.Api's ContactController.
export const contactApi = {
  submit: async (fullName: string, email: string, phone: string | undefined, message: string): Promise<void> => {
    await api.post("/contact", { fullName, email, phone: phone || null, message });
  },
};

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

export default api;
