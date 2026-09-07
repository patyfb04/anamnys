# Chapter 10: The Full Data Model

Chapter 9 covered the five tables wired into EF Core today. This chapter tours the other
63 defined in `anamnys-db-script.sql`. None of what follows is speculation — every table
and column name below comes directly from the committed schema — but almost none of it has
application code behind it yet. Read this chapter as a map of where the product is
*going*, grouped by the workflow each cluster of tables serves, not as a description of
what currently runs. A few tables along the way make the product's actual market
unmistakable in a way `CLAUDE.md`'s prose doesn't spell out directly — watch for the
Brazil-specific detail as you go.

## Patients, providers, and professional identity

Beyond the `Providers` and `PatientAccounts` tables from Chapter 9: `Patients` (the actual
clinical patient record — `ProviderId`, name, date of birth, preferred language, distinct
from `PatientAccounts`, which is specifically the *portal login* for a patient, per Chapter
8's binding logic), `ProviderProfiles` (a provider's public-facing profile — slug, display
name, bio, photo, languages, modalities, city/state — doing double duty as both a profile
*and* the provider marketplace listing, covered further below), and `SpecialistTitles`
(professional credentials with a `RegistryRef` and `EvidenceObjectKey` — a verifiable
credential, not a self-reported title).

`Providers.CrpNumber` and `CrpRegion`, mentioned in Chapter 9, register a psychologist with
Brazil's Conselho Regional de Psicologia. Keep that in mind as a throughline for the rest of
this chapter.

## Scheduling

A full appointment-booking subsystem: `AvailabilityRules` (a provider's recurring weekly
availability) and `AvailabilityExceptions` (one-off overrides — time off, an added slot);
`BookingPolicies` (lead-time and cancellation-window rules per provider) and `BookingHolds`
(a short-lived reservation on a slot while a patient completes booking, with its own
`ExpiresAt`); `AppointmentSeries` (a recurring appointment definition — `RecurrenceRule`,
default duration/modality) and `Appointments` themselves, which can either belong to a
series or stand alone, and carry a price snapshot (`PriceCentsSnapshot`) independent of
whatever the provider's current pricing is, so a historical appointment's price doesn't
retroactively change; `CalendarConnections` (external calendar sync — Google/Outlook-style
tokens and a sync channel); and `Reminders`/`Notifications` for scheduled and delivered
messages.

## The clinical note pipeline

This is the schema-level realization of Chapter 1's five-step pipeline. `Sessions` is the
actual clinical encounter (distinct from `Appointments`, which is the *booking* for it —
a session can exist without ever having been formally booked). `Transcripts` holds the raw
Whisper output per session, with its own `RetentionUntil` and `DestroyedAt` — audio and
transcript retention is treated as a first-class, independently-tracked lifecycle, not an
afterthought. `SpeechMarkers` captures signal extracted directly from audio (a `Kind` and
`Value`, with a `PatientBaseline` for comparison) — the kind of paralinguistic detail
(pace, affect) that a text transcript alone wouldn't preserve. `Notes` is the structured
SOAP/DAP note itself (`Status`, `InputMode`, `Format`, the linked `RawTranscript`,
`SignedAt`), broken into individually-editable `NoteSections` (each with its own `Key`,
`Content`, `GeneratedAt` vs. `EditedAt` — so the system can tell which parts of a note are
still exactly as the LLM produced them and which a clinician has since revised).
`ClinicalDocuments` is a more general exportable/signable document tied to a note, with its
own `RetentionUntil` and a `SupersededBy` self-reference for versioning. `AuditEntries` —
named directly in Chapter 1 as the HIPAA §164.312(b) control — records every field-level
change (`FieldChanged`, `PreviousValue`, `NewValue`) against a note or document.
`FollowUpItems` tracks action items surfaced from a note, with their own resolution
tracking.

## Extraction and theming: what the NLP layer produces

A cluster of tables that only makes sense once you know an LLM is reading every transcript:
`ExtractedFacts` (a structured fact pulled from an uploaded `ExternalDocument` — e.g. a
referral letter — with both the original `Value` and a clinician's `CorrectedValue`, plus
a `PageRef` and `ExcerptRefId` tying it back to its exact source location).
`MedicationEntries` (drug, dose, and posology, each traceable back to the `ExtractedFacts`
row it came from via `SourceFactId`). `ExcerptRefs` is a general-purpose pointer into a
source document or transcript (`SourceType`, `SourceId`, character offsets) that several
other tables reference when they need to cite exactly where a piece of derived data came
from — `ExtractedFacts`, `PatientQuotes`, and `ThemeTags` all point through it.
`PatientQuotes` captures a verbatim quote tied to a note and a `ThemeTerms` entry.
`ThemeVocabularies`, `ThemeTerms`, and `ThemeTags` together form a versioned taxonomy
system: a `ThemeVocabularies` row is a published, versioned set of `ThemeTerms` (which can
supersede each other over time via `SupersededByTermId`), and `ThemeTags` is where a
specific note gets tagged against a specific term, with a `Confidence` score and an
`ExcerptRefId` justifying the tag. `FocusAreas` (with a `ProviderFocusAreas` join table) is
a separate, simpler taxonomy — the clinical focus areas a provider lists themselves as
practicing (anxiety, trauma, sports rehab, and so on), used for the marketplace listing
below, not for note-level tagging.

