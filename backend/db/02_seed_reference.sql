-- =============================================================================
--  Anamnys — reference data
--
--  Registries the application reads but does not invent: instruments, the
--  conformity rulesets, the theme and focus vocabularies, and the plans.
--
--  Safe to re-run: every insert is keyed on a natural unique column and does
--  nothing on conflict.
--
--  Run after 01_schema.sql:
--    psql -v ON_ERROR_STOP=1 -d anamnys_dev -f 02_seed_reference.sql
-- =============================================================================

begin;

-- ── instruments (F-01) ───────────────────────────────────────────────────────
-- Score range only. No interpretation bands: recording the number is a record,
-- labelling it "moderate" is clinical interpretation and belongs to the
-- professional. Do not add a severity column here later.
insert into "Instruments" ("Code","Name","MaxScore") values
  ('PHQ-9',  'Patient Health Questionnaire-9',        27),
  ('GAD-7',  'Generalized Anxiety Disorder-7',        21),
  ('BDI-II', 'Beck Depression Inventory II',          63)
on conflict ("Code") do nothing;


-- ── conformity rulesets (F-05) ───────────────────────────────────────────────
-- PLACEHOLDER. These element lists were written from the structure of Res. CFP
-- 006/2019 as generally described, and have NOT been checked line by line
-- against the resolution. Source them from the resolution directly before the
-- validator blocks a signature — the whole selling point of F-05 is that the
-- list is right.
insert into "ConformityRulesets" ("DocumentKind","Version","Elements","EffectiveFrom") values
  ('declaracao','draft-1', '["identificacao_profissional","crp","identificacao_solicitante","finalidade","data","assinatura"]', '2026-01-01'),
  ('atestado',  'draft-1', '["identificacao_profissional","crp","identificacao_atendido","finalidade","data","assinatura"]', '2026-01-01'),
  ('relatorio', 'draft-1', '["identificacao","descricao_demanda","procedimento","analise","conclusao","referencias","data","assinatura"]', '2026-01-01'),
  ('laudo',     'draft-1', '["identificacao","descricao_demanda","procedimento","metodologia","analise","conclusao","referencias","data","assinatura"]', '2026-01-01'),
  ('parecer',   'draft-1', '["identificacao","descricao_demanda","analise","conclusao","referencias","data","assinatura"]', '2026-01-01')
on conflict ("DocumentKind","Version") do nothing;


-- ── theme vocabulary (F-02) ──────────────────────────────────────────────────
-- Mental health only. The per-specialty structure stays in the schema, but no
-- other specialty is seeded while the product is positioned for psychology.
--
-- STARTER SET, not the finished vocabulary. Settle D-01 (how historical series
-- behave when a term is renamed, split or merged) before this ships, because
-- every longitudinal series depends on these codes staying stable.
insert into "ThemeVocabularies" ("Version","PublishedAt","Active")
  values ('mh-1', now(), true)
on conflict ("Version") do nothing;

insert into "ThemeTerms" ("VocabularyId","Code","Label")
select v."Id", t.code, t.label
  from "ThemeVocabularies" v
  cross join (values
    ('sono',            'Sono'),
    ('humor',           'Humor'),
    ('ansiedade',       'Ansiedade'),
    ('trabalho',        'Trabalho'),
    ('familia',         'Família'),
    ('relacionamentos', 'Relacionamentos'),
    ('autocuidado',     'Autocuidado'),
    ('luto',            'Luto'),
    ('imagem_corporal', 'Imagem corporal'),
    ('alimentacao',     'Alimentação'),
    ('uso_substancias', 'Uso de substâncias'),
    ('estudos',         'Estudos'),
    ('financas',        'Finanças'),
    ('sexualidade',     'Sexualidade'),
    ('medicacao',       'Medicação')
  ) as t(code,label)
 where v."Version" = 'mh-1'
on conflict ("VocabularyId","Code") do nothing;


-- ── focus areas (F-64) ───────────────────────────────────────────────────────
-- The directory vocabulary. Separate from the clinical theme vocabulary above
-- on purpose: the audience is different, and so is the wording.
--
-- These are TOPICS TREATED, never specialties. "Especialista" is a regulated
-- term and lives in "SpecialistTitles" with registered evidence. The UI must
-- label this list as topics, not as specialties.
insert into "FocusAreas" ("Code","Label") values
  ('depressao',            'Depressão'),
  ('ansiedade',            'Ansiedade'),
  ('panico',               'Síndrome do pânico'),
  ('transtorno_alimentar', 'Transtornos alimentares'),
  ('luto',                 'Luto'),
  ('burnout',              'Estresse e burnout'),
  ('relacionamentos',      'Relacionamentos'),
  ('familia_casal',        'Família e casal'),
  ('trauma',               'Trauma'),
  ('autoestima',           'Autoestima'),
  ('carreira',             'Carreira e transições'),
  ('maternidade',          'Maternidade e parentalidade'),
  ('adolescencia',         'Adolescência'),
  ('sexualidade',          'Sexualidade'),
  ('uso_substancias',      'Uso de substâncias'),
  ('sono',                 'Dificuldades de sono')
on conflict ("Code") do nothing;


-- ── plans and entitlements (F-50) ────────────────────────────────────────────
-- Plans are DATA. A version is immutable once sold: to change what a plan
-- contains, insert a new version rather than updating this row, so existing
-- subscribers keep what they bought.
--
-- Prices come from specs/Anamnys__Precificacao.pdf. Paddle price ids are left
-- null until D-07 is answered.
insert into "Plans" ("Code","Name","Version") values
  ('essencial',    'Essencial',    1),
  ('profissional', 'Profissional', 1),
  ('completo',     'Completo',     1)
on conflict ("Code","Version") do nothing;

-- Feature keys are referenced BY NAME everywhere. No plan-name comparison is
-- allowed anywhere in the code — that is the whole point of F-50/F-51.
insert into "PlanFeatures" ("PlanId","FeatureKey")
select p."Id", f.key
  from "Plans" p
  join (values
    -- Essencial and up
    ('essencial','dictation'), ('essencial','live_transcription'), ('essencial','typing'),
    ('essencial','note_formats'), ('essencial','review_and_sign'), ('essencial','pdf_export'),
    ('essencial','patients'), ('essencial','audit_trail'), ('essencial','code_suggestions'),
    ('essencial','missing_doc_warnings'), ('essencial','cfp_006_validator'),
    ('essencial','account_export'),
    -- Profissional and up
    ('profissional','document_intake'), ('profissional','case_timeline'),
    ('profissional','scales'), ('profissional','theme_timeline'), ('profissional','absence_detection'),
    ('profissional','scale_trajectory'), ('profissional','nl_search'),
    ('profissional','secure_sharing'), ('profissional','retention_dashboard'),
    -- Completo only
    ('completo','session_capture'), ('completo','plan_adherence'),
    ('completo','tiss_guide'), ('completo','denial_prediction'), ('completo','authorization_tracking')
  ) as f(plan_code, key) on true
 where (f.plan_code = 'essencial'    and p."Code" in ('essencial','profissional','completo'))
    or (f.plan_code = 'profissional' and p."Code" in ('profissional','completo'))
    or (f.plan_code = 'completo'     and p."Code" = 'completo')
on conflict ("PlanId","FeatureKey") do nothing;

commit;

-- Everything Essencial gets, Profissional and Completo get too — the tiers are
-- cumulative. Verify:
--   select p."Code", count(*) from "Plans" p
--     join "PlanFeatures" f on f."PlanId" = p."Id" group by p."Code" order by 2;
