// Cross-application URLs.
//
// The three SPAs are served from sub-paths of the same origin, each with its own
// TanStack Router route tree. A router <Link> can only target routes inside its
// own app, so navigating between apps is a full document navigation via <a href>
// — deliberately, not as a workaround.
//
// These must stay in step with the `base` option in each app's vite.config.ts.
export const appUrls = {
  web: "/",
  provider: "/provider/",
  providerLogin: "/provider/login",
  providerRegister: "/provider/register",
  patient: "/patient/",
} as const;
