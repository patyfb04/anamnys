-- =============================================================================
--  Anamnys — scheduled maintenance
--
--  Four routines the schema depends on but cannot run by itself. Each one
--  exists because a constraint elsewhere could not express the rule.
--
--  Run after 01_schema.sql. Schedule the functions from HANGFIRE — the app
--  already runs it, and it is the right choice here. Cadence is in each comment.
--
--  ON NEON, DO NOT USE pg_cron FOR THESE. Neon supports the extension, but its
--  jobs only fire while the compute is awake, and a Neon compute scales to zero
--  after inactivity. The hold sweeper below matters precisely when nobody is
--  using the system — which is exactly when the compute is suspended and pg_cron
--  is not running. It would fail silently and look fine.
--  Hangfire runs in the API process, so it stays awake and also wakes the
--  compute when it connects.
-- =============================================================================

begin;

-- ── 0. refuse to run against a schema that is not there ──────────────────────
-- WHY THIS GUARD EXISTS. PostgreSQL does NOT validate the body of a plpgsql
-- function at CREATE time — it checks syntax and resolves table names only when
-- the function is CALLED. So this whole file runs cleanly against a COMPLETELY
-- EMPTY database: four "CREATE FUNCTION", a "COMMIT", exit code 0, zero tables.
-- Nothing fails until someone calls one, weeks later, and gets
-- `relation "BookingHolds" does not exist` from inside a background job.
--
-- Measured, not assumed: run this file alone on a fresh database and it reports
-- success. That is the same failure family as pg_cron on Neon — silent, and it
-- looks fine. Run 01_schema.sql first.
do $$
declare
  faltando text[];
begin
  select array_agg(t) into faltando
    from unnest(array['BookingHolds','RecordingConsents','ConsentEvents',
                      'PatientAuthTokens','Appointments']) as t
   where to_regclass(format('public.%I', t)) is null;

  if faltando is not null then
    raise exception
      'Schema not present: % missing. Run 01_schema.sql before this file.',
      array_to_string(faltando, ', ')
      using hint = 'These functions would be created without error and fail only '
                   'when first called from a background job — long after anyone '
                   'is watching. Refusing now instead.';
  end if;
end;
$$;


