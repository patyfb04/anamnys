-- Dev-only sample data for F-06 (guarda e descarte), for every non-test provider (emails not ending in
-- @test.local). Every person is fictitious. Idempotent: patients are created only if missing by name.
--   * Rodrigo Teixeira: one signed note from 2020-03-10 -> retention ended 2025-03-10 (expired, can be
--     discarded).
--   * Marina Coelho: one signed note from 2021-12-15 -> retention ends 2026-12-15 (inside the 90-day
--     window), plus a note ready for review left unsigned for 15 days.
-- Every note gets DAP sections so it can be opened (and the unsigned one edited and signed).
-- Never run outside a local development database.
--   docker exec -i <postgres-container> sh -c 'PGPASSWORD=$POSTGRES_PASSWORD psql -v ON_ERROR_STOP=1 -U postgres -d anamnysdb' < dev-seed/retention-samples.sql

BEGIN;

CREATE TEMP TABLE sample (first text, last text, signed_on date, years int) ON COMMIT DROP;
INSERT INTO sample VALUES
 ('Rodrigo', 'Teixeira', DATE '2020-03-10', 5),
 ('Marina', 'Coelho', DATE '2021-12-15', 5);

CREATE TEMP TABLE created ON COMMIT DROP AS
WITH ins AS (
  INSERT INTO "Patients" ("ProviderId", "FirstName", "LastName", "DateOfBirth", "CreatedAt", "LastRecordAt", "RetentionUntil")
  SELECT pr."Id", s.first, s.last, DATE '1985-06-01', s.signed_on::timestamptz - interval '30 days',
         s.signed_on, (s.signed_on + make_interval(years => s.years))::date
  FROM "Providers" pr CROSS JOIN sample s
  WHERE pr."Email" NOT LIKE '%@test.local'
    AND NOT EXISTS (SELECT 1 FROM "Patients" p
                    WHERE p."ProviderId" = pr."Id" AND p."FirstName" = s.first AND p."LastName" = s.last)
  RETURNING "Id", "ProviderId", "FirstName", "LastRecordAt"
)
SELECT * FROM ins;

-- One signed note on the last-record date for each new patient.
INSERT INTO "Notes" ("PatientId", "ProviderId", "Status", "InputMode", "Format", "SignedAt", "SignedByCrp", "CreatedAt", "UpdatedAt")
SELECT c."Id", c."ProviderId", 'Signed', 'Text', 'DAP',
       c."LastRecordAt"::timestamptz + interval '15 hours', coalesce(pr."CrpRegion" || '/' || pr."CrpNumber", '00/00000'),
       c."LastRecordAt"::timestamptz + interval '14 hours', c."LastRecordAt"::timestamptz + interval '15 hours'
FROM created c JOIN "Providers" pr ON pr."Id" = c."ProviderId";

-- A note ready for review, left unsigned for 15 days (Marina only).
INSERT INTO "Notes" ("PatientId", "ProviderId", "Status", "InputMode", "Format", "CreatedAt", "UpdatedAt")
SELECT c."Id", c."ProviderId", 'ReadyForReview', 'Text', 'DAP', now() - interval '15 days', now() - interval '15 days'
FROM created c WHERE c."FirstName" = 'Marina';

-- DAP sections for every note of the new patients.
INSERT INTO "NoteSections" ("NoteId", "Key", "Content", "Position", "GeneratedAt")
SELECT n."Id", x.key, x.content, x.pos, n."CreatedAt"
FROM "Notes" n JOIN created c ON c."Id" = n."PatientId"
CROSS JOIN (VALUES
  ('data', 'Paciente relata rotina estável e sono regular nas últimas semanas (exemplo).', 0),
  ('assessment', 'Sem mudanças relevantes em relação à sessão anterior (exemplo).', 1),
  ('plan', 'Manter acompanhamento quinzenal (exemplo).', 2)) AS x(key, content, pos);

SELECT "FirstName", "LastRecordAt" FROM created;

COMMIT;
