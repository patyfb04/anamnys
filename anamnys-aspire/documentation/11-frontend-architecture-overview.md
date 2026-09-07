# Chapter 11: Frontend Architecture Overview

Part 6 covers the four SPAs and the code they share. This chapter lays out the shape every
one of them follows before Chapters 12–15 go app by app.

## Same-origin, by sub-path, on purpose

You've already met this idea twice — in Chapter 4 (the AppHost's `PublishWithContainerFiles`
calls) and Chapter 6 (why BFF cookie auth needs same-origin to avoid CORS entirely). Here it
is from the frontend's own point of view. Each app's `vite.config.ts` sets its `base` to
match exactly where the server will serve it from in production:

```ts
// apps/web/vite.config.ts
base: '/',

// apps/provider/vite.config.ts
base: '/provider/',
```

(`apps/patient` and `apps/admin` follow the same pattern, at `/patient/` and `/admin/`.)
This single setting is what has to agree with the sub-path Chapter 4's
`server.PublishWithContainerFiles(provider, "wwwroot/provider")` call bakes the app into —
get them out of sync and the built app's asset URLs point at the wrong path in production,
even though everything looks fine in dev.

The `main.tsx` entry point in every app reads this same value back at runtime rather than
hardcoding it a second time:

```tsx
// apps/admin/src/main.tsx
const router = createRouter({ routeTree, basepath: import.meta.env.BASE_URL });
```

`import.meta.env.BASE_URL` mirrors whatever `base` was set to, so the TanStack Router
instance knows its own mount point without a second place to keep in sync.

## The dev-time proxy, and why it's asymmetric

Reading `apps/provider/vite.config.ts` directly makes Chapter 4's gotcha about asymmetric
`changeOrigin` settings concrete:

```ts
server: {
  port: 5273,
  strictPort: true,
  proxy: {
    '/api':  { target: process.env.SERVER_HTTP, changeOrigin: true, ws: true },
    '/hubs': { target: process.env.SERVER_HTTP, changeOrigin: true, ws: true },
    '/auth': { target: process.env.SERVER_HTTP, ws: false },
    '/signin-oidc-provider': { target: process.env.SERVER_HTTP },
  },
},
```

`changeOrigin: true` on `/api` and `/hubs` makes the proxied request look, from the
server's point of view, like it came from the container-network origin the server actually
runs on. `/auth` and `/signin-oidc-provider` deliberately omit it, because the server needs
to see the request's `Host` header as the browser's *actual* origin
(`localhost:5273`) in order to build a correct OIDC `redirect_uri` and land session cookies
against the origin the browser is really using — the exact mechanic Chapter 8 covers from
the server side. Get this backwards on either group and login breaks in a way that looks
like a Keycloak misconfiguration but isn't. `apps/web`'s config is a good contrast: it has
no `/auth` or `/signin-oidc-*` entries at all, because the marketing site never
authenticates directly — a code comment there notes it only calls `/api/auth/me` to reflect
session state, and that any owners-realm dev flow lives on `apps/admin` instead, the app
that actually logs into that realm.

`process.env.SERVER_HTTP` is supplied by Aspire — this is `AddViteApp`'s
`WithReference(server)` from Chapter 4 landing as an environment variable the Vite config
can read directly.

## The stack

- **Vite 8 + React 19 + TypeScript**, with TypeScript deliberately held at `~6.0.3` rather
  than the newer 7.x line. This isn't inertia — no released or canary version of
  `typescript-eslint` supports TypeScript 7 yet (its widest peer range tops out below 6.1),
  so upgrading TypeScript would break `npm run lint` across every workspace. Two version
  pins work together to keep this stable: `typescript` itself is patch-pinned
  (`~6.0.3`, so a plain `npm install` can't silently pull in 6.1+), and
  `typescript-eslint` is floored at `>=8.68.0`, the first release whose peer range admits
  TypeScript 6 at all. Revisit both together, not separately, whenever
  `typescript-eslint` ships TypeScript 7 support.
- **TanStack Router**, file-based, via the `@tanstack/router-plugin/vite` plugin — this is
  what generates each app's `src/routeTree.gen.ts` from the `src/routes/` directory
  structure. That generated file **must stay committed to git**: `npm run build` runs
  `tsc -b` *before* Vite ever regenerates it, so a clean checkout without it fails to
  build. If one ever goes missing or gets out of sync, a bare `npx vite build` inside that
  app's directory regenerates it.
- **TanStack Query** for server state — you'll see `useQuery` throughout the provider app
  in Chapter 13, always pointed at a function from `packages/shared/src/api/*` (Chapter
  12), never at a raw `fetch` call inline in a component.
- **Tailwind CSS 4**, via `@tailwindcss/vite`.
- **Zustand** for genuinely client-side state — UI state and, notably, *session* state
  (`useAuthStore`, covered in Chapter 12) — but never tokens. The store holds a user object
  and loading/error flags; it has no field that could hold a credential, by design.
- **i18next / react-i18next**, with locale resources living in `packages/shared` so every
  app draws from the same translation set.
- **`@microsoft/signalr`**, used today only in `apps/provider` — see Chapter 13 for an
  important caveat about how far that integration currently reaches.

## npm workspaces, revisited

Chapter 2 introduced the workspace topology; here's the piece specifically relevant to
day-to-day frontend work. The root `package.json`'s `overrides` block —

```json
"overrides": {
  "postcss": "8.5.23",
  "esbuild": "0.28.1",
  "js-yaml": "4.3.1",
  "nanoid": "3.3.18",
  "brace-expansion": "2.1.4",
  "minimatch@3.1.5": { "brace-expansion": "2.1.4" }
}
```

— is a security control, not tidiness. It pins transitive dependencies away from known
advisories, for every workspace at once, from one place. The trap worth knowing about:
pinning to an *exact* version means the pin itself will eventually go stale and start
holding the tree at a version that's since become the vulnerable one. If you ever touch
this block, run `npm audit` afterward, and bump the pinned versions forward rather than
deleting them outright — the mechanism needs to stay, even as the specific versions age.

With the shared shape established, Chapter 12 goes into the one package every app in this
list actually imports from: `packages/shared`.
