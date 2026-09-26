// Cross-application URLs.
//
// The three SPAs are served from sub-paths of the same origin, each with its own
// TanStack Router route tree. A router <Link> can only target routes inside its
// own app, so navigating between apps is a full document navigation via <a href>
// — deliberately, not as a workaround.
//
// These must stay in step with the `base` option in each app's vite.config.ts.
//
// The *Register and *Login entries are reached from apps/web's own /get-started and
// /sign-in pages, so they're not really "cross-app links" in the sense above — they're
// the BFF's /auth endpoints. In production
// that's fine as a relative path (one shared origin). In dev, each app is its own Vite
// dev server on its own fixed port, and only the provider app's dev server actually
// proxies /auth to the real server with the Host header Keycloak's registered redirect
// URI expects — a relative "/auth/provider/register" clicked from web's dev origin
// (5275) just 404s there. Absolute only in dev, so it reaches provider's dev proxy.
const providerDevOrigin = "http://localhost:5273";
const patientDevOrigin = "http://localhost:5274";

export const appUrls = {
  web: "/",
  provider: "/provider/",
  signIn: "/sign-in",
  providerLogin: import.meta.env.DEV
    ? `${providerDevOrigin}/auth/provider/login`
    : "/auth/provider/login",
  providerRegister: import.meta.env.DEV
    ? `${providerDevOrigin}/auth/provider/register`
    : "/auth/provider/register",
  getStarted: "/get-started",
  patient: "/patient/",
  patientLogin: import.meta.env.DEV
    ? `${patientDevOrigin}/auth/patient/login`
    : "/auth/patient/login",
  patientRegister: import.meta.env.DEV
    ? `${patientDevOrigin}/auth/patient/register`
    : "/auth/patient/register",
} as const;
