-- Dev-only clinical sample data: diagnoses, medications and treatment-plan objectives
-- for every non-archived patient of a non-test provider (emails not ending in @test.local).
-- Idempotent: each category is filled only for patients that have none of it yet.
-- Never run outside a local development database.
--
-- Run against the Aspire-managed Postgres container (name changes per run, see `docker ps`):
--   docker exec -i <postgres-container> sh -c 'PGPASSWORD=$POSTGRES_PASSWORD psql -v ON_ERROR_STOP=1 -U postgres -d anamnysdb' < dev-seed/clinical-records.sql

BEGIN;

CREATE TEMP TABLE targets ON COMMIT DROP AS
SELECT p."Id" AS patient_id,
       (row_number() OVER (ORDER BY p."LastName", p."FirstName", p."Id") - 1) AS rn
FROM "Patients" p
JOIN "Providers" pr ON pr."Id" = p."ProviderId"
WHERE pr."Email" NOT LIKE '%@test.local'
  AND p."ArchivedAt" IS NULL;

CREATE TEMP TABLE dx (profile int, ord int, description text, icd text, months_ago int, resolved_months_ago int) ON COMMIT DROP;
INSERT INTO dx VALUES
 (0, 0, 'Transtorno de ansiedade generalizada', 'F41.1', 8, NULL),
 (1, 0, 'Transtorno depressivo recorrente, episódio atual moderado', 'F33.1', 14, NULL),
 (1, 1, 'Insônia não-orgânica', 'F51.0', 6, NULL),
 (2, 0, 'Transtorno de pânico', 'F41.0', 10, NULL),
 (2, 1, 'Agorafobia', 'F40.0', 10, 3),
 (3, 0, 'Transtorno de déficit de atenção e hiperatividade', 'F90.0', 18, NULL),
 (4, 0, 'Transtorno afetivo bipolar, episódio atual depressivo leve ou moderado', 'F31.3', 24, NULL),
 (5, 0, 'Transtorno de estresse pós-traumático', 'F43.1', 7, NULL),
 (6, 0, 'Transtorno de adaptação (luto)', 'F43.2', 12, 2),
 (6, 1, 'Episódio depressivo leve', 'F32.0', 5, NULL),
 (7, 0, 'Transtorno obsessivo-compulsivo, forma mista', 'F42.2', 16, NULL),
 (8, 0, 'Fobia social', 'F40.1', 9, NULL),
 (9, 0, 'Distimia', 'F34.1', 20, NULL),
 (9, 1, 'Dependência de nicotina', 'F17.2', 20, NULL);

CREATE TEMP TABLE med (profile int, drug text, dose text, posology text, months_ago int, ended_months_ago int) ON COMMIT DROP;
INSERT INTO med VALUES
 (0, 'Escitalopram', '10mg', '1x/dia pela manhã', 7, NULL),
 (1, 'Sertralina', '100mg', '1x/dia pela manhã', 13, NULL),
 (1, 'Trazodona', '50mg', '1x/dia ao deitar', 5, NULL),
 (2, 'Paroxetina', '20mg', '1x/dia pela manhã', 9, NULL),
 (2, 'Clonazepam', '0,5mg', 'Se necessário, até 1x/dia', 9, 4),
 (3, 'Metilfenidato', '10mg', '2x/dia (manhã e almoço)', 17, NULL),
 (4, 'Carbonato de lítio', '300mg', '2x/dia', 23, NULL),
 (4, 'Quetiapina', '25mg', '1x/dia ao deitar', 6, NULL),
 (5, 'Sertralina', '50mg', '1x/dia pela manhã', 6, NULL),
 (5, 'Prazosina', '1mg', '1x/dia ao deitar', 4, NULL),
 (7, 'Fluoxetina', '40mg', '1x/dia pela manhã', 15, NULL),
 (8, 'Venlafaxina', '75mg', '1x/dia', 8, NULL),
 (9, 'Bupropiona', '150mg', '2x/dia', 19, NULL),
 (9, 'Adesivo de nicotina', '21mg', '1 adesivo/dia', 19, 16);

