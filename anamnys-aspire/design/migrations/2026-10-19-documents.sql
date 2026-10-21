-- Psychological documents (design/specs/2026-10-19-psychological-documents-design.md). Run after
-- 2026-10-18-scales.sql. Idempotent, and all-or-nothing.
--
--   docker exec -i <postgres container> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d anamnysdb -v ON_ERROR_STOP=1' < design/migrations/2026-10-19-documents.sql

BEGIN;

ALTER TABLE "Providers" ADD COLUMN IF NOT EXISTS "PracticeCity" text;
ALTER TABLE "Providers" ADD COLUMN IF NOT EXISTS "DocumentCheckMode" text DEFAULT 'blocking' NOT NULL;
ALTER TABLE "Providers" DROP CONSTRAINT IF EXISTS "Providers_DocumentCheckMode_ck";
ALTER TABLE "Providers" ADD CONSTRAINT "Providers_DocumentCheckMode_ck" CHECK (("DocumentCheckMode" = ANY (ARRAY['blocking'::text, 'advisory'::text])));

ALTER TABLE "ClinicalDocuments" ADD COLUMN IF NOT EXISTS "Fields" jsonb DEFAULT '{}' NOT NULL;
ALTER TABLE "ClinicalDocuments" ADD COLUMN IF NOT EXISTS "Confirmations" jsonb DEFAULT '[]' NOT NULL;
ALTER TABLE "ClinicalDocuments" ADD COLUMN IF NOT EXISTS "RulesetId" uuid;
ALTER TABLE "ClinicalDocuments" ADD COLUMN IF NOT EXISTS "SignedByCrp" text;
ALTER TABLE "ClinicalDocuments" ADD COLUMN IF NOT EXISTS "SignedCity" text;
ALTER TABLE "ClinicalDocuments" ADD COLUMN IF NOT EXISTS "SignedByName" text;
ALTER TABLE "ClinicalDocuments" ADD COLUMN IF NOT EXISTS "SignedPatientName" text;
ALTER TABLE "ClinicalDocuments" ADD COLUMN IF NOT EXISTS "PdfObjectKey" text;
ALTER TABLE "ClinicalDocuments" ADD COLUMN IF NOT EXISTS "PdfSha256" text;
ALTER TABLE "ClinicalDocuments" ADD COLUMN IF NOT EXISTS "ExportedAt" timestamp with time zone;
ALTER TABLE "ClinicalDocuments" ADD COLUMN IF NOT EXISTS "UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL;
ALTER TABLE "ClinicalDocuments" DROP CONSTRAINT IF EXISTS "ClinicalDocuments_RulesetId_fkey";
ALTER TABLE "ClinicalDocuments" ADD CONSTRAINT "ClinicalDocuments_RulesetId_fkey" FOREIGN KEY ("RulesetId") REFERENCES "ConformityRulesets"("Id") ON DELETE RESTRICT;
-- Documents signed before the snapshot existed keep the best available value (current profile/patient).
UPDATE "ClinicalDocuments" d SET
  "SignedCity" = COALESCE(d."SignedCity", NULLIF(btrim(COALESCE((SELECT p."PracticeCity" FROM "Providers" p WHERE p."Id" = d."ProviderId"), '')), ''), '—'),
  "SignedByName" = COALESCE(d."SignedByName", (SELECT p."Name" FROM "Providers" p WHERE p."Id" = d."ProviderId"), '—'),
  "SignedPatientName" = COALESCE(d."SignedPatientName", (SELECT btrim(pa."FirstName" || ' ' || pa."LastName") FROM "Patients" pa WHERE pa."Id" = d."PatientId"), '—')
