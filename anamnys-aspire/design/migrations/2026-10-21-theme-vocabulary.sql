-- F-02: theme vocabulary (design/specs/2026-10-21-theme-vocabulary-design.md, sections 2 and 3).
-- Seeds mh-1, records who added and removed a tag, allows one active tag per note and term, and
-- adds the succession and change tables behind decision D-01.
-- Idempotent; all or nothing.
BEGIN;

ALTER TABLE "ThemeTags" ADD COLUMN IF NOT EXISTS "AddedById" uuid;
ALTER TABLE "ThemeTags" ADD COLUMN IF NOT EXISTS "RemovedById" uuid;

-- Existing rows must satisfy the new rules before the index and the check are added, or a database
-- that already holds tags would reject the migration. Soft-removed rows predate RemovedById, so the
-- note's provider is recorded as remover (the only actor who could tag the note). When a note and
-- term have several active tags, the earliest stays and the rest are marked removed. Idempotent:
-- a second run finds nothing to fix.
UPDATE "ThemeTags" t SET "RemovedById" = n."ProviderId"
  FROM "Notes" n
 WHERE n."Id" = t."NoteId" AND t."RemovedAt" IS NOT NULL AND t."RemovedById" IS NULL;

UPDATE "ThemeTags" t SET "RemovedAt" = now(), "RemovedById" = n."ProviderId"
  FROM "Notes" n
 WHERE n."Id" = t."NoteId" AND t."RemovedAt" IS NULL
   AND EXISTS (SELECT 1 FROM "ThemeTags" e
                WHERE e."NoteId" = t."NoteId" AND e."TermId" = t."TermId" AND e."RemovedAt" IS NULL
                  AND (e."CreatedAt", e."Id") < (t."CreatedAt", t."Id"));

CREATE UNIQUE INDEX IF NOT EXISTS "ThemeTags_Note_Term_active_key"
    ON "ThemeTags" ("NoteId","TermId") WHERE "RemovedAt" IS NULL;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ThemeTags_Removed_ck') THEN
    ALTER TABLE "ThemeTags"
      ADD CONSTRAINT "ThemeTags_Removed_ck" CHECK ("RemovedAt" IS NULL OR "RemovedById" IS NOT NULL);
  END IF;
END $$;

CREATE TABLE IF NOT EXISTS "ThemeTermSuccessions" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"FromTermId" uuid NOT NULL,
	"ToTermId" uuid NOT NULL,
	"Kind" text NOT NULL,
	"At" timestamp with time zone DEFAULT now() NOT NULL,
	"StaffId" uuid NOT NULL,
	CONSTRAINT "ThemeTermSuccessions_From_To_key" UNIQUE("FromTermId","ToTermId"),
	CONSTRAINT "ThemeTermSuccessions_Kind_ck" CHECK (("Kind" = ANY (ARRAY['split'::text, 'merge'::text])))
);

CREATE TABLE IF NOT EXISTS "ThemeTermChanges" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"TermId" uuid NOT NULL,
	"Kind" text NOT NULL,
	"PreviousLabel" text,
	"NewLabel" text,
	"StaffId" uuid NOT NULL,
	"At" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "ThemeTermChanges_Kind_ck" CHECK (("Kind" = ANY (ARRAY['add'::text, 'rename'::text, 'retire'::text, 'split'::text, 'merge'::text])))
);

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ThemeTermSuccessions_FromTermId_fkey') THEN
    ALTER TABLE "ThemeTermSuccessions" ADD CONSTRAINT "ThemeTermSuccessions_FromTermId_fkey" FOREIGN KEY ("FromTermId") REFERENCES "ThemeTerms"("Id") ON DELETE RESTRICT;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ThemeTermSuccessions_ToTermId_fkey') THEN
    ALTER TABLE "ThemeTermSuccessions" ADD CONSTRAINT "ThemeTermSuccessions_ToTermId_fkey" FOREIGN KEY ("ToTermId") REFERENCES "ThemeTerms"("Id") ON DELETE RESTRICT;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ThemeTermSuccessions_StaffId_fkey') THEN
    ALTER TABLE "ThemeTermSuccessions" ADD CONSTRAINT "ThemeTermSuccessions_StaffId_fkey" FOREIGN KEY ("StaffId") REFERENCES "Staff"("Id") ON DELETE RESTRICT;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ThemeTermChanges_TermId_fkey') THEN
    ALTER TABLE "ThemeTermChanges" ADD CONSTRAINT "ThemeTermChanges_TermId_fkey" FOREIGN KEY ("TermId") REFERENCES "ThemeTerms"("Id") ON DELETE RESTRICT;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ThemeTermChanges_StaffId_fkey') THEN
    ALTER TABLE "ThemeTermChanges" ADD CONSTRAINT "ThemeTermChanges_StaffId_fkey" FOREIGN KEY ("StaffId") REFERENCES "Staff"("Id") ON DELETE RESTRICT;
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS "ThemeTermChanges_TermId_idx" ON "ThemeTermChanges" ("TermId");

