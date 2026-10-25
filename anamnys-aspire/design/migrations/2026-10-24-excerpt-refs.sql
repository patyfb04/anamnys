-- F-03: excerpt references (design/specs/2026-10-24-excerpt-references-design.md, sections 2 and 3).
-- Gives ExcerptRefs an owner (provider), the section it points into, the quoted text and the kind
-- (exact | section), and links every existing active theme tag to a section reference.
-- Idempotent; all or nothing.
BEGIN;

-- References that predate this model carry no provider, section or quote and nothing creates them:
-- they are removed (the foreign keys that point at them are ON DELETE SET NULL), once, when the
-- columns are first added.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                  WHERE table_schema = 'public' AND table_name = 'ExcerptRefs' AND column_name = 'ProviderId') THEN
    DELETE FROM "ExcerptRefs";
    ALTER TABLE "ExcerptRefs" ADD COLUMN "ProviderId" uuid NOT NULL;
    ALTER TABLE "ExcerptRefs" ADD COLUMN "Kind" text NOT NULL;
  END IF;
END $$;

ALTER TABLE "ExcerptRefs" ADD COLUMN IF NOT EXISTS "SectionKey" text;
ALTER TABLE "ExcerptRefs" ADD COLUMN IF NOT EXISTS "Quote" text;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExcerptRefs_ProviderId_fkey') THEN
    ALTER TABLE "ExcerptRefs"
      ADD CONSTRAINT "ExcerptRefs_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExcerptRefs_Kind_ck') THEN
    ALTER TABLE "ExcerptRefs"
      ADD CONSTRAINT "ExcerptRefs_Kind_ck" CHECK (("Kind" = ANY (ARRAY['exact'::text, 'section'::text])));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExcerptRefs_Exact_ck') THEN
    ALTER TABLE "ExcerptRefs"
      ADD CONSTRAINT "ExcerptRefs_Exact_ck" CHECK ((("Kind" = 'exact' AND "OffsetStart" IS NOT NULL AND "OffsetEnd" IS NOT NULL AND "Quote" IS NOT NULL)
                                                   OR ("Kind" = 'section' AND "OffsetStart" IS NULL AND "OffsetEnd" IS NULL)));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExcerptRefs_Quote_ck') THEN
    ALTER TABLE "ExcerptRefs"
      ADD CONSTRAINT "ExcerptRefs_Quote_ck" CHECK (("Quote" IS NULL) OR (char_length("Quote") <= 200));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExcerptRefs_NoteSection_ck') THEN
    ALTER TABLE "ExcerptRefs"
      ADD CONSTRAINT "ExcerptRefs_NoteSection_ck" CHECK (("SourceType" <> 'note') OR ("SectionKey" IS NOT NULL));
  END IF;
END $$;

DROP INDEX IF EXISTS "ExcerptRefs_Source_idx";
CREATE INDEX IF NOT EXISTS "ExcerptRefs_Provider_Source_idx" ON "ExcerptRefs" ("ProviderId","SourceType","SourceId");

-- Every active theme tag without a reference points at the first non-empty section of its note
-- (kind section). Notes with no text keep the tag without a reference.
WITH todo AS (
  SELECT t."Id" AS tag_id,
         gen_random_uuid() AS ref_id,
         n."ProviderId" AS provider_id,
         n."Id" AS note_id,
         (SELECT s."Key" FROM "NoteSections" s
           WHERE s."NoteId" = n."Id" AND btrim(s."Content") <> ''
           ORDER BY s."Position" LIMIT 1) AS section_key,
         COALESCE(se."ActualStart", se."ScheduledStart", n."CreatedAt") AS dated_at
    FROM "ThemeTags" t
    JOIN "Notes" n ON n."Id" = t."NoteId"
    LEFT JOIN "Sessions" se ON se."Id" = n."SessionId"
   WHERE t."RemovedAt" IS NULL AND t."ExcerptRefId" IS NULL
),
ins AS (
  INSERT INTO "ExcerptRefs" ("Id","ProviderId","SourceType","SourceId","SectionKey","Kind","DatedAt")
  SELECT ref_id, provider_id, 'note', note_id, section_key, 'section', dated_at
    FROM todo WHERE section_key IS NOT NULL
  RETURNING "Id"
)
UPDATE "ThemeTags" t SET "ExcerptRefId" = todo.ref_id
  FROM todo
 WHERE t."Id" = todo.tag_id AND todo.section_key IS NOT NULL
   AND EXISTS (SELECT 1 FROM ins WHERE ins."Id" = todo.ref_id);

COMMIT;
