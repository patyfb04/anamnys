import { create } from "zustand";
import type { AuthUser, Specialty } from "@/lib/types";
import { authApi } from "@/api/auth";

interface AuthState {
  user: AuthUser | null;
  isLoading: boolean;
  isSubmitting: boolean;
  error: string | null;
  pendingTwoFactor: { challengeToken: string } | null;

  login: (email: string, password: string) => Promise<void>;
  register: (email: string, password: string, name: string, specialty: Specialty) => Promise<void>;
  completeTwoFactorLogin: (code: string) => Promise<void>;
  cancelTwoFactor: () => void;
  logout: () => Promise<void>;
  loadUser: () => Promise<void>;
  clearError: () => void;
}

export const useAuthStore = create<AuthState>((set, get) => ({
  user: null,
  // Starts true (not false, unlike the source app's native-navigator version) — the (app)
  // layout's client-side auth gate (Task 12) checks `!isLoading && !user` in a useEffect that
  // fires before Providers' loadUser()-calling effect (child effects fire before ancestor
  // effects on initial mount). Defaulting to false would make that gate redirect a genuinely
  // authenticated user to /login for one frame on every fresh page load, before the session
  // check even starts.
  isLoading: true,
  isSubmitting: false,
  error: null,
  pendingTwoFactor: null,

  login: async (email, password) => {
    set({ isSubmitting: true, error: null });
    try {
      const res = await authApi.login({ email, password });
      if (res.requiresTwoFactor && res.challengeToken) {
        set({ pendingTwoFactor: { challengeToken: res.challengeToken }, isSubmitting: false });
      } else {
        set({ user: res.user ?? null, isSubmitting: false });
      }
    } catch (e) {
      const message = e instanceof Error ? e.message : "Login failed.";
      set({ error: message, isSubmitting: false });
    }
  },

  register: async (email, password, name, specialty) => {
    set({ isSubmitting: true, error: null });
    try {
      const res = await authApi.register({ email, password, name, specialty });
      if (res.requiresTwoFactor && res.challengeToken) {
        set({ pendingTwoFactor: { challengeToken: res.challengeToken }, isSubmitting: false });
      } else {
        set({ user: res.user ?? null, isSubmitting: false });
      }
    } catch (e) {
      const message = e instanceof Error ? e.message : "Registration failed.";
      set({ error: message, isSubmitting: false });
    }
  },

  completeTwoFactorLogin: async (code) => {
    const challenge = get().pendingTwoFactor;
    if (!challenge) return;
    set({ isSubmitting: true, error: null });
    try {
      const res = await authApi.completeTwoFactorLogin(challenge.challengeToken, code);
      set({ user: res.user ?? null, pendingTwoFactor: null, isSubmitting: false });
    } catch (e) {
      const message = e instanceof Error ? e.message : "Invalid code.";
      set({ error: message, isSubmitting: false });
    }
  },

  cancelTwoFactor: () => set({ pendingTwoFactor: null, error: null }),

  logout: async () => {
    await authApi.logout();
    set({ user: null, error: null, pendingTwoFactor: null });
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