-- ── theme vocabulary (F-02) ──────────────────────────────────────────────────
-- Mental health only. The per-specialty structure stays in the schema, but no
-- other specialty is seeded while the product is positioned for psychology.
--
-- O QUE A D-01 PERMITE E O QUE NÃO PERMITE, porque a diferença decide o que
-- pode ser semeado hoje:
--
--   ACRESCENTAR termo é seguro. Nenhuma série existente muda de significado —
--   'trabalho' continua querendo dizer trabalho depois que 'assedio_no_trabalho'
--   passa a existir. O único efeito é que a série do termo novo começa na data
--   em que ele entrou, e é exatamente para isso que existe "IntroducedOn".
--
--   RENOMEAR, DIVIDIR ou FUNDIR termo não é seguro, e continua bloqueado pela
--   D-01. É o caso de dividir 'ansiedade' em 'generalizada' e 'social': dois
--   anos de marcação anterior deixariam de ter sentido. Quando essa regra for
--   escrita, o caminho já está no esquema — "SupersededByTermId" liga o termo
--   antigo ao novo, e a série se remonta pela ligação.
--
-- Por isso os termos abaixo entram por acréscimo, e os recortes de trabalho e
-- imigração entram como FILHOS ("ParentTermId") em vez de substituir os termos
-- amplos. Uma consulta de rollup soma filho no pai, e a série de 'trabalho'
-- segue contínua de ponta a ponta.
-- Superado em 2026-10-21: a D-01 permite renomear, dividir e fundir sem mover marcações (documentation/23-theme-vocabulary.md).
insert into "ThemeVocabularies" ("Version","PublishedAt","Active")
  values ('mh-1', now(), true)
on conflict ("Version") do nothing;

-- At most one active vocabulary: the term lookups assume a single one.
CREATE UNIQUE INDEX IF NOT EXISTS "ThemeVocabularies_single_active_key"
    ON "ThemeVocabularies" ("Active") WHERE "Active";

