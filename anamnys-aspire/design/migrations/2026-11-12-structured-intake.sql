-- F-31: structured intake (anamnese) (design/specs/2026-11-12-structured-intake-design.md, section 2).
-- IntakeTemplates, IntakeQuestions and IntakeResponses existed as schema only. This makes IntakeTemplates.ProviderId
-- nullable (null only for the system default) and adds IsDefault (one at most), BasedOnTemplateId and ArchivedAt;
-- adds the question's section, help text, choices and Disabled, with the answer-type, choices and destination checks;
-- links responses to a new Intakes row (one per intake and question); adds IntakeTransfers (answers waiting to be taken
-- into the record), ProviderIntakeSettings (the provider's preferred template), the Anamnese note format and
-- Notes.IntakeId; refuses new intakes of a discarded record; adds the audit actions intake_started, intake_signed,
-- intake_transfer_confirmed, intake_transfer_ignored, intake_template_adapted, intake_discarded and intake_answers_unlocked; adds MedicationEntries.SourceNoteId; and seeds the default template
-- "Anamnese padrão" (version 1, six sections). Responses written before this file (no code ever wrote one) have no
-- intake and are removed. Idempotent; all or nothing.
BEGIN;

-- Templates.
ALTER TABLE "IntakeTemplates" ALTER COLUMN "ProviderId" DROP NOT NULL;
ALTER TABLE "IntakeTemplates" ADD COLUMN IF NOT EXISTS "IsDefault" boolean DEFAULT false NOT NULL;
ALTER TABLE "IntakeTemplates" ADD COLUMN IF NOT EXISTS "BasedOnTemplateId" uuid;
ALTER TABLE "IntakeTemplates" ADD COLUMN IF NOT EXISTS "ArchivedAt" timestamp with time zone;
ALTER TABLE "IntakeTemplates" DROP CONSTRAINT IF EXISTS "IntakeTemplates_BasedOnTemplateId_fkey";
ALTER TABLE "IntakeTemplates" ADD CONSTRAINT "IntakeTemplates_BasedOnTemplateId_fkey"
    FOREIGN KEY ("BasedOnTemplateId") REFERENCES "IntakeTemplates"("Id") ON DELETE SET NULL;
ALTER TABLE "IntakeTemplates" DROP CONSTRAINT IF EXISTS "IntakeTemplates_Owner_ck";
ALTER TABLE "IntakeTemplates" ADD CONSTRAINT "IntakeTemplates_Owner_ck" CHECK (("IsDefault" = ("ProviderId" IS NULL)));
CREATE UNIQUE INDEX IF NOT EXISTS "IntakeTemplates_OneDefault_key" ON "IntakeTemplates" ("IsDefault") WHERE "IsDefault";

-- Questions.
ALTER TABLE "IntakeQuestions" ADD COLUMN IF NOT EXISTS "SectionKey" text;
ALTER TABLE "IntakeQuestions" ADD COLUMN IF NOT EXISTS "SectionTitle" text;
ALTER TABLE "IntakeQuestions" ADD COLUMN IF NOT EXISTS "HelpText" text;
ALTER TABLE "IntakeQuestions" ADD COLUMN IF NOT EXISTS "Choices" text[];
ALTER TABLE "IntakeQuestions" ADD COLUMN IF NOT EXISTS "Disabled" boolean DEFAULT false NOT NULL;
UPDATE "IntakeQuestions" SET "SectionKey" = 'geral' WHERE "SectionKey" IS NULL;
UPDATE "IntakeQuestions" SET "SectionTitle" = 'Geral' WHERE "SectionTitle" IS NULL;
UPDATE "IntakeQuestions" SET "TargetField" = NULL
 WHERE "TargetField" IS NOT NULL
   AND "TargetField" <> ALL (ARRAY['patient.dateOfBirth', 'patient.phone', 'patient.contactEmail', 'medication', 'diagnosis', 'plan.objective']);
UPDATE "IntakeQuestions" SET "AnswerType" = 'text'
 WHERE "AnswerType" = 'choice' AND coalesce(cardinality("Choices"), 0) = 0;
ALTER TABLE "IntakeQuestions" ALTER COLUMN "SectionKey" SET NOT NULL;
ALTER TABLE "IntakeQuestions" ALTER COLUMN "SectionTitle" SET NOT NULL;
ALTER TABLE "IntakeQuestions" DROP CONSTRAINT IF EXISTS "IntakeQuestions_TargetField_ck";
ALTER TABLE "IntakeQuestions" ADD CONSTRAINT "IntakeQuestions_TargetField_ck" CHECK ((("TargetField" IS NULL) OR ("TargetField" = ANY (ARRAY['patient.dateOfBirth'::text, 'patient.phone'::text, 'patient.contactEmail'::text, 'medication'::text, 'diagnosis'::text, 'plan.objective'::text]))));
ALTER TABLE "IntakeQuestions" DROP CONSTRAINT IF EXISTS "IntakeQuestions_Choices_ck";
ALTER TABLE "IntakeQuestions" ADD CONSTRAINT "IntakeQuestions_Choices_ck" CHECK ((("AnswerType" <> 'choice'::text) OR (coalesce(cardinality("Choices"), 0) > 0)));
CREATE INDEX IF NOT EXISTS "IntakeQuestions_Template_Position_idx" ON "IntakeQuestions" ("TemplateId", "Position");

-- The provider's preferred template; no row means the system default.
CREATE TABLE IF NOT EXISTS "ProviderIntakeSettings" (
	"ProviderId" uuid PRIMARY KEY,
	"DefaultTemplateId" uuid NOT NULL
);
ALTER TABLE "ProviderIntakeSettings" DROP CONSTRAINT IF EXISTS "ProviderIntakeSettings_ProviderId_fkey";
ALTER TABLE "ProviderIntakeSettings" ADD CONSTRAINT "ProviderIntakeSettings_ProviderId_fkey"
    FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "ProviderIntakeSettings" DROP CONSTRAINT IF EXISTS "ProviderIntakeSettings_DefaultTemplateId_fkey";
ALTER TABLE "ProviderIntakeSettings" ADD CONSTRAINT "ProviderIntakeSettings_DefaultTemplateId_fkey"
    FOREIGN KEY ("DefaultTemplateId") REFERENCES "IntakeTemplates"("Id") ON DELETE CASCADE;

-- One intake (first session or re-assessment) of a patient; at most one draft per patient.
CREATE TABLE IF NOT EXISTS "Intakes" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"PatientId" uuid NOT NULL,
	"TemplateId" uuid NOT NULL,
	"NoteId" uuid,
	"Status" text DEFAULT 'draft' NOT NULL,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"SignedAt" timestamp with time zone
);
ALTER TABLE "Intakes" DROP CONSTRAINT IF EXISTS "Intakes_Status_ck";
ALTER TABLE "Intakes" ADD CONSTRAINT "Intakes_Status_ck" CHECK (("Status" = ANY (ARRAY['draft'::text, 'signed'::text])));
ALTER TABLE "Intakes" DROP CONSTRAINT IF EXISTS "Intakes_Signed_ck";
ALTER TABLE "Intakes" ADD CONSTRAINT "Intakes_Signed_ck" CHECK ((("Status" = 'signed'::text) = ("SignedAt" IS NOT NULL)));
ALTER TABLE "Intakes" DROP CONSTRAINT IF EXISTS "Intakes_Patient_fk";
ALTER TABLE "Intakes" ADD CONSTRAINT "Intakes_Patient_fk"
    FOREIGN KEY ("PatientId", "ProviderId") REFERENCES "Patients"("Id", "ProviderId") ON DELETE CASCADE;