CREATE TEMP TABLE obj (profile int, ord int, description text) ON COMMIT DROP;
INSERT INTO obj VALUES
 (0, 0, 'Reduzir pontuação GAD-7 para abaixo de 10'),
 (0, 1, 'Praticar técnica de respiração diafragmática diariamente'),
 (0, 2, 'Identificar e registrar pensamentos catastróficos'),
 (1, 0, 'Reduzir pontuação PHQ-9 em 50%'),
 (1, 1, 'Retomar ao menos duas atividades prazerosas por semana'),
 (1, 2, 'Dormir 7h por noite com horário regular'),
 (2, 0, 'Reduzir frequência de crises de pânico para menos de uma por mês'),
 (2, 1, 'Utilizar transporte público sozinho(a) com ansiedade tolerável'),
 (2, 2, 'Aplicar técnicas de exposição interoceptiva'),
 (3, 0, 'Estabelecer rotina de organização com agenda diária'),
 (3, 1, 'Concluir tarefas de trabalho dentro do prazo'),
 (3, 2, 'Reduzir procrastinação com técnica de blocos de tempo'),
 (4, 0, 'Manter adesão à medicação estabilizadora'),
 (4, 1, 'Identificar sinais precoces de mudança de humor'),
 (4, 2, 'Manter registro diário de humor e sono'),
 (5, 0, 'Reduzir frequência de pesadelos e flashbacks'),
 (5, 1, 'Retomar atividades evitadas desde o evento traumático'),
 (5, 2, 'Desenvolver estratégias de autorregulação emocional'),
 (6, 0, 'Elaborar o processo de luto'),
 (6, 1, 'Fortalecer rede de apoio social'),
 (6, 2, 'Retomar rotina de trabalho e autocuidado'),
 (7, 0, 'Reduzir tempo diário gasto com rituais compulsivos'),
 (7, 1, 'Realizar exercícios de exposição com prevenção de resposta'),
 (7, 2, 'Reduzir pontuação Y-BOCS para faixa leve'),
 (8, 0, 'Participar de reuniões de trabalho expressando opinião'),
 (8, 1, 'Iniciar conversas em situações sociais semanalmente'),
 (8, 2, 'Reduzir comportamentos de segurança em situações sociais'),
 (9, 0, 'Manter abstinência de tabaco'),
 (9, 1, 'Aumentar engajamento em atividades físicas'),
 (9, 2, 'Reestruturar crenças negativas sobre si mesmo(a)');

INSERT INTO "PatientDiagnoses" ("PatientId", "Description", "IcdCode", "RecordedAt", "ResolvedOn")
SELECT t.patient_id, d.description, d.icd,
       now() - make_interval(months => d.months_ago, days => (t.rn % 20)::int),
       CASE WHEN d.resolved_months_ago IS NULL THEN NULL
            ELSE (current_date - make_interval(months => d.resolved_months_ago))::date END
FROM targets t
JOIN dx d ON d.profile = t.rn % 10
WHERE NOT EXISTS (SELECT 1 FROM "PatientDiagnoses" x WHERE x."PatientId" = t.patient_id);

INSERT INTO "MedicationEntries" ("PatientId", "Drug", "Dose", "Posology", "StartedOn", "EndedOn")
SELECT t.patient_id, m.drug, m.dose, m.posology,
       (current_date - make_interval(months => m.months_ago, days => (t.rn % 20)::int))::date,
       CASE WHEN m.ended_months_ago IS NULL THEN NULL
            ELSE (current_date - make_interval(months => m.ended_months_ago))::date END
FROM targets t
JOIN med m ON m.profile = t.rn % 10
WHERE NOT EXISTS (SELECT 1 FROM "MedicationEntries" x WHERE x."PatientId" = t.patient_id);

INSERT INTO "TreatmentPlans" ("PatientId", "CreatedAt")
SELECT t.patient_id, now() - make_interval(weeks => (t.rn % 6 + 1)::int)
FROM targets t
WHERE NOT EXISTS (SELECT 1 FROM "TreatmentPlans" x WHERE x."PatientId" = t.patient_id AND x."ClosedAt" IS NULL);

INSERT INTO "PlanObjectives" ("PlanId", "Description", "CreatedAt")
SELECT plan."Id", o.description, plan."CreatedAt" + make_interval(secs => o.ord)
FROM targets t
CROSS JOIN LATERAL (
    SELECT tp."Id", tp."CreatedAt" FROM "TreatmentPlans" tp
    WHERE tp."PatientId" = t.patient_id AND tp."ClosedAt" IS NULL
    ORDER BY tp."CreatedAt" DESC LIMIT 1) plan
JOIN obj o ON o.profile = t.rn % 10
WHERE NOT EXISTS (SELECT 1 FROM "PlanObjectives" x WHERE x."PlanId" = plan."Id");

SELECT (SELECT count(*) FROM targets) AS patients,
       (SELECT count(*) FROM "PatientDiagnoses" d JOIN targets t ON t.patient_id = d."PatientId") AS diagnoses,
       (SELECT count(*) FROM "MedicationEntries" m JOIN targets t ON t.patient_id = m."PatientId") AS medications,
       (SELECT count(*) FROM "PlanObjectives" o JOIN "TreatmentPlans" tp ON tp."Id" = o."PlanId" JOIN targets t ON t.patient_id = tp."PatientId") AS objectives;

COMMIT;
