import { useEffect, useState } from "react";
import { Outlet, createRootRoute } from "@tanstack/react-router";
import { TanStackRouterDevtools } from "@tanstack/react-router-devtools";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { I18nextProvider } from "react-i18next";
import i18n from "@anamnys/shared/lib/i18n";
import { useSettingsStore } from "@anamnys/shared/lib/store/settingsStore";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";

// Root document shell. index.html owns html/body; this is the provider tree.
function RootLayout() {
  const [queryClient] = useState(() => new QueryClient());
  const loadLanguage = useSettingsStore((s) => s.loadLanguage);
  const loadUser = useAuthStore((s) => s.loadUser);

  useEffect(() => {
    loadLanguage();
    loadUser();
  }, [loadLanguage, loadUser]);

  return (
    <QueryClientProvider client={queryClient}>
      <I18nextProvider i18n={i18n}>
        <Outlet />
        <TanStackRouterDevtools position="bottom-right" />
      </I18nextProvider>
    </QueryClientProvider>
  );
}

export const Route = createRootRoute({ component: RootLayout });