ALTER TABLE "Intakes" DROP CONSTRAINT IF EXISTS "Intakes_ProviderId_fkey";
ALTER TABLE "Intakes" ADD CONSTRAINT "Intakes_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "Intakes" DROP CONSTRAINT IF EXISTS "Intakes_TemplateId_fkey";
ALTER TABLE "Intakes" ADD CONSTRAINT "Intakes_TemplateId_fkey" FOREIGN KEY ("TemplateId") REFERENCES "IntakeTemplates"("Id");
ALTER TABLE "Intakes" DROP CONSTRAINT IF EXISTS "Intakes_NoteId_fkey";
ALTER TABLE "Intakes" ADD CONSTRAINT "Intakes_NoteId_fkey" FOREIGN KEY ("NoteId") REFERENCES "Notes"("Id") ON DELETE SET NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "Intakes_OneDraft_key" ON "Intakes" ("PatientId") WHERE ("Status" = 'draft'::text);
CREATE INDEX IF NOT EXISTS "Intakes_Patient_Created_idx" ON "Intakes" ("PatientId", "CreatedAt" DESC);
CREATE INDEX IF NOT EXISTS "Intakes_TemplateId_idx" ON "Intakes" ("TemplateId");

-- Responses belong to an intake.
ALTER TABLE "IntakeResponses" ADD COLUMN IF NOT EXISTS "IntakeId" uuid;
ALTER TABLE "IntakeResponses" ADD COLUMN IF NOT EXISTS "UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL;
DELETE FROM "IntakeResponses" WHERE "IntakeId" IS NULL;
ALTER TABLE "IntakeResponses" ALTER COLUMN "IntakeId" SET NOT NULL;
ALTER TABLE "IntakeResponses" DROP CONSTRAINT IF EXISTS "IntakeResponses_IntakeId_fkey";
ALTER TABLE "IntakeResponses" ADD CONSTRAINT "IntakeResponses_IntakeId_fkey"
    FOREIGN KEY ("IntakeId") REFERENCES "Intakes"("Id") ON DELETE CASCADE;