WHERE d."SignedAt" IS NOT NULL;
ALTER TABLE "ClinicalDocuments" DROP CONSTRAINT IF EXISTS "ClinicalDocuments_Signed_ck";
ALTER TABLE "ClinicalDocuments" ADD CONSTRAINT "ClinicalDocuments_Signed_ck" CHECK ((("SignedAt" IS NULL) = ("SignedByCrp" IS NULL) AND ("SignedAt" IS NULL) = ("SignedCity" IS NULL) AND ("SignedAt" IS NULL) = ("SignedByName" IS NULL) AND ("SignedAt" IS NULL) = ("SignedPatientName" IS NULL)));
ALTER TABLE "ClinicalDocuments" DROP CONSTRAINT IF EXISTS "ClinicalDocuments_Pdf_ck";
ALTER TABLE "ClinicalDocuments" ADD CONSTRAINT "ClinicalDocuments_Pdf_ck" CHECK ((("PdfObjectKey" IS NULL AND "PdfSha256" IS NULL AND "ExportedAt" IS NULL) OR ("PdfObjectKey" IS NOT NULL AND "PdfSha256" IS NOT NULL AND "ExportedAt" IS NOT NULL AND "SignedAt" IS NOT NULL)));

-- Conformity rulesets, Res. CFP 006/2019 (seed). Re-running keeps rows already present.
insert into "ConformityRulesets"
  ("DocumentKind","Version","ArticleRef","Elements","Prohibitions","EffectiveFrom") values

  -- DECLARAÇÃO — registra prestação de serviço, e nada além disso.
  -- Note que a norma NÃO pede identificação do solicitante aqui: a declaração
  -- é sobre quem foi atendido.
  ('declaracao', 'cfp-006-2019.1', 'Art. 9º, §§1º e 2º; Art. 5º §8º; Art. 17',
   '["titulo_declaracao",
     "nome_pessoa_atendida",
     "finalidade",
     "local_dias_horarios_e_duracao_do_acompanhamento",
     "local_emissao",
     "data_emissao",
     "carimbo",
     "nome_psicologo",
     "inscricao_crp",
     "assinatura",
     "laudas_numeradas_rubricadas",
     "prazo_validade"]',
   -- Art. 9º §1º. A vedação é o coração da declaração: é o que a separa do
   -- atestado. Documento que descreve estado psicológico não é declaração.
   '["sintomas_situacoes_ou_estados_psicologicos"]',
   '2019-06-01'),

  -- ATESTADO PSICOLÓGICO — certifica situação, estado ou funcionamento
  -- psicológico com base em avaliação psicológica.
  ('atestado', 'cfp-006-2019.1', 'Art. 10, §§5º e 6º; Art. 5º §8º; Art. 17',
   '["titulo_atestado_psicologico",
     "nome_pessoa_ou_instituicao_atendida",
     "nome_solicitante",
     "finalidade",
     "descricao_condicoes_psicologicas",
     "local_emissao",
     "data_emissao",
     "carimbo",
     "nome_psicologo",
     "inscricao_crp",
     "laudas_numeradas_rubricadas",
     "assinatura",
     "prazo_validade"]',
   -- Art. 10 §5º: texto corrido, separado só por pontuação, sem parágrafos,
   -- para evitar adulteração; havendo parágrafo, preencher com traços. E o
   -- documento restringe-se à informação solicitada.
   '["quebra_de_paragrafo_sem_preenchimento",
     "informacao_alem_do_solicitado"]',
   '2019-06-01'),

  -- RELATÓRIO PSICOLÓGICO — cinco itens. Sem referências obrigatórias.
  ('relatorio', 'cfp-006-2019.1', 'Art. 11, §§1º a 6º; Art. 5º §8º; Art. 17',
   '["identificacao",
     "titulo_relatorio_psicologico",
     "nome_pessoa_ou_instituicao_atendida",
     "nome_solicitante",
     "finalidade",
     "nome_autor_e_inscricao_crp",
     "descricao_demanda",
     "procedimento",
     "referencial_teorico",
     "pessoas_ouvidas_numero_de_encontros_e_duracao",
     "analise",
     "conclusao",
     "local_emissao",
     "data_emissao",
     "carimbo",
     "nome_psicologo",
     "inscricao_crp",
     "laudas_numeradas_rubricadas",
     "assinatura",
     "prazo_validade"]',
   -- Art. 11 §5º III e Art. 6º §5º / Art. 11 III.
   '["afirmacao_sem_fonte_ou_sustentacao",
     "descricao_literal_das_sessoes"]',
   '2019-06-01'),

  -- RELATÓRIO MULTIPROFISSIONAL — modalidade própria do Art. 12, não é uma
  -- variação do relatório psicológico: a autoria é coletiva, os procedimentos
  -- privativos da Psicologia vêm separados e cada profissional assina a sua
  -- análise. Faltava inteiramente no rascunho anterior.
  ('relatorio_multiprofissional', 'cfp-006-2019.1', 'Art. 12, §§1º a 9º; Art. 5º §8º; Art. 17',
   '["identificacao",
     "titulo_relatorio_multiprofissional",
     "nome_pessoa_ou_instituicao_atendida",
     "nome_solicitante",
     "finalidade",
     "nomes_autores_categorias_e_registros_profissionais",
     "descricao_demanda",
     "procedimento",
     "procedimentos_privativos_da_psicologia_em_secao_separada",
     "analise",
     "analise_identificada_por_profissional_e_categoria",
     "conclusao",
     "local_emissao",
     "data_emissao",
     "carimbo",
     "nomes_profissionais",
     "inscricoes_profissionais",
     "laudas_numeradas_rubricadas",
     "assinatura_do_psicologo",
     "prazo_validade"]',
   -- Art. 12 §7º remete ao Art. 11 §5º, então as vedações do relatório valem.
   '["afirmacao_sem_fonte_ou_sustentacao",
     "descricao_literal_das_sessoes"]',
   '2019-06-01'),

  -- LAUDO PSICOLÓGICO — seis itens, obrigatoriamente em forma de itens, e
  -- referências obrigatórias. Não existe item "metodologia": o referencial
  -- teórico-metodológico é conteúdo do Procedimento (Art. 13 §4º).
  ('laudo', 'cfp-006-2019.1', 'Art. 13, §§1º a 7º; Art. 5º §8º; Art. 17',
   '["secoes_em_forma_de_itens",
     "identificacao",
     "titulo_laudo_psicologico",
     "nome_pessoa_ou_instituicao_atendida",
     "nome_solicitante",
     "finalidade",
     "nome_autor_e_inscricao_crp",
     "descricao_demanda",
     "procedimento",
     "referencial_teorico_metodologico",
     "pessoas_ouvidas_numero_de_encontros_e_duracao",
     "analise",
     "conclusao",
     "local_emissao",
     "data_emissao",
     "carimbo",
     "nome_psicologo",
     "inscricao_crp",
     "laudas_numeradas_rubricadas",
     "assinatura",
     "referencias",
     "prazo_validade"]',
   -- Art. 13 §5º I e III.
   '["descricao_literal_das_sessoes",
     "afirmacao_sem_fonte_ou_sustentacao"]',
   '2019-06-01'),

  -- PARECER PSICOLÓGICO — cinco itens, SEM procedimento, com referências
  -- obrigatórias e com exigência extra na identificação: a titulação que
  -- comprove conhecimento específico no assunto (Art. 14 §2º V).
  ('parecer', 'cfp-006-2019.1', 'Art. 14, §§1º a 6º; Art. 5º §8º; Art. 17',
   '["secoes_em_forma_de_itens",
     "identificacao",
     "titulo_parecer_psicologico",
     "nome_pessoa_ou_instituicao_objeto_do_questionamento",
     "nome_solicitante",
     "finalidade",
     "nome_autor_inscricao_crp_e_titulacao",
     "descricao_demanda",
     "analise",
     "conclusao",
     "local_emissao",
     "data_emissao",
     "carimbo",
     "nome_psicologo",
     "inscricao_crp",
     "laudas_numeradas_rubricadas",
     "assinatura",
     "referencias",
     "prazo_validade"]',
   -- Art. 14, inciso IV: o parecer NÃO é documento resultante de processo de
   -- avaliação psicológica ou de intervenção psicológica. Parecer que conclui
   -- a partir de avaliação é laudo com o título errado.
   '["resultado_de_avaliacao_ou_intervencao_psicologica"]',
   '2019-06-01')

on conflict ("DocumentKind","Version") do nothing;

COMMIT;
