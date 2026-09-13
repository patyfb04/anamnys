# Chapter 18: Conventions & Anti-Patterns

This chapter is the shortest in the book, deliberately — it's a reference for habits you
should already be forming naturally if you've read the preceding seventeen chapters, made
explicit so there's one place to check when in doubt.

## Workflow

- **Branch naming:** `feature/`, `bugfix/`, `hotfix/` prefixes.
- **Commit format:** `type: description` — `feat`, `fix`, `refactor`, `test`, `docs`,
  `chore`.
- **Always create a branch before making changes** — never commit directly to `develop`.
- **Run tests before committing.** Given Chapter 16, that means `dotnet run --project
  anamnys-aspire.Tests`, not `dotnet test`, for anything touching the server or auth — and
  the relevant `npm run lint`/`npm run build` for anything touching a frontend workspace.

## Creating or modifying an API endpoint: the expected sequence

This is worth internalizing as a checklist, not just a description, because it's the
concrete process every PHI-touching feature in this codebase is meant to follow:

1. **Plan the endpoint changes first** — the new or updated methods, their paths, and
   their request/response payloads — before writing implementation code.
2. **Confirm the proposed shape with the user** (or whoever owns the feature) before
   building it out.
3. **Implement the endpoint**, following Chapter 5's pattern: hang it off the `phi` or
   `admin` route group rather than defining a fresh authorization policy, and resolve any
   provider id via `principal.LocalId()` (Chapter 8), never from a route parameter or
   request body field (Chapter 9's provider-scoping discipline).
4. **Document it in `anamnys-aspire.Server/anamnys-aspire.Server.http`** — the endpoint
   itself and its payloads. This file is the executable reference for the API; an endpoint
   that isn't in it is, for practical purposes, undocumented.
5. **Run the requests in that `.http` file against a live AppHost** to verify the endpoint
   actually behaves as documented, before considering it done.
6. **Choose the endpoint group deliberately.** `/api/phi/*` is for anything touching
   patient data; `/api/admin/*` is for owners-realm surfaces. This choice determines which
   realms can authenticate at all (Chapter 6) — get it wrong and you've either locked out a
   realm that needs access or, worse, opened a PHI endpoint to the owners realm.

## Code style

**General:** prefer clear code over clever code; use inline comments sparingly. If you've
read Part 4 closely, you've already seen the exception to "sparingly" in practice — this
codebase reaches for a comment specifically at the point where a shortcut would be tempting
and wrong, not as routine narration of what the code already says.

**C#:**
- 4-space indentation.
- `PascalCase` for classes and methods; `_camelCase` for private fields; `camelCase` for
  local variables and parameters.
- Primary constructors, including for dependency injection — you saw this throughout
  Chapter 8 (`FirstLoginProvisioner(AnamnysDbContext db)`, `DatabaseInitializer(
  IServiceScopeFactory scopeFactory, ILogger<DatabaseInitializer> logger)`, and others).
- File-scoped namespaces.
- **Always pass `CancellationToken` to async methods** — every async method in Parts 3–5
  follows this without exception; treat a new one that doesn't as a review comment waiting
  to happen.
- Use records for complex incoming request parameters.
- Use auto-properties, and the `field` keyword where needed, rather than a manually
  backed property.
- **No XML documentation comments.** This codebase relies on descriptive names and, where
  genuinely needed, an inline comment explaining *why* — not `<summary>` blocks restating
  *what* a method's signature already says.

**Tests:** `<ClassName>Tests` for the test class name; `<MethodName>_<Conditions>_
<AssertedOutcome>` for test method names, deliberately **never** with an `Async` suffix
even on an `async` test method; Arrange/Act/Assert, each section explicitly commented.
Chapter 16 showed you several real examples of this shape.

**TypeScript / JavaScript / CSS:** 2-space indentation (matching the existing code, not a
Prettier default the frontend explicitly overrides). Keep a `*.test.ts` file in the same
directory as the `.ts` file it tests, rather than in a parallel `__tests__` tree.

## Patterns this codebase deliberately does not use

Four things, and it's worth understanding *why* each is excluded rather than treating the
list as arbitrary taste:

- **The repository pattern.** EF Core's `DbContext` and `DbSet<T>` already are a
  repository/unit-of-work abstraction; wrapping them in a second, hand-rolled layer adds
  indirection without adding capability. Query directly against `AnamnysDbContext`.
- **AutoMapper.** Mappings are written explicitly. For a codebase this attentive to exactly
  which fields cross a trust boundary (Chapter 1's "no PHI in tokens, logs, or URLs," the
  entity-vs-column gaps flagged in Chapter 9), an explicit mapping is also a deliberate
  checkpoint — a reviewer can see precisely what's being copied from one shape to another,
  which a convention-based auto-mapper would obscure.
- **Exceptions for business logic errors.** This is worth reading carefully rather than
  applying too broadly: `FirstLoginProvisioner` (Chapter 8) *does* throw
  `InvalidOperationException` for things like an uninvited patient or a disabled account.
  That's not a contradiction — those are authentication/authorization failures being
  surfaced through `OnRemoteFailure`'s exception handling, a genuinely exceptional and rare
  path, not routine business logic like "this form field failed validation." The
  distinction to hold onto: an exception here signals something has gone wrong at a
  security or integrity boundary, not that a user made an ordinary, expected mistake.
- **Stored procedures.** All data access logic lives in application code (C#, via EF Core)
  or, for the one-time schema bootstrap (Chapter 9), a plain SQL script — never inside the
  database as a stored procedure.

## The one discipline with no compiler behind it

Chapter 9 already made this point once, and it's worth repeating here as the single
highest-value thing to internalize from this whole chapter: **provider-scoping is a
discipline this codebase asks of every developer, not something a framework enforces for
you.** There is no query filter, no interceptor, no attribute that automatically prevents
a PHI query from leaking across providers — it's just `principal.LocalId()`, used
correctly, every time. When you review a pull request touching anything in
`/api/phi/*`, this is the first thing worth checking, before formatting or style.

Chapter 19 closes the book with a single searchable reference for every trap like this one
scattered across the previous eighteen chapters.
