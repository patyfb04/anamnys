-- F-16: record search (design/specs/2026-10-31-record-search-design.md, section 2).
-- Portuguese stemming, accent-insensitive: anamnys_pt is a copy of portuguese with unaccent before the stemmer.
-- to_tsvector(regconfig, text) is already immutable, so the index needs no wrapper function. The search queries must
-- repeat the expression of the index exactly (RecordSearchQuery).
-- Idempotent; all or nothing.
BEGIN;

CREATE EXTENSION IF NOT EXISTS unaccent;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_ts_config WHERE cfgname = 'anamnys_pt') THEN
    CREATE TEXT SEARCH CONFIGURATION anamnys_pt (COPY = portuguese);
    ALTER TEXT SEARCH CONFIGURATION anamnys_pt ALTER MAPPING FOR hword, hword_part, word WITH unaccent, portuguese_stem;
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS "NoteSections_Content_fts_idx" ON "NoteSections" USING gin (to_tsvector('anamnys_pt', "Content"));

COMMIT;
