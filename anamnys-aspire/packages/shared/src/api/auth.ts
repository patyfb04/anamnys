import api from "@anamnys/shared/api/client";
import type { AuthUser } from "@anamnys/shared/lib/types";

type Realm = "provider" | "patient" | "owner";

// Login is not here on purpose. It is a full-page navigation to the BFF, which
// answers with a 302 to Keycloak — an XHR cannot follow that usefully.
export const authApi = {
  // Logout is a full-page navigation for the same reason, and it has to stay
  // one. The BFF's sign-out ends in a 302 to Keycloak's end_session_endpoint;
  // that response is cross-origin and carries no CORS headers, so an XHR fails
  // on it. The failure is invisible — the __Host- cookie has already been
  // cleared, so the UI looks signed out — while the Keycloak SSO session
  // survives and silently re-authenticates the next person to click "Log in"
  // on this browser.
  logout: (realm: Realm): void => {
    // A submitted form rather than window.location.assign: /auth/{realm}/logout
    // is a POST, and it is not under /api, unlike everything else this client
    // calls.
    const form = document.createElement("form");
    form.method = "POST";
    form.action = `/auth/${realm}/logout`;
    document.body.appendChild(form);
    form.submit();
  },
  me: async (): Promise<AuthUser> => {
    const { data } = await api.get<AuthUser>("/auth/me");
    return data;
  },
};