## Assessment instruments

`Instruments` models a standardized clinical assessment scale — its item count, score
range, and, notably, a `SatepsiStatus` and `SatepsiCheckedOn` column. SATEPSI is the
Sistema de Avaliação de Testes Psicológicos, the Brazilian Federal Psychology Council's
official registry of validated psychological tests — a Brazilian psychologist is generally
expected to use only SATEPSI-approved instruments in professional practice, so tracking
this status per instrument is a real regulatory requirement, not incidental metadata. A
`LicenseMode` and `MayRenderItems` flag suggest some instruments are licensed such that
their actual item text can't be reproduced in the app, only referenced. `ScaleApplications`
records one administration of an instrument to a patient (raw answers, computed score,
who administered it). `ProviderInstrumentOptIns` tracks a provider's acknowledgment before
using a given instrument — plausibly tied to licensing or liability requirements.

## Intake

A straightforward, provider-configurable intake form system: `IntakeTemplates` (versioned,
one active per provider at a time via `Active`), `IntakeQuestions` (each with an
`AnswerType`, ordering `Position`, and a `TargetField` — suggesting answers can map
directly onto structured patient fields rather than staying free text), and
`IntakeResponses` (one row per question per patient submission).

## Billing and insurance — the Brazilian health insurance system specifically