ALTER TABLE "IntakeResponses" DROP CONSTRAINT IF EXISTS "IntakeResponses_Intake_Question_key";
ALTER TABLE "IntakeResponses" ADD CONSTRAINT "IntakeResponses_Intake_Question_key" UNIQUE ("IntakeId", "QuestionId");

-- Answers with a record destination, waiting for the provider's decision after signing.
CREATE TABLE IF NOT EXISTS "IntakeTransfers" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"IntakeId" uuid NOT NULL,
	"QuestionId" uuid NOT NULL,
	"TargetField" text NOT NULL,
	"Value" text NOT NULL,
	"Status" text DEFAULT 'pending' NOT NULL,
	"DecidedAt" timestamp with time zone,
	"ResultId" uuid,
	"ExcerptRefId" uuid,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);
ALTER TABLE "IntakeTransfers" DROP CONSTRAINT IF EXISTS "IntakeTransfers_Status_ck";
ALTER TABLE "IntakeTransfers" ADD CONSTRAINT "IntakeTransfers_Status_ck" CHECK (("Status" = ANY (ARRAY['pending'::text, 'confirmed'::text, 'ignored'::text])));
ALTER TABLE "IntakeTransfers" DROP CONSTRAINT IF EXISTS "IntakeTransfers_Decided_ck";
ALTER TABLE "IntakeTransfers" ADD CONSTRAINT "IntakeTransfers_Decided_ck" CHECK ((("Status" = 'pending'::text) = ("DecidedAt" IS NULL)));
ALTER TABLE "IntakeTransfers" DROP CONSTRAINT IF EXISTS "IntakeTransfers_TargetField_ck";
ALTER TABLE "IntakeTransfers" ADD CONSTRAINT "IntakeTransfers_TargetField_ck" CHECK (("TargetField" = ANY (ARRAY['patient.dateOfBirth'::text, 'patient.phone'::text, 'patient.contactEmail'::text, 'medication'::text, 'diagnosis'::text, 'plan.objective'::text])));
ALTER TABLE "IntakeTransfers" DROP CONSTRAINT IF EXISTS "IntakeTransfers_IntakeId_fkey";
ALTER TABLE "IntakeTransfers" ADD CONSTRAINT "IntakeTransfers_IntakeId_fkey" FOREIGN KEY ("IntakeId") REFERENCES "Intakes"("Id") ON DELETE CASCADE;
ALTER TABLE "IntakeTransfers" DROP CONSTRAINT IF EXISTS "IntakeTransfers_QuestionId_fkey";
ALTER TABLE "IntakeTransfers" ADD CONSTRAINT "IntakeTransfers_QuestionId_fkey" FOREIGN KEY ("QuestionId") REFERENCES "IntakeQuestions"("Id");
ALTER TABLE "IntakeTransfers" DROP CONSTRAINT IF EXISTS "IntakeTransfers_ExcerptRefId_fkey";
ALTER TABLE "IntakeTransfers" ADD CONSTRAINT "IntakeTransfers_ExcerptRefId_fkey" FOREIGN KEY ("ExcerptRefId") REFERENCES "ExcerptRefs"("Id") ON DELETE SET NULL;
ALTER TABLE "IntakeTransfers" DROP CONSTRAINT IF EXISTS "IntakeTransfers_Intake_Question_key";
ALTER TABLE "IntakeTransfers" ADD CONSTRAINT "IntakeTransfers_Intake_Question_key" UNIQUE ("IntakeId", "QuestionId");

