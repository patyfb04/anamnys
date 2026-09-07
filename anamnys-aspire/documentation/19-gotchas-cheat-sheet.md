# Chapter 19: Gotchas Cheat-Sheet

Every entry below was explained in depth somewhere earlier in this book. This chapter
exists purely so you don't have to remember which chapter — search this table by symptom,
then go read the referenced chapter for the full reasoning if you need it.

| If you're seeing... | It's because... | Full explanation |
|---|---|---|
| A clean checkout fails to build with routing errors | `src/routeTree.gen.ts` is generated but must stay committed — `tsc -b` runs before Vite regenerates it | Ch. 3, 11 |
| `MSB3491` or `CS2012` during a build | An Aspire run is still holding a file lock on `bin/`/`obj/` — run `aspire stop` first | Ch. 3 |
| `npm install` pulls a newer TypeScript and `npm run lint` breaks | TypeScript is pinned `~6.0.3`; no `typescript-eslint` release supports TS 7 yet | Ch. 11 |
| A `git clone` fails on Windows with an illegal-filename error | `apps/*/obj*` is gitignored on purpose — the esproj SDK emits a directory with a literal backslash in its name on macOS | Ch. 11 (see also `CLAUDE.md`) |
| `npm audit` flags something the `overrides` block was supposed to fix | An exact-version pin in the root `package.json`'s `overrides` block has itself gone stale | Ch. 11 |
| `dotnet test` says no tests were found, or errors oddly | This solution uses Microsoft.Testing.Platform; the real command is `dotnet run --project anamnys-aspire.Tests` | Ch. 16 |
| Keycloak fails to build with a missing-theme error | `keycloak/theme/keycloak-theme-anamnys.jar` doesn't exist yet — run the root `npm run build` (or the theme workspace's build) first | Ch. 7, 15 |
| You edited `keycloak/realms/*.json` and nothing changed | Realm import only runs when the realm doesn't already exist; `WithDataVolume()` means your edit is invisible until you wipe the Keycloak *and* Postgres volumes | Ch. 3, 7 |
| Client-authentication fails with an opaque Keycloak error after setting new secrets | You ran the AppHost before setting the four user secrets, so Keycloak imported with stale/missing values — wipe both data volumes | Ch. 3 |
| Login silently doesn't set a cookie, off `localhost` | `http://localhost` is a secure context to the browser; a container-network alias is not, so `Secure` cookies get dropped | Ch. 7 |
| An API endpoint returns 403 for a wrong-realm credential | It shouldn't — that's a sign a scheme has leaked into an endpoint group it doesn't belong in. Fix the group, never the test expecting 401 | Ch. 6, 8, 16 |
| A PHI endpoint 500s when hit with a valid-looking bearer token | The bearer schemes are registered but not mounted anywhere — `LocalId` is only minted on the cookie/OIDC path | Ch. 8, 16 |
| Login breaks with a redirect-URI mismatch after a restart | One of the four pinned dev SPA ports (5273–5276) or Keycloak's port (8080) changed, or someone "fixed" the asymmetric `changeOrigin` proxy settings | Ch. 4, 8, 11 |
| The server throws `InvalidOperationException` about `DataProtection:CertificateThumbprint` on startup | This is required outside Development — without it, DataProtection keys (which decrypt every stored session ticket) would sit in Redis unencrypted | Ch. 8, 17 |
| The server refuses to start outside Development citing `KEYCLOAK_ADMIN_*` | `DevOnlyTestClientGuard` needs these to verify dev-only Keycloak clients/users/origins are absent — a fail-closed check, not optional config | Ch. 4, 8, 17 |
| `anamnys-db-script.sql` fails to replay against a fresh Postgres | If it's ever regenerated from its original source tool, five known defects (a redundant `CREATE SCHEMA "public"`, duplicate indexes, malformed `gist` exclusions, a `TABLESPACE` on a view) will reappear — the committed file already has them fixed | Ch. 9 |
| `ExecuteSqlRawAsync` throws `FormatException` on schema-bootstrap SQL | That overload treats the SQL as a composite format string; the JSONB `{}` defaults in the script aren't valid format-string syntax. Use the raw ADO.NET connection instead, as `DatabaseInitializer` does | Ch. 9 |
| A provider's `CrpNumber`/`CrpRegion`/`BillingSystem` doesn't show up through `AnamnysDbContext` | The SQL schema has the columns; the EF Core entity hasn't mapped them yet. Check the raw schema, not just the C# model | Ch. 9, 10 |
| You're looking for a 2FA settings screen in any SPA | There isn't one — Keycloak owns TOTP/password reset/recovery codes entirely; link out to its account console instead | Ch. 6, 13 |
| `useAudioRecorder` or `lib/signalr.ts` seem unused | They are, as of this writing — built ahead of the SignalR hubs and UI that would call them. Don't assume live transcription works end to end because this code exists | Ch. 13 |
| You can't find any real functionality in `apps/patient` | It's explicitly a scaffold — its own code comment says so — waiting on an appointment API that doesn't exist yet | Ch. 14 |
| `aspire --version` doesn't match what you expected | Trust it over the Homebrew (or other package manager) install path — `AspireUseCliBundle` runs a bundle matching the AppHost SDK version regardless | `CLAUDE.md` |
| Docker isn't running, or `keycloakify build` fails looking for `mvn` | Both are hard prerequisites before any `aspire run` — Docker for the container-backed resources, Maven because Keycloakify shells out to it | Ch. 3, 15 |

## The one meta-gotcha behind most of the others

If you look across this table, a pattern repeats: a very large fraction of these traps
exist because something has to stay consistent across two or three files that don't look
related at first glance — a port number in `AppHost.cs`, a `vite.config.ts`, and a realm's
redirect URI; a build flag's logic split between `AppHost.cs` and `strip-dev-seed.jq`; a
data volume that silently outlives the config file that's supposed to control it. None of
these are bugs waiting to be fixed — they're the load-bearing consequence of a system where
identity, origin, and session all have to agree with each other everywhere, all the time
(Chapter 4 said this explicitly about the AppHost, and it turns out to be true of nearly
the whole codebase). When something behaves strangely and the fix isn't obvious from the
error message, the first question worth asking is: *what two files does this depend on
staying in sync, and did one of them change without the other?*

That closes the book. Between Chapters 1 and 19 you now have the product, the platform, the
identity system, the data model, the frontend, and the standing conventions — everything
this handbook set out to teach at the start.