This cluster confirms the market beyond any doubt: `TissGuides` generates a **TISS**
guide (Troca de Informação em Saúde Suplementar — the mandatory standardized data-exchange
format Brazilian health insurers require for reimbursement claims), linked to an
`Insurers` row that carries an `AnsRegistry` field — a registration number with the ANS
(Agência Nacional de Saúde Suplementar), Brazil's national supplementary health insurance
regulator. `InsurerAuthorizations` tracks a specific pre-authorization from an insurer,
including how many sessions were authorized versus performed — directly gating whether a
session can be billed at all. `BillingCodes` is where a note's derived procedure/diagnosis
codes live (`ProcedureCode`, `DiagnosisCode`, a `Confidence` score, and — matching Chapter
1's product description exactly — a `DenialRisk` score computed per code, plus
`ConfirmedBy`/`ConfirmedAt` for the clinician's sign-off before submission).
`Denials`/`DenialFlags` close the loop: `Denials` records an actual rejection from an
insurer (`ReasonCode`, `ReasonText`), and `DenialFlags` appears to be a *pre-emptive*
warning system, raised against a `TissGuides` row before submission based on a
`RuleKey` — the mechanism that would make the "fix weak documentation before it's denied"
promise from Chapter 1 real.

## Treatment plans

Don't confuse these with the SaaS subscription `Plans` covered below — entirely different
concept, unfortunately similar name. `TreatmentPlans` is the clinical treatment plan for a
patient (`CreatedAt`, `ReviewedAt`, `ClosedAt` — a lifecycle, not a single document).
`PlanObjectives` are individual goals within it, each optionally tied to a `ThemeTerms`
entry (`TermId`) and the most recent session where progress against it was recorded
(`LastRecordedSessionId`).

## Compliance, consent, and retention

The largest and most HIPAA/LGPD-specific cluster in the schema, and worth reading in full
if you're ever asked to build a feature that touches any of it.

- **Consent, specifically for recording**: `RecordingConsents` — a full lifecycle
  (`State`, `TemplateVersion`, `IssuedAt`, `SignedAt`, `SignatureEvidence`, `ValidUntil`,
  `RevokedAt`), with `GuardianName` and `AssentRecordedAt` columns suggesting explicit
  support for minor patients, where a guardian consents and the minor separately assents.
  `ConsentEvents` is a generic state-transition audit log (`FromState`, `ToState`, `Reason`)
  that a `ConsentId` — presumably including `RecordingConsents` — can be tied to.
- **Disclosure**: `DisclosureAuthorizations` — an authorization for releasing a specific
  document to a specific named recipient, for a stated purpose, recorded by a specific
  staff member. This is HIPAA's accounting-of-disclosures requirement made concrete.
- **Retention and disposal**: `RetentionRules` (per provider, per `RecordKind`, a
  `GuardYears` figure with a stated legal `Basis` — retention periods clearly vary by
  record type and jurisdiction, not a single blanket policy), `DisposalRecords` (proof a
  specific document was actually destroyed, including the object-storage key of whatever
  replaced it — `TermObjectKey`), and `AudioDestructionLogs` (specifically for session
  audio, with an `Outcome` column — audio appears to get a stricter, separately-tracked
  destruction lifecycle than other document types, consistent with Chapter 1's framing of
  session audio as maximally sensitive).
- **Integrity**: `DocumentHashes` implements a hash chain — each row stores a `Hash` and
  its `PreviousHash`, `SealedAt` — a tamper-evidence mechanism for clinical documents, the
  kind of thing you'd want to be able to prove a signed note hasn't been altered after the
  fact.
- **Sharing**: `ShareLinks` (a document share link with a `TokenHash` — never a raw token —
  optional `PasswordHash`, `ExpiresAt`, and a `ViewCap`/`ViewsUsed` pair limiting total
  views) and `ShareAccesses` (every access against a link, including coarse geolocation and
  user agent — an audit trail for who actually opened a shared document, not just that a
  link existed).
- **Formal compliance checking**: `ConformityRulesets` (a versioned set of required
  (`Elements`) and forbidden (`Prohibitions`) content for a given `DocumentKind`, tied to a
  specific regulatory `ArticleRef`) and `ConformityChecks` (the result of actually running
  a document against a ruleset — `MissingElements`, `ForbiddenFound`, `Passed`). This is a
  genuinely rare thing to see modeled explicitly in a schema: automated verification that a
  clinical document satisfies a *specific, citable regulation*, versioned so old documents
  can still be checked against the ruleset that was in force when they were created.
- **Data portability**: `AccountExports` (a full account data export, scoped and formatted,
  landing in object storage) and `Dossiers` (a generated patient dossier bundle with a
  `Manifest` and an `IntegrityVerified` flag — plausibly the artifact a `ShareLink` actually
  points at).
- **Access accountability**: `AccessLogs` and `BreakGlassGrants` — covered fully in Chapters
  6 and 9, and already wired into EF Core.

## The provider marketplace

A public-facing directory feature, distinct from anything a logged-in provider or patient
does inside the product: `ProviderProfiles` (already introduced above) carries its own
listing-specific lifecycle columns — `ListingState`, `ListingOptedInAt`,
`ListingTermsVersion`, `ListingWithdrawnAt` — separate from the profile content itself,
so a provider can maintain a profile without necessarily being publicly listed.
`ProfileListingEvents` is the audit trail of state transitions on that listing (mirroring
`ConsentEvents`'s shape). `ServiceOfferings` is what a listed provider actually offers
publicly — kind, duration, price, currency, and an `IsPublic`/`Active` pair distinguishing
"exists" from "currently bookable by the public."

## Platform billing — Anamnys's own SaaS subscription

Not to be confused with anything above: this is Anamnys's *own* revenue model, i.e. what a
practice pays *Anamnys* to use the product. `Plans` (with `MonthlyPriceId`/`AnnualPriceId`
— external price references) and `PlanFeatures` (a simple `PlanId`/`FeatureKey` join table
gating feature access by subscription tier) define what's for sale. `Subscriptions` tracks
a provider's actual subscription — status, billing period, trial end, and
`PaddleCustomerId`/`PaddleSubscriptionId`, naming Paddle as the payment processor.
`UsageCounters` tracks metered usage against a cap per billing period (`CapturedSessions`,
`Cap`, `WarnedAt`) — consistent with a usage-based or usage-limited pricing tier.
`WebhookEvents` is a generic inbound-webhook ledger (`ExternalEventId`, `Type`,
`OccurredAt`, `ReceivedAt`, `ProcessedAt`, raw `Payload`) — almost certainly for consuming
Paddle's webhook events reliably, with the `ReceivedAt`/`ProcessedAt` split suggesting
at-least-once delivery handling.

## One table from the marketing site

`ContactMessages` (name, email, phone, message, timestamp) is the plain contact-form
submission table backing `apps/web`'s contact page (Chapter 14) — the one table in this
entire tour with nothing to do with clinical data, compliance, or billing. A useful
reminder that not everything in this schema is high-stakes; some of it is just a contact
form.

## What this chapter should leave you with

Two things. First, a real sense of the product's eventual scope — scheduling, a full
clinical documentation pipeline with NLP-driven extraction and theming, standardized
assessment instruments, Brazilian TISS/ANS insurance billing, a genuinely thorough
HIPAA/LGPD compliance layer, a public provider marketplace, and Anamnys's own SaaS billing
— all designed before most of it has application code. Second, a concrete sense of how to
read this codebase going forward: when you're trying to understand a feature, check both
the C# entities *and* the raw SQL schema, because — as Chapter 9's `Provider.CrpNumber`
example showed — the schema is very often ahead of the code, and the schema is where the
real design intent for a not-yet-built feature already lives.

Part 6 shifts from what the system stores to how the four front-end applications actually
present it — starting with the shape they all share.
