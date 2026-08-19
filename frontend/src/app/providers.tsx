"use client";

import { useEffect, useState } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { I18nextProvider } from "react-i18next";
import i18n from "@/lib/i18n";
import { useSettingsStore } from "@/lib/store/settingsStore";
import { useAuthStore } from "@/lib/store/authStore";

export default function Providers({ children }: { children: React.ReactNode }) {
  const [queryClient] = useState(() => new QueryClient());
  const loadLanguage = useSettingsStore((s) => s.loadLanguage);
  const loadUser = useAuthStore((s) => s.loadUser);

  useEffect(() => {
    loadLanguage();
    loadUser();
  }, [loadLanguage, loadUser]);

  return (
    <QueryClientProvider client={queryClient}>
      <I18nextProvider i18n={i18n}>{children}</I18nextProvider>
    </QueryClientProvider>
  );
}
