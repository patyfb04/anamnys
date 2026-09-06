import api from "@anamnys/shared/api/client";
import type { AuthUser } from "@anamnys/shared/lib/types";

// Login is not here on purpose. It is a full-page navigation to the BFF, which
// answers with a 302 to Keycloak — an XHR cannot follow that usefully.
export const authApi = {
  logout: async (realm: "provider" | "patient" | "owner"): Promise<void> => {
    // /auth/{realm}/logout is not under /api, unlike everything else this client calls.
    await api.post(`/auth/${realm}/logout`, undefined, { baseURL: "/" }).catch(() => undefined);
  },
  me: async (): Promise<AuthUser> => {
    const { data } = await api.get<AuthUser>("/auth/me");
    return data;
  },
};
