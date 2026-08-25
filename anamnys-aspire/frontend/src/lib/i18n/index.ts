import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import { SUPPORTED_LANGUAGES } from "@/lib/types";

import pt from "./locales/pt.json";

export const resources = { pt: { translation: pt } };

const SUPPORTED_CODES = SUPPORTED_LANGUAGES.map((l) => l.code);
export const DEFAULT_LOCALE = "pt";

// Picks the best supported UI language from the browser's language preferences, falling back
// to Portuguese (the only shipped translation right now) when none of the browser languages
// match.
export function detectBrowserLocale(): string {
  if (typeof navigator === "undefined") return DEFAULT_LOCALE;
  const candidates = navigator.languages?.length ? navigator.languages : [navigator.language];
  for (const full of candidates) {
    const code = full.split("-")[0];
    if (SUPPORTED_CODES.includes(code)) return code;
  }
  return DEFAULT_LOCALE;
}

if (!i18n.isInitialized) {
  i18n.use(initReactI18next).init({
    resources,
    lng: DEFAULT_LOCALE, // server-safe default; Providers switches to the detected/saved locale on mount
    fallbackLng: DEFAULT_LOCALE,
    interpolation: { escapeValue: false },
    returnEmptyString: false,
  });
}

export default i18n;
