// Native fetch, not the axios client in ./client.ts: that client's interceptor turns
// every error into a bare Error(message), and this form needs the 400 ValidationProblem
// body to show per-field messages. credentials: "include" is what sends the BFF's
// HttpOnly session cookie.
const BASE_URL = import.meta.env.VITE_API_BASE_URL ?? "/api";

export type AccountRealm = "provider" | "patient";

export type ProviderProfile = {
  email: string;
  name: string;
  crpNumber: string | null;
  crpRegion: string | null;
};
export type ProviderProfileUpdate = Omit<ProviderProfile, "email">;

export type PatientProfile = {
  email: string | null;
  firstName: string;
  lastName: string;
  phone: string | null;
  dateOfBirth: string | null;
};
export type PatientProfileUpdate = Omit<PatientProfile, "email">;

export class ProfileValidationError extends Error {
  readonly errors: Record<string, string[]>;

  constructor(errors: Record<string, string[]>) {
    super("Validation failed.");
    this.errors = errors;
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${BASE_URL}${path}`, {
    ...init,
    credentials: "include",
    headers: { "Content-Type": "application/json", ...init?.headers },
  });

  if (response.status === 400) {
    const body = (await response.json()) as { errors?: Record<string, string[]> };
    throw new ProfileValidationError(body.errors ?? {});
  }
  if (!response.ok) {
    throw new Error(`Request failed with status ${response.status}.`);
  }
  return (response.status === 204 ? undefined : await response.json()) as T;
}

export const profileApi = {
  getProvider: () => request<ProviderProfile>("/phi/providers/me/profile"),
  updateProvider: (body: ProviderProfileUpdate) =>
    request<void>("/phi/providers/me/profile", { method: "PUT", body: JSON.stringify(body) }),
  getPatient: () => request<PatientProfile>("/phi/patients/me/profile"),
  updatePatient: (body: PatientProfileUpdate) =>
    request<void>("/phi/patients/me/profile", { method: "PUT", body: JSON.stringify(body) }),
};

// A full-page navigation target, not a fetch: the BFF answers with a 302 to Keycloak,
// which runs the action and comes back to the current page.
export function accountActionUrl(
  realm: AccountRealm,
  action: "UPDATE_PASSWORD" | "CONFIGURE_TOTP" | "UPDATE_EMAIL"
): string {
  return `/auth/${realm}/action/${action}?returnUrl=${encodeURIComponent(window.location.pathname)}`;
}
