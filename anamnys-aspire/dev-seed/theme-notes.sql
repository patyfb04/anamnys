-- Dev-only sample notes with themes (F-02), for manual testing of tags, suggestions and the theme
-- filter. For the first 3 non-archived patients (by name) of every non-test provider (emails not
-- ending in @test.local) it adds five DAP typed notes:
--   A  signed 60 days ago  "insônia"                      tags: sono (human), trabalho (AI, accepted)
--   B  signed 30 days ago  "dificuldade para dormir"      tags: sono (AI, accepted), assedio_no_trabalho (human),
--                          + chefe, família                     familia (removed by the provider)
--   C  signed 10 days ago  "sono ruim", perda do avô      tags: sono, luto (human)
--   D  in review, today    ansiedade, família             tags: ansiedade, familia (AI proposals, untouched)
--   E  signed 5 days ago   mudança de país, saudade       no tags (try "Sugerir temas")
-- A, B and C describe sleep problems in different words: the "Sono" filter must return all three.
-- Idempotent: a patient that already has seed note A is skipped. Requires the
-- design/migrations/2026-10-21-theme-vocabulary.sql migration (vocabulary mh-1).
-- Never run outside a local development database.
--
-- Run against the Aspire-managed Postgres container (name changes per run, see `docker ps`):
--   docker exec -i <postgres-container> sh -c 'PGPASSWORD=$POSTGRES_PASSWORD psql -v ON_ERROR_STOP=1 -U postgres -d anamnysdb' < dev-seed/theme-notes.sql

BEGIN;

DO $$
DECLARE
  marker constant text := 'Paciente relata insônia há três semanas';
  t record;
  crp text;
  note_id uuid;
  term record;
