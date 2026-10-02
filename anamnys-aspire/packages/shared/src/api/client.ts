import axios, { type AxiosInstance, type AxiosError } from "axios";

// Unset in normal Aspire-hosted dev/prod — relative "/api" already resolves through
// vite.config.ts's same-origin proxy to the server resource (see also lib/signalr.ts).
const BASE_URL = import.meta.env.VITE_API_BASE_URL ?? "/api";

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

// Normalised API failure. `errors` carries ASP.NET's ValidationProblem field errors
// (camelCase keys, e.g. "medications[0].drug") so forms can show them next to the field.
export class ApiError extends Error {
  readonly status?: number;
  readonly errors?: Record<string, string[]>;

  constructor(message: string, status?: number, errors?: Record<string, string[]>) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.errors = errors;
  }
}

api.interceptors.response.use(
  (r) => r,
  (error: AxiosError) => {
    const data = error.response?.data as { message?: string; errors?: Record<string, string[]> } | undefined;
    const msg = data?.message ?? error.message ?? "An unexpected error occurred.";
    return Promise.reject(new ApiError(msg, error.response?.status, data?.errors));
  }
);

export default api;