-- The Anamnese note format and the note's intake.
ALTER TABLE "Notes" DROP CONSTRAINT IF EXISTS "Notes_Format_ck";
ALTER TABLE "Notes" ADD CONSTRAINT "Notes_Format_ck" CHECK (("Format" = ANY (ARRAY['DAP'::text, 'SOAP'::text, 'Biopsychosocial'::text, 'PtFunctional'::text, 'Anamnese'::text])));
ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "IntakeId" uuid;
ALTER TABLE "Notes" DROP CONSTRAINT IF EXISTS "Notes_IntakeId_fkey";
ALTER TABLE "Notes" ADD CONSTRAINT "Notes_IntakeId_fkey" FOREIGN KEY ("IntakeId") REFERENCES "Intakes"("Id") ON DELETE SET NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "Notes_IntakeId_key" ON "Notes" ("IntakeId") WHERE ("IntakeId" IS NOT NULL);

-- A medication taken from an intake answer points at the intake's note (shown as "da anamnese" on the timeline).
ALTER TABLE "MedicationEntries" ADD COLUMN IF NOT EXISTS "SourceNoteId" uuid;
ALTER TABLE "MedicationEntries" DROP CONSTRAINT IF EXISTS "MedicationEntries_SourceNoteId_fkey";
ALTER TABLE "MedicationEntries" ADD CONSTRAINT "MedicationEntries_SourceNoteId_fkey" FOREIGN KEY ("SourceNoteId") REFERENCES "Notes"("Id") ON DELETE SET NULL;
CREATE INDEX IF NOT EXISTS "MedicationEntries_SourceNoteId_idx" ON "MedicationEntries" ("SourceNoteId") WHERE ("SourceNoteId" IS NOT NULL);

-- A discarded record takes no new intake (the function comes from 2026-10-28-retention-panel.sql).
DROP TRIGGER IF EXISTS "Intakes_patient_not_discarded" ON "Intakes";
CREATE TRIGGER "Intakes_patient_not_discarded" BEFORE INSERT ON "Intakes" FOR EACH ROW EXECUTE FUNCTION "Patients_not_discarded"();

-- This file owns the full, current action list: a re-run keeps it as it is.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conname = 'AuditEntries_subject_ck' AND pg_get_constraintdef(oid) LIKE '%intake_answers_unlocked%') THEN
    ALTER TABLE "AuditEntries" DROP CONSTRAINT IF EXISTS "AuditEntries_subject_ck";
    ALTER TABLE "AuditEntries"
      ADD CONSTRAINT "AuditEntries_subject_ck" CHECK ((("NoteId" IS NOT NULL) OR ("DocumentId" IS NOT NULL) OR ("Action" = ANY (ARRAY['account_export_requested'::text, 'account_export_downloaded'::text, 'external_document_uploaded'::text, 'external_document_opened'::text, 'external_document_deleted'::text, 'external_document_blocked'::text, 'fact_confirmed'::text, 'fact_corrected'::text, 'fact_discarded'::text, 'facts_extracted'::text, 'facts_deleted'::text, 'medication_from_fact'::text, 'patient_discarded'::text, 'plan_reviewed'::text, 'follow_up_created'::text, 'follow_up_resolved'::text, 'follow_up_reopened'::text, 'quote_recorded'::text, 'quote_removed'::text, 'disclosure_authorized'::text, 'share_link_created'::text, 'share_link_revoked'::text, 'disclosure_term_opened'::text, 'integrity_report_downloaded'::text, 'integrity_check_failed'::text, 'dossier_requested'::text, 'dossier_downloaded'::text, 'dossier_receipt_downloaded'::text, 'availability_changed'::text, 'block_created'::text, 'block_deleted'::text, 'appointment_outside_availability'::text, 'series_created'::text, 'series_split'::text, 'series_ended'::text, 'intake_started'::text, 'intake_signed'::text, 'intake_transfer_confirmed'::text, 'intake_transfer_ignored'::text, 'intake_template_adapted'::text, 'intake_discarded'::text, 'intake_answers_unlocked'::text]))));
  END IF;
