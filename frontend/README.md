# clinical-draft-frontend

Next.js (App Router) web client for Clinical Draft, ported from the Expo/React Native
`clinical-draft-app`. Browser-only — no native/mobile target in this codebase.

## Setup

```bash
npm install
cp .env.example .env.local   # then fill in your backend's URLs
npm run dev
```

## What's built

- Full auth flow: Welcome (`/welcome`), Login (`/login`), Register (`/register`) against the
  real backend API (cookie-based session auth).
- Authenticated app shell (TopBar + bottom AppTabBar) with an auth gate that redirects to
  `/login` when signed out.
- Full route skeleton for Patients/Notes/Settings — each currently a placeholder page.
- Ported business logic: `src/lib/api.ts`, `src/lib/signalr.ts`, `src/lib/store/`,
  `src/lib/i18n/`, `src/lib/types.ts`, `src/lib/password.ts`, `src/hooks/useAudioRecorder.ts`.

### Verification status

The auth flow scaffolding is complete and type-checked, with client-side routing logic in place.
However, end-to-end verification of the full auth flow (register/login/logout round-trip with a
live backend) was **not performed** in the scaffolding environment and should be done by the user
against their actual backend before considering Tasks 9-11 (Welcome/Login/Register) fully verified
end-to-end. Routes return HTTP 200 with expected content, and TypeScript/linting/build all pass.

## Not built yet

Patients screens, Notes/NoteEditor, LiveTranscribe, Settings/AccountSettings, 2FA UI, billing
code display, biometric/push (dropped — no direct web equivalent in scope).

See `docs/superpowers/specs/2026-08-16-clinical-draft-frontend-design.md` in the parent repo
for the full design.
