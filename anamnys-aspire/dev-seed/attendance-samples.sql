-- Dev-only appointment history for F-04 (comparecimento), for every non-test provider (emails not ending
-- in @test.local): the first four active patients (by name) get ~10 months of weekly past appointments
-- with mixed outcomes, plus one session without appointment ("sem agendamento").
-- Pattern per patient, repeating every 10 weeks: 6 attended, 1 no-show, 1 cancelled by the patient
-- (30 h before), 1 cancelled by the provider (3 h before), 1 left unmarked (counts as "sem registro").
-- The third patient comes every other week, so intervals differ. The F-06 retention samples (Rodrigo
-- Teixeira, Marina Coelho) are left out so their retention dates stay as seeded.
-- Idempotent: rows are marked with ExternalEventId 'dev-seed-attendance'; a provider that already has
-- them is skipped. Slots are at 06:xx in São Paulo to avoid real appointments; any overlap is skipped.
-- Never run outside a local development database.
--   docker exec -i <postgres-container> sh -c 'PGPASSWORD=$POSTGRES_PASSWORD psql -v ON_ERROR_STOP=1 -U postgres -d anamnysdb' < dev-seed/attendance-samples.sql

BEGIN;

CREATE TEMP TABLE targets ON COMMIT DROP AS
SELECT * FROM (
  SELECT p."Id" AS patient_id, p."ProviderId" AS provider_id,
         (row_number() OVER (PARTITION BY p."ProviderId" ORDER BY p."LastName", p."FirstName", p."Id") - 1) AS rn
  FROM "Patients" p
  JOIN "Providers" pr ON pr."Id" = p."ProviderId"
  WHERE pr."Email" NOT LIKE '%@test.local'
    AND p."ArchivedAt" IS NULL AND p."DiscardedAt" IS NULL
    AND (p."FirstName", p."LastName") NOT IN (('Rodrigo', 'Teixeira'), ('Marina', 'Coelho'))
    AND NOT EXISTS (SELECT 1 FROM "Appointments" a
                    WHERE a."ProviderId" = p."ProviderId" AND a."ExternalEventId" = 'dev-seed-attendance')
) t WHERE rn < 4;

CREATE TEMP TABLE slots ON COMMIT DROP AS
SELECT t.patient_id, t.provider_id, t.rn, w AS week,
       -- 06:00 + rn*15 min in São Paulo, on a weekday that differs per patient
       ((date_trunc('day', now() AT TIME ZONE 'America/Sao_Paulo') - make_interval(days => w * 7 + 1 + t.rn::int))
         + make_interval(hours => 6, mins => t.rn::int * 15)) AT TIME ZONE 'America/Sao_Paulo' AS starts_at
FROM targets t
CROSS JOIN generate_series(1, 44) AS w
WHERE t.rn <> 2 OR w % 2 = 0;

INSERT INTO "Appointments" ("ProviderId", "PatientId", "StartsAt", "EndsAt", "Timezone", "Modality", "Status",
                            "CreatedBy", "CancelledAt", "CancelledBy", "CancellationReason", "ExternalEventId", "CreatedAt")
SELECT s.provider_id, s.patient_id, s.starts_at, s.starts_at + interval '50 minutes', 'America/Sao_Paulo',
       CASE WHEN s.week % 3 = 0 THEN 'presencial' ELSE 'online' END,
       CASE s.week % 10 WHEN 6 THEN 'no_show' WHEN 7 THEN 'cancelled' WHEN 8 THEN 'cancelled'
                        WHEN 9 THEN 'scheduled' ELSE 'attended' END,
       'provider',
       CASE s.week % 10 WHEN 7 THEN s.starts_at - interval '30 hours'
                        WHEN 8 THEN s.starts_at - interval '3 hours' END,
       CASE s.week % 10 WHEN 7 THEN 'patient' WHEN 8 THEN 'provider' END,
       CASE s.week % 10 WHEN 7 THEN 'Imprevisto do paciente (exemplo)'
                        WHEN 8 THEN 'Reagendado pelo profissional (exemplo)' END,
       'dev-seed-attendance', s.starts_at - interval '14 days'
FROM slots s
ON CONFLICT DO NOTHING;

-- One attended session with no appointment per patient, 17 days ago at 07:00 São Paulo.
INSERT INTO "Sessions" ("PatientId", "ProviderId", "ScheduledStart", "ScheduledMinutes", "ActualStart", "ActualEnd",
                        "Modality", "Attendance")
SELECT t.patient_id, t.provider_id, NULL, NULL,
       ((date_trunc('day', now() AT TIME ZONE 'America/Sao_Paulo') - interval '17 days')
         + make_interval(hours => 7, mins => t.rn::int * 15)) AT TIME ZONE 'America/Sao_Paulo',
       ((date_trunc('day', now() AT TIME ZONE 'America/Sao_Paulo') - interval '17 days')
         + make_interval(hours => 7, mins => t.rn::int * 15 + 45)) AT TIME ZONE 'America/Sao_Paulo',
       'presencial', 'attended'
FROM targets t;

SELECT p."FirstName" || ' ' || p."LastName" AS patient, count(a.*) AS appointments
FROM targets t JOIN "Patients" p ON p."Id" = t.patient_id
LEFT JOIN "Appointments" a ON a."PatientId" = t.patient_id AND a."ExternalEventId" = 'dev-seed-attendance'
GROUP BY 1 ORDER BY 1;

COMMIT;
