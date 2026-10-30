-- F-12: theme absence detection (design/specs/2026-10-29-theme-absence-design.md, section 2).
-- Gives each provider the number of consecutive signed sessions without a record after which a theme that was
-- recorded repeatedly is described as "sem registro recente" on the case analysis (default 4, 2 to 20).
-- Idempotent; all or nothing.
BEGIN;

ALTER TABLE "Providers" ADD COLUMN IF NOT EXISTS "AbsenceSessions" integer NOT NULL DEFAULT 4;
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'Providers_AbsenceSessions_ck') THEN
    ALTER TABLE "Providers" ADD CONSTRAINT "Providers_AbsenceSessions_ck" CHECK (("AbsenceSessions" >= 2 AND "AbsenceSessions" <= 20));
  END IF;
END $$;

COMMIT;
