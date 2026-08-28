/// <reference types="vite/client" />

interface ImportMetaEnv {
  // Optional overrides — unset in normal Aspire-hosted dev/prod, where the API and SignalR
  // hubs are reached through vite.config.ts's same-origin '/api' and '/hubs' proxies instead.
  readonly VITE_API_BASE_URL?: string;
  readonly VITE_SIGNALR_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
