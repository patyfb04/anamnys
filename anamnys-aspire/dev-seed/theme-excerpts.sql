-- Dev-only: turns the section references of the sample theme tags (dev-seed/theme-notes.sql) into
-- exact references on known sentences, so the passage highlight can be tested without running the
-- model. Notes A and C get exact passages; B, D and E keep section references (use "Localizar
-- trechos" there). Idempotent: only section references whose phrase is found are changed.
-- Requires design/migrations/2026-10-24-excerpt-refs.sql. Never run outside a local dev database.
--
--   docker exec -i <postgres-container> sh -c 'PGPASSWORD=$POSTGRES_PASSWORD psql -v ON_ERROR_STOP=1 -U postgres -d anamnysdb' < dev-seed/theme-excerpts.sql

BEGIN;

WITH phrases(code, phrase) AS (
  VALUES ('sono', 'insônia há três semanas'),
         ('trabalho', 'sensação de sobrecarga com as metas da equipe'),
         ('luto', 'falecimento do avô')
),
targets AS (
  SELECT r."Id" AS ref_id, s."Key" AS section_key, ph.phrase,
         strpos(s."Content", ph.phrase) - 1 AS start_at
  FROM "ThemeTags" t
  JOIN "ThemeTerms" tt ON tt."Id" = t."TermId"
  JOIN phrases ph ON ph.code = tt."Code"
  JOIN "ExcerptRefs" r ON r."Id" = t."ExcerptRefId" AND r."Kind" = 'section'
  JOIN "NoteSections" s ON s."NoteId" = t."NoteId" AND s."Key" = 'data'
  WHERE t."RemovedAt" IS NULL AND strpos(s."Content", ph.phrase) > 0
)
UPDATE "ExcerptRefs" r
SET "Kind" = 'exact',
    "SectionKey" = tg.section_key,
    "Quote" = tg.phrase,
    "OffsetStart" = tg.start_at,
    "OffsetEnd" = tg.start_at + length(tg.phrase)
FROM targets tg
WHERE r."Id" = tg.ref_id;

COMMIT;