END $$;

-- The default template. Its id is fixed so a re-run finds it; questions are matched by key.
INSERT INTO "IntakeTemplates" ("Id", "ProviderId", "Name", "Version", "Active", "IsDefault", "CreatedAt")
VALUES ('0f310000-0000-4000-8000-000000000001', NULL, 'Anamnese padrão', '1', true, true, now())
ON CONFLICT DO NOTHING;

INSERT INTO "IntakeQuestions" ("TemplateId", "SectionKey", "SectionTitle", "Key", "Label", "HelpText", "AnswerType", "Choices", "Position", "TargetField", "Required")
SELECT '0f310000-0000-4000-8000-000000000001', q.section_key, q.section_title, q.key, q.label, q.help, q.answer_type, q.choices, q.position, q.target, q.required
  FROM (VALUES
    ('identificacao', 'Identificação', 'data_nascimento', 'Data de nascimento', NULL, 'date', NULL::text[], 0, 'patient.dateOfBirth', false),
    ('identificacao', 'Identificação', 'telefone', 'Telefone', 'Com DDD.', 'text', NULL, 1, 'patient.phone', false),
    ('identificacao', 'Identificação', 'email', 'E-mail', NULL, 'text', NULL, 2, 'patient.contactEmail', false),
    ('identificacao', 'Identificação', 'estado_civil', 'Estado civil', NULL, 'choice',
        ARRAY['Solteiro(a)', 'Casado(a) ou em união estável', 'Separado(a) ou divorciado(a)', 'Viúvo(a)', 'Outro'], 3, NULL, false),
    ('identificacao', 'Identificação', 'escolaridade', 'Escolaridade', NULL, 'choice',
        ARRAY['Fundamental incompleto', 'Fundamental completo', 'Médio completo', 'Superior incompleto', 'Superior completo', 'Pós-graduação'], 4, NULL, false),
    ('identificacao', 'Identificação', 'ocupacao', 'Ocupação ou profissão', NULL, 'text', NULL, 5, NULL, false),
    ('identificacao', 'Identificação', 'encaminhamento', 'Como chegou ao atendimento', NULL, 'choice',
        ARRAY['Indicação de conhecido', 'Encaminhamento médico', 'Encaminhamento de outro profissional', 'Busca própria', 'Convênio', 'Outro'], 6, NULL, false),
    ('queixa', 'Queixa principal', 'queixa_principal', 'Queixa principal', 'Nas palavras do paciente.', 'text', NULL, 7, NULL, true),
    ('queixa', 'Queixa principal', 'inicio', 'Quando começou', 'Início dos sintomas ou da dificuldade.', 'text', NULL, 8, NULL, false),
    ('queixa', 'Queixa principal', 'desencadeantes', 'Fatores desencadeantes ou situações associadas', NULL, 'text', NULL, 9, NULL, false),
    ('queixa', 'Queixa principal', 'intensidade', 'Intensidade do sofrimento atual (0 a 10)', NULL, 'number', NULL, 10, NULL, false),
    ('queixa', 'Queixa principal', 'impacto', 'Impacto no dia a dia', 'Trabalho, estudos, relações, autocuidado.', 'text', NULL, 11, NULL, false),
    ('queixa', 'Queixa principal', 'tentativas', 'O que já tentou para lidar com isso', NULL, 'text', NULL, 12, NULL, false),
    ('historia', 'História pessoal e familiar', 'desenvolvimento', 'Infância e desenvolvimento', NULL, 'text', NULL, 13, NULL, false),
    ('historia', 'História pessoal e familiar', 'composicao_familiar', 'Composição familiar e com quem mora', NULL, 'text', NULL, 14, NULL, false),
    ('historia', 'História pessoal e familiar', 'relacoes_familiares', 'Relações familiares', NULL, 'text', NULL, 15, NULL, false),
    ('historia', 'História pessoal e familiar', 'historico_familiar', 'Histórico de saúde mental na família', NULL, 'text', NULL, 16, NULL, false),
    ('historia', 'História pessoal e familiar', 'eventos_marcantes', 'Eventos marcantes ou traumáticos', NULL, 'text', NULL, 17, NULL, false),
    ('historia', 'História pessoal e familiar', 'tratamento_anterior', 'Já fez psicoterapia ou tratamento psiquiátrico', NULL, 'boolean', NULL, 18, NULL, false),
    ('historia', 'História pessoal e familiar', 'tratamento_anterior_detalhes', 'Tratamentos anteriores', 'Quando, por quanto tempo, como terminou.', 'text', NULL, 19, NULL, false),
    ('saude', 'Saúde', 'diagnosticos_previos', 'Diagnósticos prévios', 'Psiquiátricos ou clínicos.', 'text', NULL, 20, 'diagnosis', false),
    ('saude', 'Saúde', 'medicacoes', 'Medicações em uso', 'Nome, dose e posologia, se souber.', 'text', NULL, 21, 'medication', false),
    ('saude', 'Saúde', 'condicoes_clinicas', 'Condições clínicas relevantes', NULL, 'text', NULL, 22, NULL, false),
    ('saude', 'Saúde', 'sono', 'Sono', NULL, 'choice',
        ARRAY['Dorme bem', 'Dificuldade para pegar no sono', 'Acorda durante a noite', 'Acorda cedo demais', 'Dorme demais'], 23, NULL, false),
    ('saude', 'Saúde', 'alimentacao', 'Alimentação e apetite', NULL, 'text', NULL, 24, NULL, false),
    ('saude', 'Saúde', 'substancias', 'Uso de álcool, tabaco ou outras substâncias', NULL, 'text', NULL, 25, NULL, false),
    ('saude', 'Saúde', 'atividade_fisica', 'Atividade física', NULL, 'text', NULL, 26, NULL, false),
    ('saude', 'Saúde', 'risco', 'Ideação suicida ou autolesão', 'Atual ou passada.', 'choice',
        ARRAY['Nunca', 'No passado', 'Atualmente'], 27, NULL, true),
    ('contexto', 'Contexto atual', 'trabalho_estudos', 'Trabalho ou estudos', NULL, 'text', NULL, 28, NULL, false),
    ('contexto', 'Contexto atual', 'rede_apoio', 'Rede de apoio', 'Com quem pode contar.', 'text', NULL, 29, NULL, false),
    ('contexto', 'Contexto atual', 'relacionamentos', 'Relacionamentos afetivos', NULL, 'text', NULL, 30, NULL, false),
    ('contexto', 'Contexto atual', 'lazer', 'Lazer e interesses', NULL, 'text', NULL, 31, NULL, false),
    ('contexto', 'Contexto atual', 'rotina', 'Rotina diária', NULL, 'text', NULL, 32, NULL, false),
    ('objetivos', 'Objetivos', 'objetivos_terapia', 'O que espera alcançar com a terapia', NULL, 'text', NULL, 33, 'plan.objective', false),
    ('objetivos', 'Objetivos', 'expectativas', 'Expectativas sobre o processo', NULL, 'text', NULL, 34, NULL, false),
    ('objetivos', 'Objetivos', 'disponibilidade', 'Disponibilidade e preferências de horário', NULL, 'text', NULL, 35, NULL, false)
  ) AS q(section_key, section_title, key, label, help, answer_type, choices, position, target, required)
ON CONFLICT ("TemplateId", "Key") DO NOTHING;

COMMIT;
