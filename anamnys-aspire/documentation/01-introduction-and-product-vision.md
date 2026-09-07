# Chapter 1: Introduction & Product Vision

## What Anamnys is

Anamnys is a **specialty-native clinical note drafting system** built for solo and
small-group practices in two specialties: mental health and physical therapy. "Specialty-
native" is doing real work in that sentence — the product is not a generic transcription
tool with a medical skin on it. Every stage of its pipeline is shaped by what a mental
health or physical therapy note actually needs to contain, which billing codes apply to
that specialty, and which compliance rules govern that kind of care.

The core workflow looks like this:

1. **A provider dictates.** During or after a session, the provider records audio — either
   live, streamed in real time, or as a completed recording.
2. **Whisper transcribes it.** Speech-to-text runs locally, in-process, on the same server
   that hosts everything else. There is no third-party transcription API in this picture.
3. **A local LLM structures the transcript into a clinical note.** The raw transcript is
   turned into a structured note in one of the standard formats a clinical note takes —
   SOAP (Subjective, Objective, Assessment, Plan) or DAP (Data, Assessment, Plan) — again
   using an in-process model (LLaMA, via LLamaSharp), not a cloud API.
4. **A billing engine derives codes.** From the structured note, the system proposes CPT
   procedure codes and ICD-10 diagnosis codes, along with a **denial-risk score** — an
   estimate of how likely an insurer is to reject the claim as documented, so the provider
   can fix weak documentation before submitting rather than after a denial arrives.
5. **The provider reviews, signs, and exports.** Nothing produced by the pipeline is final
   until a human clinician has reviewed and signed it. Once signed, the note can be
   exported as a PDF.

If you take nothing else from this chapter, take this: **every one of those five steps
touches Protected Health Information (PHI)**, and that fact is the single biggest
influence on how this codebase is built. The next section explains why, and what it means
in practice.

## Who uses it

The system has three distinct kinds of users, and — as you'll see from Chapter 6 onward —
they are kept deliberately, structurally separate, not just separated by an `if` check in
application code:

- **Providers** — the clinicians who dictate, review, and sign notes. This is the primary
  user of `apps/provider`, the most feature-complete application in the repository today.
- **Patients** — who have their own portal (`apps/patient`) for a more limited set of
  interactions with their own record. This app is currently a routing skeleton; see
  Chapter 14.
- **Owners / staff** — practice owners and internal support/ops staff, who use
  `apps/admin` for administrative and operational tasks, distinct from clinical work. Also
  a skeleton today.

There's a fourth surface worth knowing about even though it isn't a "user" in the same
sense: `apps/web`, the public marketing site, which is by far the most built-out
front-end application in the repository right now (see Chapter 14) — a reminder that
"most complete" and "most important to the product" aren't always the same thing this
early in a build.

## The HIPAA constraints — read this as law, not commentary

The project's own `CLAUDE.md` file is unusually blunt about this, and it's worth repeating
here verbatim in spirit: these are **binding product requirements**, not descriptions of
code that already exists. As of this writing, the server is still close to the stock
Aspire template — most of the product described above (transcription, note structuring,
billing) has no server-side implementation yet. Do not read that absence as evidence that
these rules are optional. They are exactly as binding on a not-yet-written STT integration
as they would be on a shipped one, and every design decision from here on is made as if
that code already existed.

**All AI inference runs in-process, on the same server, forever.** Whisper.net for
speech-to-text and LLamaSharp for note structuring are .NET libraries loaded into the
server process — not sidecar services, not calls to a hosted model. This is the rule you
are most likely to be tempted to break, because routing a transcript to a cloud AI API
(OpenAI, Azure OpenAI, Gemini, Anthropic — any of them) is usually the easy path. Here, it
is never the path: sending PHI to a third party's API puts that data outside the network
boundary the practice controls, and doing so legally requires a signed Business Associate
Agreement (BAA) with that vendor. Anamnys's answer to that whole problem is to never let
PHI leave the boundary in the first place.

**Everything that touches PHI is self-hosted.** The same logic that rules out a cloud AI
API rules out any managed or hosted service for a PHI-bearing component: the database, the
identity provider, object storage. This is why Postgres and Keycloak run as
Aspire-managed Docker containers rather than, say, a hosted Postgres or a hosted identity
platform — see Chapter 4 for how the AppHost wires this up, and Chapter 7 for why Keycloak
in particular is self-hosted rather than something like Auth0 or Okta.

**Provider-scoping is the system's primary access control, and it's enforced at the query
level.** When a request arrives, the authenticated user's row id is resolved
*server-side*, from a session the server itself created — never from anything the client
supplied — and that id is what every provider-scoped database query filters on. A
provider cannot read another provider's patients or notes even with a completely valid
session, because the query itself never asks for anyone else's data. You'll meet the code
that makes this possible, `CurrentUser.LocalId`, in Chapter 8; the principle behind it —
that a local row id must come from the session, never from a claim the client could
theoretically forge or a route parameter it could tamper with — is one you should hold
onto for every future feature, not just the ones that exist today.

**Every note mutation is audited.** Editing a clinical note appends an `AuditEntry` to an
`AuditTrail` JSONB column, satisfying HIPAA's audit control requirement
(§164.312(b)). You'll see the `AuditEntries` table (among many others) in Chapter 10 — it
exists in the schema today even though the application code that would write to it
doesn't yet.

**Exported PDFs are treated as ePHI.** They live in a private object storage bucket and
are only ever reachable through short-lived (one-hour) presigned URLs — never a
permanent, guessable, or publicly reachable link.

**The application itself never stores a password.** Keycloak owns every credential —
hashing, password resets, account lockout — and there is, deliberately, no password
column anywhere in the application's own database. Chapters 6–8 cover exactly how this
works.

**No PHI ever appears in a token, a log line, or a URL.** Tokens carry only a subject id,
roles, and scopes. This constrains, for instance, how errors are logged in the
authentication pipeline (see the `OnRemoteFailure` handling in Chapter 8) and why the
open-redirect guard in `AuthEndpoints.cs` validates a return URL's *shape* rather than
logging its contents freely.

## What this means for you as a developer

Two habits will serve you well in this codebase specifically:

First, **when you're not sure whether something touches PHI, assume it does.** Patient
names, session audio, transcripts, clinical notes, diagnosis codes — obviously PHI. But so
is a provider's own contact information in some contexts, and so is metadata that could
indirectly identify a patient (an appointment time, a room assignment). If a feature you're
building might touch any of it, the self-hosted/in-process/provider-scoped rules above
apply to it.

Second, **read the comments.** This is not a typical instruction for a codebase, but
Anamnys's is unusually well-annotated at exactly the points where a shortcut would be
tempting and wrong — a cloud AI call that would "just work" in a demo, a query that
"would still be correct" without the provider-scope filter, a log statement that "would be
useful" with the exception message included. Later chapters, especially Part 4, will walk
through several of these comments verbatim because they explain *why* the code is
shaped the way it is better than any paraphrase could.

The next chapter takes you into the repository itself: what's actually on disk, how the
.NET solution and the JavaScript monorepo share one tree, and where each of the pieces
described above actually lives.
