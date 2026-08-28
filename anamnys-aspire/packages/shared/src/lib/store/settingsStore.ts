import { create } from "zustand";
import i18n, { DEFAULT_LOCALE, detectBrowserLocale } from "@anamnys/shared/lib/i18n";

const LANGUAGE_STORAGE_KEY = "app_language";

interface SettingsState {
  language: string;
  isLoaded: boolean;

  loadLanguage: () => void;
  setLanguage: (code: string) => void;
}

// Persisted UI language preference, separate from the per-patient transcription language.
// Defaults to the browser locale until the user picks one. Uses localStorage directly (no
// HttpOnly-cookie concern here — this is a UI preference, not a credential).
export const useSettingsStore = create<SettingsState>((set) => ({
  language: DEFAULT_LOCALE,
  isLoaded: false,

  loadLanguage: () => {
    const saved = typeof window !== "undefined" ? window.localStorage.getItem(LANGUAGE_STORAGE_KEY) : null;
    const language = saved ?? detectBrowserLocale();
    i18n.changeLanguage(language);
    set({ language, isLoaded: true });
  },

  setLanguage: (code: string) => {
    i18n.changeLanguage(code);
    if (typeof window !== "undefined") window.localStorage.setItem(LANGUAGE_STORAGE_KEY, code);
    set({ language: code });
  },
}));