BEGIN
  FOR t IN
    SELECT p."Id" AS patient_id, p."ProviderId" AS provider_id
    FROM (
      SELECT p.*, row_number() OVER (PARTITION BY p."ProviderId" ORDER BY p."LastName", p."FirstName", p."Id") AS rn
      FROM "Patients" p
      JOIN "Providers" pr ON pr."Id" = p."ProviderId"
      WHERE pr."Email" NOT LIKE '%@test.local' AND p."ArchivedAt" IS NULL
    ) p
    WHERE p.rn <= 3
      AND NOT EXISTS (
        SELECT 1 FROM "Notes" n WHERE n."PatientId" = p."Id" AND n."RawTranscript" LIKE marker || '%')
  LOOP
    SELECT coalesce(nullif(concat_ws('/', pr."CrpNumber", pr."CrpRegion"), ''), '00000/00')
      INTO crp FROM "Providers" pr WHERE pr."Id" = t.provider_id;

    -- A: insônia, trabalho
    INSERT INTO "Notes" ("PatientId","ProviderId","Status","InputMode","Format","RawTranscript","SignedAt","SignedByCrp","StructureStatus","CreatedAt","UpdatedAt")
    VALUES (t.patient_id, t.provider_id, 'Signed', 'Text', 'DAP',
      marker || '. Diz que acorda várias vezes de madrugada e chega cansado ao trabalho. Sente-se sobrecarregado com as metas.',
      now() - interval '60 days', crp, 'done', now() - interval '60 days', now() - interval '60 days')
    RETURNING "Id" INTO note_id;
    INSERT INTO "NoteSections" ("NoteId","Key","Content","Position","GeneratedAt") VALUES
      (note_id, 'data', 'Paciente relata insônia há três semanas, com despertares frequentes durante a madrugada. Refere cansaço no trabalho e sensação de sobrecarga com as metas da equipe.', 0, now() - interval '60 days'),
      (note_id, 'assessment', 'Queixa de sono associada ao período de maior demanda profissional.', 1, now() - interval '60 days'),
      (note_id, 'plan', 'Higiene do sono; registro diário do horário de deitar e acordar.', 2, now() - interval '60 days');
    INSERT INTO "ThemeTags" ("NoteId","TermId","CreatedBy","AddedById","CreatedAt")
    SELECT note_id, tt."Id", x.created_by, t.provider_id, now() - interval '60 days'
    FROM (VALUES ('sono','human'), ('trabalho','ai')) AS x(code, created_by)
    JOIN "ThemeTerms" tt ON tt."Code" = x.code
    JOIN "ThemeVocabularies" v ON v."Id" = tt."VocabularyId" AND v."Active";

    -- B: dificuldade para dormir, assédio, família (removed)
    INSERT INTO "Notes" ("PatientId","ProviderId","Status","InputMode","Format","RawTranscript","SignedAt","SignedByCrp","StructureStatus","CreatedAt","UpdatedAt")
    VALUES (t.patient_id, t.provider_id, 'Signed', 'Text', 'DAP',
      'Relata dificuldade para dormir nas noites de domingo. Conta que o chefe faz comentários humilhantes em reuniões. Mencionou a irmã de passagem.',
      now() - interval '30 days', crp, 'done', now() - interval '30 days', now() - interval '30 days')
    RETURNING "Id" INTO note_id;
    INSERT INTO "NoteSections" ("NoteId","Key","Content","Position","GeneratedAt") VALUES
      (note_id, 'data', 'Relata dificuldade para dormir, sobretudo nas noites de domingo. Conta que o chefe faz comentários humilhantes em reuniões na frente dos colegas. Menciona a irmã de passagem.', 0, now() - interval '30 days'),
      (note_id, 'assessment', 'Situação no trabalho descrita como fonte principal de desgaste.', 1, now() - interval '30 days'),
      (note_id, 'plan', 'Explorar estratégias de comunicação assertiva na próxima sessão.', 2, now() - interval '30 days');
    INSERT INTO "ThemeTags" ("NoteId","TermId","CreatedBy","AddedById","CreatedAt","RemovedAt","RemovedById")
    SELECT note_id, tt."Id", x.created_by, t.provider_id, now() - interval '30 days',
           CASE WHEN x.removed THEN now() - interval '29 days' END,
           CASE WHEN x.removed THEN t.provider_id END
    FROM (VALUES ('sono','ai',false), ('assedio_no_trabalho','human',false), ('familia','ai',true)) AS x(code, created_by, removed)
    JOIN "ThemeTerms" tt ON tt."Code" = x.code
    JOIN "ThemeVocabularies" v ON v."Id" = tt."VocabularyId" AND v."Active";

    -- C: sono ruim, luto
    INSERT INTO "Notes" ("PatientId","ProviderId","Status","InputMode","Format","RawTranscript","SignedAt","SignedByCrp","StructureStatus","CreatedAt","UpdatedAt")
    VALUES (t.patient_id, t.provider_id, 'Signed', 'Text', 'DAP',
      'Sono ruim desde o falecimento do avô, há um mês. Chorou ao falar dele.',
      now() - interval '10 days', crp, 'done', now() - interval '10 days', now() - interval '10 days')
    RETURNING "Id" INTO note_id;
    INSERT INTO "NoteSections" ("NoteId","Key","Content","Position","GeneratedAt") VALUES
      (note_id, 'data', 'Refere sono ruim desde o falecimento do avô, há cerca de um mês. Emocionou-se ao falar dele.', 0, now() - interval '10 days'),
      (note_id, 'assessment', 'Período de luto recente.', 1, now() - interval '10 days'),
      (note_id, 'plan', 'Acolhimento do luto; retomar a rotina de sono combinada.', 2, now() - interval '10 days');
    INSERT INTO "ThemeTags" ("NoteId","TermId","CreatedBy","AddedById","CreatedAt")
    SELECT note_id, tt."Id", 'human', t.provider_id, now() - interval '10 days'
    FROM (VALUES ('sono'), ('luto')) AS x(code)
    JOIN "ThemeTerms" tt ON tt."Code" = x.code
    JOIN "ThemeVocabularies" v ON v."Id" = tt."VocabularyId" AND v."Active";

    -- D: in review, AI proposals untouched
    INSERT INTO "Notes" ("PatientId","ProviderId","Status","InputMode","Format","RawTranscript","StructureStatus","CreatedAt","UpdatedAt")
    VALUES (t.patient_id, t.provider_id, 'ReadyForReview', 'Text', 'DAP',
      'Chegou ansioso, com taquicardia antes de uma apresentação. Discussões frequentes com os pais sobre a mudança de cidade.',
      'done', now() - interval '1 hour', now() - interval '1 hour')
    RETURNING "Id" INTO note_id;
    INSERT INTO "NoteSections" ("NoteId","Key","Content","Position","GeneratedAt") VALUES
      (note_id, 'data', 'Chegou ansioso, relatando taquicardia antes de uma apresentação importante. Conta que tem discutido com frequência com os pais sobre a mudança de cidade.', 0, now() - interval '1 hour'),
      (note_id, 'assessment', '', 1, now() - interval '1 hour'),
      (note_id, 'plan', 'Treino de respiração; retomar o tema familiar na próxima sessão.', 2, now() - interval '1 hour');
    INSERT INTO "ThemeTags" ("NoteId","TermId","CreatedBy","CreatedAt")
    SELECT note_id, tt."Id", 'ai', now() - interval '1 hour'
    FROM (VALUES ('ansiedade'), ('familia')) AS x(code)
    JOIN "ThemeTerms" tt ON tt."Code" = x.code
    JOIN "ThemeVocabularies" v ON v."Id" = tt."VocabularyId" AND v."Active";

    -- E: signed, no tags (for "Sugerir temas")
    INSERT INTO "Notes" ("PatientId","ProviderId","Status","InputMode","Format","RawTranscript","SignedAt","SignedByCrp","StructureStatus","CreatedAt","UpdatedAt")
    VALUES (t.patient_id, t.provider_id, 'Signed', 'Text', 'DAP',
      'Mudou-se de país há seis meses. Sente saudade de casa e tem dificuldade com o idioma no novo emprego.',
      now() - interval '5 days', crp, 'done', now() - interval '5 days', now() - interval '5 days')
    RETURNING "Id" INTO note_id;
    INSERT INTO "NoteSections" ("NoteId","Key","Content","Position","GeneratedAt") VALUES
      (note_id, 'data', 'Mudou-se de país há seis meses. Relata saudade de casa e dificuldade com o idioma no novo emprego.', 0, now() - interval '5 days'),
      (note_id, 'assessment', 'Adaptação ao novo país em curso.', 1, now() - interval '5 days'),
      (note_id, 'plan', 'Mapear rede de apoio local.', 2, now() - interval '5 days');
  END LOOP;
END $$;

COMMIT;