-- ── 1. release expired booking holds ─────────────────────────────────────────
-- The non-overlap constraint on "BookingHolds" cannot test ExpiresAt > now(),
-- because now() is not immutable and constraint predicates must be. So that
-- constraint treats an expired hold as still live.
--
-- Scope of the problem, measured rather than assumed: an expired hold blocks
-- ANOTHER HOLD on the same slot, but not an appointment — the cross-table
-- trigger does filter on expiry. So an abandoned booking form does not stop the
-- provider from booking that slot; it stops the next PATIENT from starting a
-- booking there. Bad enough, and invisible from the provider's side.
--
-- THIS IS THE SAFETY NET, NOT THE MECHANISM. Run HOURLY.
--
-- The primary sweep belongs in the AVAILABILITY QUERY (F-62): every time free
-- intervals are computed for a provider, release that provider's expired holds
-- first. That fixes the slot at the moment it matters, with no latency.
--
-- Why it cannot be hung off the BOOKING attempt instead, which is the obvious
-- place: the patient never tries to book a blocked slot, because a hold that
-- looks live removes the slot from the availability list — so the patient never
-- SEES it. The trigger would be an action the defect itself prevents. Sweeping
-- on listing works; sweeping on booking is a loop that cancels itself.
--
-- Why the routine still exists after that. If the availability query gets a bug,
-- or someone refactors it and drops the sweep, nothing else notices — slots
-- silently vanish from every calendar and no error is raised anywhere. This
-- routine catches it, and the dashboard query below ("holds the sweeper is not
-- clearing") names it. A mechanism with no independent check is a mechanism you
-- find out about from a customer.
--
-- Cost, since it drove the change: at one run per minute a Neon compute never
-- scales to zero — 1440 connections a day, billed continuously. Hourly, it is
-- awake roughly 5 minutes per hour (Neon suspends after ~5 min idle), and during
-- business hours it would be awake anyway.
create or replace function anamnys_release_expired_holds()
returns integer language plpgsql as $$
declare
  released integer;
begin
  update "BookingHolds"
     set "ReleasedAt" = now()
   where "ReleasedAt" is null
     and "ConvertedAppointmentId" is null
     and "ExpiresAt" <= now();
  get diagnostics released = row_count;
  return released;
end;
$$;


-- ── 2. expire recording consent ──────────────────────────────────────────────
-- Recording capability is a pure function of consent state (F-24). A consent
-- past its validity window must stop permitting capture on its own, not when
-- someone next opens the screen.
--
-- Every transition is written to "ConsentEvents", because the state alone is
-- not evidence of when it changed. Run hourly.
create or replace function anamnys_expire_consents()
returns integer language plpgsql as $$
declare
  expired integer;
begin
  with moved as (
    update "RecordingConsents"
       set "State" = 'EXPIRED'
     where "State" = 'SIGNED'
       and "ValidUntil" is not null
       and "ValidUntil" <= now()
    returning "Id"
  )
  insert into "ConsentEvents" ("ConsentId","FromState","ToState","Reason")
  select "Id", 'SIGNED', 'EXPIRED', 'validity window elapsed' from moved;
  get diagnostics expired = row_count;
  return expired;
end;
$$;


-- ── 3. purge used and expired portal tokens ──────────────────────────────────
-- Single-use login tokens are a standing credential until deleted. Keeping
-- spent ones adds risk and no value. Run daily.
create or replace function anamnys_purge_auth_tokens(older_than interval default interval '7 days')
returns integer language plpgsql as $$
declare
  purged integer;
begin
  delete from "PatientAuthTokens"
   where ("UsedAt" is not null and "UsedAt" < now() - older_than)
      or ("ExpiresAt" < now() - older_than);
  get diagnostics purged = row_count;
  return purged;
end;
$$;

-- ── 4. purge abandoned booking holds ─────────────────────────────────────────
-- Job 1 RELEASES an expired hold; nothing ever DELETES it. Every abandoned
-- booking form leaves a permanent row carrying a "PatientId", and nothing reads
-- it after release. Two problems, and the second is the one that matters:
--
-- SIZE. This becomes the largest table in the system, made entirely of attempts
-- that went nowhere.
--
-- MINIMIZAÇÃO. LGPD art. 6º III: tratamento limitado ao mínimo necessário para a
-- finalidade. The purpose of a hold ends when it is released. Keeping identified
-- booking attempts forever, with no purpose attached, is retention without
-- basis — a smaller problem than a leak, and a much easier one to fix before
-- there is data than after.
--
-- ONLY ABANDONED HOLDS. A hold that converted is provenance: "Appointments"
-- ."HoldId" points back at it, and the FK is ON DELETE SET NULL, so deleting it
-- would silently cut the link between the appointment and how it was made.
-- Those stay.
--
-- The 90 days is a reasonable guess, not a decision: long enough for support to
-- answer "why couldn't I book that slot last month", short enough not to
-- accumulate. Settle it with D-10, which already owns the booking policy.
-- Run daily.
create or replace function anamnys_purge_abandoned_holds(older_than interval default interval '90 days')
returns integer language plpgsql as $$
declare
  purged integer;
begin
  delete from "BookingHolds"
   where "ReleasedAt" is not null
     and "ConvertedAppointmentId" is null
     and "ReleasedAt" < now() - older_than;
  get diagnostics purged = row_count;
  return purged;
end;
$$;

commit;

-- =============================================================================
--  Operational checks — queries worth putting on a dashboard
-- =============================================================================

-- Growth of the holds table, split by what the rows are for. If "abandonadas"
-- keeps climbing while job 4 runs, the retention window is too long — or the
-- booking form is losing people, which is a product problem wearing a database
-- costume.
--
--   select count(*) filter (where "ReleasedAt" is null)                    as vivas,
--          count(*) filter (where "ConvertedAppointmentId" is not null)    as converteram,
--          count(*) filter (where "ReleasedAt" is not null
--                             and "ConvertedAppointmentId" is null)        as abandonadas
--     from "BookingHolds";

-- Google Calendar channels about to expire. Once one lapses the mirror stops
-- receiving changes SILENTLY: the app keeps working and simply stops seeing
-- outside edits. Alert on anything inside 48 hours.
--
--   select p."Email", c."ChannelExpiresAt"
--     from "CalendarConnections" c
--     join "Providers" p on p."Id" = c."ProviderId"
--    where c."RevokedAt" is null
--      and c."ChannelExpiresAt" < now() + interval '48 hours';

-- Holds that the sweeper is not clearing — if this is ever non-empty, job 1 is
-- not running and slots are leaking out of every calendar.
--
--   select count(*) from "BookingHolds"
--    where "ReleasedAt" is null and "ConvertedAppointmentId" is null
--      and "ExpiresAt" < now() - interval '5 minutes';

-- Notices that are past due and still unsent. The Terms of Use fix exact day
-- counts for dunning and trial expiry; a backlog here is a contractual problem,
-- not a queue problem.
--
--   select "Kind", count(*) from "Notifications"
--    where "DeliveryStatus" = 'pending' and "ScheduledFor" < now() - interval '1 hour'
--    group by "Kind";

-- Charts approaching their retention limit, and unsigned drafts (F-06).
--
--   select count(*) filter (where "RetentionUntil" < current_date + 90) as guarda_vencendo,
--          count(*) filter (where "SignedAt" is null)                   as nao_assinados
--     from "ClinicalDocuments";

-- SATEPSI status going stale. The CFP's lists change, and a status recorded once
-- and never rechecked is a claim with no backing that ages silently — the same
-- failure family as pg_cron on Neon. Anything past a year needs a human to open
-- satepsi.cfp.org.br and look, because nothing here can detect the drift.
--
--   select "Code", "SatepsiStatus", "SatepsiCheckedOn"
--     from "Instruments"
--    where "SatepsiCheckedOn" is not null
--      and "SatepsiCheckedOn" < current_date - interval '12 months'
--    order by "SatepsiCheckedOn";

-- Scores recorded against instruments the catalogue does not offer. Everything
-- the CFP forbids is already refused at write time, so what shows up here is
-- the other case: unclassified instruments the professional uses anyway. That
-- is not a violation and must not be treated as one — it is a signal that the
-- catalogue is missing something she needs, and a candidate for the art. 13
-- route (ask the CRP to submit it to the CCAP).
--
--   select i."Code", i."SatepsiStatus", count(*) as lancamentos,
--          count(distinct a."PatientId") as pacientes,
--          max(a."AppliedAt") as ultimo
--     from "ScaleApplications" a
--     join "Instruments" i on i."Id" = a."InstrumentId"
--    where i."Enabled" = false
--    group by i."Code", i."SatepsiStatus"
--    order by 3 desc;

-- Rows that should be impossible: an application recorded against an instrument
-- the CFP forbids. The trigger refuses these, so a non-zero count means the
-- guard was bypassed — a migration load with triggers disabled, or an
-- instrument whose status changed AFTER the rows were written. The second is
-- the likely one, and it is why "InstrumentSeries" exists as a second line.
--
--   select i."Code", i."SatepsiStatus", count(*)
--     from "ScaleApplications" a
--     join "Instruments" i on i."Id" = a."InstrumentId"
--    where i."SatepsiStatus" in ('nao_avaliado','desfavoravel')
--    group by 1, 2;

-- Documents signed while the conformity check was failing, or with content the
-- ruleset forbids (F-05). The check is advisory by default, so a signature can
-- happen over a red flag — which is exactly the case worth reviewing.
--
--   select d."Kind", c."CheckedAt", c."MissingElements", c."ForbiddenFound"
--     from "ConformityChecks" c
--     join "ClinicalDocuments" d on d."Id" = c."DocumentId"
--    where d."SignedAt" is not null
--      and (c."Passed" = false or c."ForbiddenFound" <> '[]'::jsonb)
--      and c."CheckedAt" = (select max(c2."CheckedAt") from "ConformityChecks" c2
--                            where c2."DocumentId" = d."Id")
--    order by c."CheckedAt" desc;
