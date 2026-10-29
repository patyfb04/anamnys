-- Dev-only theme history for F-10/F-11 (página Análises), for the first two active patients (by name) of
-- every non-test provider (emails not ending in @test.local). Adds 12 signed DAP notes, one every 30 days
-- from ~11 months ago, each tagged by the provider with an exact passage, so the patient passes the
-- 6-signed-session minimum and the grid has lanes, a parent with children (Trabalho), co-occurrence and a
-- theme that stops being recorded (Sono, notes 1-8 only — useful later for F-12):
--   sono                     1-8        ansiedade                 5-12
--   sobrecarga_jornada       1,2,4,5,7  conflito_no_trabalho      3,6     (children of Trabalho)
--   familia                  2,6,9,11   luto                      10-12   saudade_desenraizamento 9,12
-- Every person and event is fictitious. Idempotent: a patient that already has these notes is skipped.
-- Never run outside a local development database.
--   docker exec -i <postgres-container> sh -c 'PGPASSWORD=$POSTGRES_PASSWORD psql -v ON_ERROR_STOP=1 -U postgres -d anamnysdb' < dev-seed/analysis-samples.sql

BEGIN;

DO $$
DECLARE
  marker constant text := 'Seed de análise (exemplo)';
  t record;
  crp text;
  i int;
  at timestamptz;
  note_id uuid;
  content text;
  ph record;
  ref_id uuid;
  pos int;
BEGIN
  CREATE TEMP TABLE plan (code text, phrase text, notes int[]) ON COMMIT DROP;
  INSERT INTO plan VALUES
    ('sono', 'acorda várias vezes durante a noite', ARRAY[1,2,3,4,5,6,7,8]),
    ('sobrecarga_jornada', 'jornada de trabalho longa', ARRAY[1,2,4,5,7]),
    ('conflito_no_trabalho', 'discussão com o colega de equipe', ARRAY[3,6]),
    ('ansiedade', 'aperto no peito antes de reuniões', ARRAY[5,6,7,8,9,10,11,12]),
    ('familia', 'conversa difícil com a mãe', ARRAY[2,6,9,11]),
    ('luto', 'falecimento da tia', ARRAY[10,11,12]),
    ('saudade_desenraizamento', 'saudade da cidade natal', ARRAY[9,12]);

  FOR t IN
    SELECT p."Id" AS patient_id, p."ProviderId" AS provider_id
    FROM (
      SELECT p.*, row_number() OVER (PARTITION BY p."ProviderId" ORDER BY p."LastName", p."FirstName", p."Id") AS rn
      FROM "Patients" p
      JOIN "Providers" pr ON pr."Id" = p."ProviderId"
      WHERE pr."Email" NOT LIKE '%@test.local' AND p."ArchivedAt" IS NULL AND p."DiscardedAt" IS NULL
        AND (p."FirstName", p."LastName") NOT IN (('Rodrigo', 'Teixeira'), ('Marina', 'Coelho'))
    ) p
    WHERE p.rn <= 2
      AND NOT EXISTS (SELECT 1 FROM "Notes" n WHERE n."PatientId" = p."Id" AND n."RawTranscript" LIKE marker || '%')
  LOOP
    SELECT coalesce(nullif(concat_ws('/', pr."CrpNumber", pr."CrpRegion"), ''), '00000/00')
      INTO crp FROM "Providers" pr WHERE pr."Id" = t.provider_id;

    FOR i IN 1..12 LOOP
      at := now() - make_interval(days => 350 - (i - 1) * 30);
      SELECT 'Relata que ' || string_agg(pl.phrase, '; ' ORDER BY pl.code) || '.'
        INTO content FROM plan pl WHERE i = ANY (pl.notes);

      INSERT INTO "Notes" ("PatientId","ProviderId","Status","InputMode","Format","RawTranscript","SignedAt","SignedByCrp","StructureStatus","CreatedAt","UpdatedAt")
      VALUES (t.patient_id, t.provider_id, 'Signed', 'Text', 'DAP', marker || ' ' || i || '. ' || content,
              at, crp, 'done', at - interval '1 hour', at)
      RETURNING "Id" INTO note_id;
      INSERT INTO "NoteSections" ("NoteId","Key","Content","Position","GeneratedAt") VALUES
        (note_id, 'data', content, 0, at),
        (note_id, 'assessment', 'Sessão de acompanhamento (exemplo).', 1, at),
        (note_id, 'plan', 'Manter acompanhamento (exemplo).', 2, at);

      FOR ph IN SELECT pl.code, pl.phrase FROM plan pl WHERE i = ANY (pl.notes) LOOP
        pos := strpos(content, ph.phrase) - 1;
        INSERT INTO "ExcerptRefs" ("SourceType","SourceId","OffsetStart","OffsetEnd","DatedAt","ProviderId","SectionKey","Quote","Kind")
        VALUES ('note', note_id, pos, pos + length(ph.phrase), at, t.provider_id, 'data', ph.phrase, 'exact')
        RETURNING "Id" INTO ref_id;
        INSERT INTO "ThemeTags" ("NoteId","TermId","ExcerptRefId","CreatedBy","AddedById","CreatedAt")
        SELECT note_id, tt."Id", ref_id, 'human', t.provider_id, at
        FROM "ThemeTerms" tt JOIN "ThemeVocabularies" v ON v."Id" = tt."VocabularyId" AND v."Active"
        WHERE tt."Code" = ph.code;
      END LOOP;
    END LOOP;
  END LOOP;
END $$;

SELECT p."FirstName" || ' ' || p."LastName" AS patient, count(*) AS seeded_notes
FROM "Notes" n JOIN "Patients" p ON p."Id" = n."PatientId"
WHERE n."RawTranscript" LIKE 'Seed de análise (exemplo)%'
GROUP BY 1 ORDER BY 1;

COMMIT;
