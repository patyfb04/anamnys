-- =============================================================================
--  Anamnys — scheduled maintenance
--
--  Three routines the schema depends on but cannot run by itself. Each one
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
-- Run every minute.
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

commit;

-- =============================================================================
--  Operational checks — queries worth putting on a dashboard
-- =============================================================================

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