-- Termos de primeiro nível.
insert into "ThemeTerms" ("VocabularyId","Code","Label")
select v."Id", t.code, t.label
  from "ThemeVocabularies" v
  cross join (values
    -- conjunto original
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
    ('medicacao',       'Medicação'),

    -- Estados que apareciam diluídos em 'humor' e não se deixavam acompanhar.
    ('raiva_irritabilidade',    'Raiva e irritabilidade'),
    ('culpa_vergonha',          'Culpa e vergonha'),
    ('solidao',                 'Solidão'),
    ('motivacao_procrastinacao','Motivação e procrastinação'),

    -- RISCO — NÃO SEMEAR. 'ideacao_suicida' e 'autolesao' foram propostos e
    -- descartados. Registro do porquê, para que ninguém os acrescente de novo
    -- achando que é esquecimento:
    --
    --   1. Quem aplica a etiqueta é a IA. O F-02 restringe o prompt de
    --      estruturação ao vocabulário ativo, e "ThemeTags"."CreatedBy" tem
    --      default 'ai'. Máquina escolhendo 'ideacao_suicida' é máquina
    --      emitindo inferência de risco — a linha do Manual Orientativo do
    --      CFP (2025).
    --   2. A nota já diz, e diz melhor. Na prosa está o entendimento do
    --      profissional, com a nuance que fez daquilo um julgamento, escrito e
    --      assinado por ele. A etiqueta achata isso numa palavra e reatribui a
    --      autoria ao sistema.
    --   3. A série longitudinal não serve. Para 'sono' a tendência é o dado;
    --      para risco, decide-se na primeira menção, não pela curva. E a
    --      detecção de ausência (F-12) geraria "registrado em 4 das 9
    --      primeiras sessões; sem registro nas 11 seguintes" — afirmação de
    --      que o risco cessou, produzida por software.
    --   4. Parece rede de proteção e não é. O sistema vê só o que foi escrito
    --      em nota estruturada. Paciente em risco pode nunca dizer as palavras.
    --      Sinal silenciosamente incompleto com cara de vigilância é pior do
    --      que sinal nenhum.
    --   5. O esquema não consegue garantir "só humano aplica este termo".
    --      Seria convenção, não restrição, e para isto convenção não serve.
    --
    -- Se um dia o risco precisar de estrutura, entra como plano de segurança
    -- criado e assinado deliberadamente pelo profissional — nunca como termo
    -- extraível deste vocabulário.

    -- 'violencia_domestica' — NÃO SEMEAR. Descartado por motivo diferente do
    -- que derrubou os termos de risco acima. Não é conclusão clínica: é fato
    -- relatado, como 'luto'. Mas:
    --
    --   1. A detecção de ausência (F-12) geraria "registrada em 4 das 9
    --      primeiras sessões; sem registro nas 11 seguintes". Em violência
    --      doméstica o relato parar é característica conhecida do ciclo, e a
    --      frase renderiza o oposto disso. Pior aqui do que na ideação
    --      suicida: lá o silêncio é ambíguo, aqui o sistema inverte o sinal.
    --   2. Existe DEVER LEGAL DE NOTIFICAR. Lei 10.778/2003, art. 1º —
    --      notificação compulsória dos casos com indícios ou confirmação de
    --      violência contra a mulher atendida em serviços de saúde públicos E
    --      PRIVADOS; a Lei 13.931/2019 acrescentou comunicação à autoridade
    --      policial em 24 h. Para criança e adolescente, ECA art. 13,
    --      comunicação ao Conselho Tutelar. O alcance disso sobre o psicólogo
    --      autônomo é matéria em disputa, e o produto não decide por ele.
    --   3. Etiqueta estruturada e consultável não é a frase na prosa. É prova
    --      legível por máquina de que o caso foi identificado pelo sistema, em
    --      prontuário requisitável, sem registro de que algo foi feito. Isso
    --      não protege o profissional: expõe.
    --
    -- Violência no prontuário não é tema, é gatilho de um dever. O que precisa
    -- ser modelado é o dever e o ATO — quem notificou, quando, a quem —, não o
    -- assunto. Mesmo caminho do plano de segurança.
    --
    -- 'discriminacao' FICA. Testado contra os mesmos critérios e não compartilha
    -- nenhum: a IA não infere estado psicológico ao marcá-lo, a recorrência ao
    -- longo do acompanhamento tem uso clínico real, e não há dever legal
    -- pendurado. Mesma natureza de 'luto' e 'desemprego'.
    ('discriminacao',           'Discriminação'),

    ('dor_saude_fisica',        'Dor e saúde física'),
    ('sentido_espiritualidade', 'Sentido e espiritualidade'),
    ('parentalidade',           'Parentalidade'),
    ('identidade_genero',       'Identidade de gênero'),
    ('uso_de_telas',            'Uso de telas'),
    ('transicoes_de_vida',      'Transições de vida'),
    ('envelhecimento',          'Envelhecimento'),

    -- Raiz nova. Os recortes vêm abaixo, como filhos.
    ('imigracao',               'Imigração')
  ) as t(code,label)
 where v."Version" = 'mh-1'
on conflict ("VocabularyId","Code") do nothing;

-- Termos de segundo nível. O pai é resolvido pelo código, então esta parte pode
-- rodar quantas vezes for preciso e continua idempotente.
insert into "ThemeTerms" ("VocabularyId","Code","Label","ParentTermId")
select v."Id", t.code, t.label, p."Id"
  from "ThemeVocabularies" v
  join "ThemeTerms" p on p."VocabularyId" = v."Id"
  join (values
    -- sob 'trabalho'
    ('trabalho', 'assedio_no_trabalho',        'Assédio no trabalho'),
    ('trabalho', 'conflito_no_trabalho',       'Conflito no trabalho'),
    ('trabalho', 'sobrecarga_jornada',         'Sobrecarga e jornada'),
    ('trabalho', 'inseguranca_no_emprego',     'Insegurança no emprego'),
    ('trabalho', 'retorno_ao_trabalho',        'Retorno ao trabalho'),
    ('trabalho', 'limites_trabalho_vida',      'Limites entre trabalho e vida'),

    -- sob 'imigracao'
    ('imigracao','adaptacao_cultural',         'Adaptação cultural'),
    ('imigracao','saudade_desenraizamento',    'Saudade e desenraizamento'),
    ('imigracao','barreira_linguistica',       'Barreira linguística'),
    ('imigracao','familia_a_distancia',        'Família à distância'),
    ('imigracao','status_migratorio',          'Incerteza de status migratório'),
    ('imigracao','revalidacao_recomeco',       'Revalidação e recomeço profissional')
  ) as t(parent_code, code, label) on t.parent_code = p."Code"
 where v."Version" = 'mh-1'
on conflict ("VocabularyId","Code") do nothing;


COMMIT;
