import { create } from "zustand";
import type { AuthUser } from "@anamnys/shared/lib/types";
import { authApi } from "@anamnys/shared/api/auth";

type Realm = "provider" | "patient" | "owner";

interface AuthState {
  user: AuthUser | null;
  isLoading: boolean;
  error: string | null;

  login: (realm: Realm, returnUrl?: string) => void;
  logout: (realm: Realm) => void;
  loadUser: () => Promise<void>;
  clearError: () => void;
}

export const useAuthStore = create<AuthState>((set) => ({
  user: null,
  // Starts true: the (app) layout's auth gate checks `!isLoading && !user` in a
  // useEffect that fires before the ancestor effect calling loadUser(), because
  // child effects run first on initial mount. Defaulting to false would bounce a
  // genuinely authenticated user to login for one frame on every page load.
  isLoading: true,
  error: null,

  // A full-page navigation, not a fetch. The BFF answers with a 302 to Keycloak
  // and the whole document has to follow it.
  login: (realm, returnUrl) => {
    const query = returnUrl ? `?returnUrl=${encodeURIComponent(returnUrl)}` : "";
    window.location.assign(`/auth/${realm}/login${query}`);
  },

  // Also a full-page navigation, not a fetch — see authApi.logout. Nothing is
  // set() afterwards on purpose: the document is on its way to Keycloak and
  // back to the app's landing path, so there is no state left to clear, and
  // clearing it would only make an XHR-shaped logout look like it worked.
  logout: (realm) => {
    authApi.logout(realm);
  },

  loadUser: async () => {
    set({ isLoading: true });
    try {
      const user = await authApi.me();
      set({ user, isLoading: false });
    } catch {
      set({ user: null, isLoading: false });
    }
  },

  clearError: () => set({ error: null }),
}));
