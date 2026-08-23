-- =============================================================================
--  Anamnys — complete schema
--  PostgreSQL 15+ · greenfield (builds all 70 tables from an empty database)
--
--  Naming follows the EF Core default already in use: PascalCase plural tables,
--  PascalCase columns. Every identifier is therefore quoted.
--
--  Derived from specs/docs/anamnys-modelo-dados.html. Backlog ids (F-nn) in the
--  comments point at TODO.txt.
--
--  Run:  psql -v ON_ERROR_STOP=1 -d anamnys_dev -f 01_schema.sql
-- =============================================================================

begin;

-- ── extensions ───────────────────────────────────────────────────────────────
-- btree_gist lets an exclusion constraint mix equality (uuid) with overlap
-- (tstzrange) in one index. Without it, the booking non-overlap guarantee in
-- F-62 cannot be expressed and has to be faked in application code, which loses
-- the race under concurrency.
create extension if not exists btree_gist;
create extension if not exists pgcrypto;

-- ── shared helpers ───────────────────────────────────────────────────────────

-- Append-only guard. Attached to tables that are evidence rather than state:
-- the audit trail and the integrity hash chain. Their value comes entirely from
-- the fact that nothing can rewrite them.
create or replace function anamnys_forbid_mutation() returns trigger
language plpgsql as $$
begin
  raise exception
    'Table "%" is append-only: % is not permitted.', tg_table_name, tg_op
    using hint = 'Insert a correcting row instead of altering history.';
end;
$$;

create or replace function anamnys_touch_updated_at() returns trigger
language plpgsql as $$
begin
  new."UpdatedAt" := now();
  return new;
end;
$$;


-- =============================================================================
--  A — IDENTITY AND ACCOUNT
-- =============================================================================

create table "Providers" (
  "Id"                       uuid primary key default gen_random_uuid(),
  "Email"                    text        not null,
  "Name"                     text        not null,
  "PasswordHash"             text        not null,
  -- CRP is required to sign a document (Res. CFP 006/2019) and to advertise
  -- services. Nullable here only so an account can exist before onboarding
  -- completes; publishing and signing check it explicitly.
  "CrpNumber"                text,
  "CrpRegion"                text,
  "Specialty"                text        not null default 'MentalHealth',
  "PreferredNoteFormat"      text        not null default 'DAP',
  "BillingSystem"            integer     not null default 2,   -- 2 = BrazilTuss
  "TwoFactorEnabled"         boolean     not null default false,
  "TwoFactorSecretEncrypted" text,
  "CreatedAt"                timestamptz not null default now(),
  "UpdatedAt"                timestamptz not null default now(),
  constraint "Providers_Email_unique"    unique ("Email"),
  constraint "Providers_NoteFormat_ck"   check ("PreferredNoteFormat" in ('DAP','SOAP','Biopsychosocial','PtFunctional')),
  constraint "Providers_Crp_ck"          check (("CrpNumber" is null) = ("CrpRegion" is null))
);

-- Tenant anchor. Every clinical table reaches "Providers" by a known path, and
-- the composite key below is what lets child tables prove they agree with it.
create unique index "Providers_Id_key" on "Providers" ("Id");

create trigger "Providers_touch" before update on "Providers"
  for each row execute function anamnys_touch_updated_at();


create table "RecoveryCodes" (
  "Id"         uuid primary key default gen_random_uuid(),
  "ProviderId" uuid        not null references "Providers"("Id") on delete cascade,
  "CodeHash"   text        not null,
  "CreatedAt"  timestamptz not null default now(),
  "UsedAt"     timestamptz
);
create index "RecoveryCodes_ProviderId_idx" on "RecoveryCodes" ("ProviderId");


-- Public contact form. Deliberately has no ProviderId: anyone may submit.
create table "ContactMessages" (
  "Id"        uuid primary key default gen_random_uuid(),
  "FullName"  text        not null,
  "Email"     text        not null,
  "Phone"     text,
  "Message"   text        not null,
  "CreatedAt" timestamptz not null default now()
);
create index "ContactMessages_CreatedAt_idx" on "ContactMessages" ("CreatedAt");


-- Records the exception, not the rule: our staff reading clinical content only
-- ever happens on an authorised support request, and the professional can see it.
create table "AccessLogs" (
  "Id"           uuid primary key default gen_random_uuid(),
  "ProviderId"   uuid        not null references "Providers"("Id") on delete cascade,
  "ActorType"    text        not null,
  "Actor"        text        not null,
  "Scope"        text        not null,
  "TicketRef"    text,
  "AuthorizedAt" timestamptz,
  "At"           timestamptz not null default now(),
  constraint "AccessLogs_ActorType_ck" check ("ActorType" in ('provider','patient','staff','system'))
);
create index "AccessLogs_ProviderId_At_idx" on "AccessLogs" ("ProviderId", "At" desc);


-- =============================================================================
--  B — CHART CORE
-- =============================================================================

create table "Patients" (
  "Id"                uuid primary key default gen_random_uuid(),
  "ProviderId"        uuid        not null references "Providers"("Id") on delete restrict,
  "FirstName"         text        not null,
  "LastName"          text        not null,
  "DateOfBirth"       date,
  "PreferredLanguage" text,
  "LastVisit"         timestamptz,
  "CreatedAt"         timestamptz not null default now(),
  "UpdatedAt"         timestamptz not null default now(),
  -- Lets every child table carry ProviderId AND prove it matches the patient's
  -- owner, instead of trusting the application to keep the two in step.
  constraint "Patients_Id_ProviderId_key" unique ("Id", "ProviderId")
);
create index "Patients_ProviderId_idx" on "Patients" ("ProviderId");
create trigger "Patients_touch" before update on "Patients"
  for each row execute function anamnys_touch_updated_at();


-- Declared before "Sessions" because "Notes" references it; the FK is added
-- after "Sessions" exists.
create table "Notes" (
  "Id"            uuid primary key default gen_random_uuid(),
  "PatientId"     uuid        not null,
  "ProviderId"    uuid        not null,
  "SessionId"     uuid,
  "Status"        text        not null default 'Draft',
  "InputMode"     text        not null,
  "Format"        text        not null default 'DAP',
  "RawTranscript" text,
  "SignedAt"      timestamptz,
  "CreatedAt"     timestamptz not null default now(),
  "UpdatedAt"     timestamptz not null default now(),
  constraint "Notes_Patient_fk"   foreign key ("PatientId", "ProviderId")
                                  references "Patients" ("Id", "ProviderId") on delete restrict,
  constraint "Notes_Status_ck"    check ("Status" in ('Draft','Processing','ReadyForReview','Signed','Exported')),
  constraint "Notes_InputMode_ck" check ("InputMode" in ('VoiceBatch','VoiceLive','VoiceAppointment','Text')),
  constraint "Notes_Format_ck"    check ("Format" in ('DAP','SOAP','Biopsychosocial','PtFunctional')),
  -- A signed note is the official record. It carries a signature timestamp and
  -- is never edited in place; a correction is a new note linked to this one.
  constraint "Notes_Signed_ck"    check (("Status" in ('Signed','Exported')) = ("SignedAt" is not null))
);
create index "Notes_PatientId_idx"  on "Notes" ("PatientId");
create index "Notes_ProviderId_idx" on "Notes" ("ProviderId");
create index "Notes_Status_idx"     on "Notes" ("Status");
create trigger "Notes_touch" before update on "Notes"
  for each row execute function anamnys_touch_updated_at();


-- Was a JSONB dictionary inside "Notes". Extracted so a section can be queried,
-- diffed and rendered without opening every note.
create table "NoteSections" (
  "Id"          uuid primary key default gen_random_uuid(),
  "NoteId"      uuid        not null references "Notes"("Id") on delete cascade,
  "Key"         text        not null,
  "Content"     text        not null default '',
  "Position"    integer     not null default 0,
  "GeneratedAt" timestamptz not null default now(),
  "EditedAt"    timestamptz,
  constraint "NoteSections_NoteId_Key_key" unique ("NoteId", "Key")
);
create index "NoteSections_NoteId_idx" on "NoteSections" ("NoteId");


create table "ClinicalDocuments" (
  "Id"             uuid primary key default gen_random_uuid(),
  "PatientId"      uuid        not null,
  "ProviderId"     uuid        not null,
  "NoteId"         uuid        references "Notes"("Id") on delete set null,
  "Kind"           text        not null,
  "Body"           text        not null default '',
  "SignedAt"       timestamptz,
  "RetentionUntil" date,
  "SupersededBy"   uuid        references "ClinicalDocuments"("Id") on delete set null,
  "CreatedAt"      timestamptz not null default now(),
  constraint "ClinicalDocuments_Patient_fk" foreign key ("PatientId", "ProviderId")
                                            references "Patients" ("Id", "ProviderId") on delete restrict,
  -- The six modalities of Art. 8 of Res. CFP 006/2019. "relatorio" is the
  -- Relatório Psicológico (Art. 11); "relatorio_multiprofissional" is the
  -- distinct modality of Art. 12, which has its own authorship and analysis
  -- rules and cannot be validated by the Art. 11 ruleset.
  constraint "ClinicalDocuments_Kind_ck"    check ("Kind" in ('declaracao','atestado','relatorio',
                                                              'relatorio_multiprofissional','laudo','parecer'))
);
create index "ClinicalDocuments_PatientId_idx"      on "ClinicalDocuments" ("PatientId");
create index "ClinicalDocuments_RetentionUntil_idx" on "ClinicalDocuments" ("RetentionUntil")
  where "RetentionUntil" is not null;


-- Evidence, not state. Append-only by trigger below.
create table "AuditEntries" (
  "Id"            uuid primary key default gen_random_uuid(),
  "NoteId"        uuid references "Notes"("Id") on delete restrict,
  "DocumentId"    uuid references "ClinicalDocuments"("Id") on delete restrict,
  "ActorId"       uuid,
  "Action"        text        not null,
  "FieldChanged"  text,
  "PreviousValue" text,
  "NewValue"      text,
  "At"            timestamptz not null default now(),
  constraint "AuditEntries_subject_ck" check ("NoteId" is not null or "DocumentId" is not null)
);
create index "AuditEntries_NoteId_At_idx"     on "AuditEntries" ("NoteId", "At" desc);
create index "AuditEntries_DocumentId_At_idx" on "AuditEntries" ("DocumentId", "At" desc);
create index "AuditEntries_At_idx"            on "AuditEntries" ("At" desc);

create trigger "AuditEntries_append_only" before update or delete on "AuditEntries"
  for each row execute function anamnys_forbid_mutation();


create table "TreatmentPlans" (
  "Id"         uuid primary key default gen_random_uuid(),
  "PatientId"  uuid        not null references "Patients"("Id") on delete cascade,
  "CreatedAt"  timestamptz not null default now(),
  "ReviewedAt" timestamptz,
  "ClosedAt"   timestamptz
);
create index "TreatmentPlans_PatientId_idx" on "TreatmentPlans" ("PatientId");


create table "PlanObjectives" (
  "Id"                     uuid primary key default gen_random_uuid(),
  "PlanId"                 uuid        not null references "TreatmentPlans"("Id") on delete cascade,
  "Description"            text        not null,
  "TermId"                 uuid,
  "LastRecordedSessionId"  uuid,
  "CreatedAt"              timestamptz not null default now()
);
create index "PlanObjectives_PlanId_idx" on "PlanObjectives" ("PlanId");


create table "FollowUpItems" (
  "Id"            uuid primary key default gen_random_uuid(),
  "PatientId"     uuid        not null references "Patients"("Id") on delete cascade,
  "NoteId"        uuid        references "Notes"("Id") on delete set null,
  "Description"   text        not null,
  "ResolvedAt"    timestamptz,
  "ResolvedNoteId" uuid       references "Notes"("Id") on delete set null,
  "CreatedAt"     timestamptz not null default now()
);
create index "FollowUpItems_PatientId_idx" on "FollowUpItems" ("PatientId")
  where "ResolvedAt" is null;


-- =============================================================================
--  C — LONGITUDINAL FOUNDATION  (F-01 .. F-04)
-- =============================================================================

create table "Sessions" (
  "Id"                    uuid primary key default gen_random_uuid(),
  "PatientId"             uuid        not null,
  "ProviderId"            uuid        not null,
  "ScheduledStart"        timestamptz,
  "ScheduledMinutes"      integer,
  "ActualStart"           timestamptz,
  "ActualEnd"             timestamptz,
  "Modality"              text,
  "Attendance"            text        not null default 'scheduled',
  "CancelledAt"           timestamptz,
  "CancellationLeadHours" numeric(6,2),
  "CreatedAt"             timestamptz not null default now(),
  constraint "Sessions_Patient_fk"    foreign key ("PatientId", "ProviderId")
                                      references "Patients" ("Id", "ProviderId") on delete restrict,
  constraint "Sessions_Modality_ck"   check ("Modality" is null or "Modality" in ('presencial','online')),
  constraint "Sessions_Attendance_ck" check ("Attendance" in ('scheduled','attended','cancelled','no_show')),
  constraint "Sessions_Times_ck"      check ("ActualEnd" is null or "ActualStart" is null or "ActualEnd" > "ActualStart")
);
create index "Sessions_PatientId_Start_idx" on "Sessions" ("PatientId", "ScheduledStart" desc);
create index "Sessions_ProviderId_Start_idx" on "Sessions" ("ProviderId", "ScheduledStart" desc);

alter table "Notes"
  add constraint "Notes_Session_fk" foreign key ("SessionId")
  references "Sessions"("Id") on delete set null;

alter table "PlanObjectives"
  add constraint "PlanObjectives_Session_fk" foreign key ("LastRecordedSessionId")
  references "Sessions"("Id") on delete set null;


-- Registry of instruments. Deliberately stores the score range and NOTHING that
-- interprets it: recording the number is a record, labelling it "moderate" is
-- clinical interpretation and belongs to the professional (F-01).
--
-- The regulatory columns are not documentation. A catalogue INDUCES the
-- professional's conduct, and under art. 12 of Res. CFP 31/2022 the ethics
-- sanction falls on them, not on us — so the rules that decide what may be
-- offered are constraints here, not conventions in application code.
--
--   "SatepsiStatus"  where the instrument sits in the CFP's own lists.
--   "LicenseMode"    what copyright allows, which is a separate question.
--   "MayRenderItems" whether the app may show the questionnaire on screen.
--                    FALSE means score-only: the professional applied the
--                    instrument through the official material and types the
--                    number in. That is registro documental, not aplicação.
--
-- NOTHING HERE IS "APPROVED BY THE CFP", and no screen may say so.
-- 'favoravel' means the CFP certified a psychological test. 'nao_privativo'
-- means the opposite — the CFP determined the thing is NOT a psychological
-- test, so it falls outside certification entirely; the SATEPSI itself states
-- that the psychometric quality of non-privative instruments is not assessed
-- by the CFP and that checking it is the professional's job. Neither status is
-- an endorsement of the instrument's quality.
create table "Instruments" (
  "Id"               uuid    primary key default gen_random_uuid(),
  "Code"             text    not null,
  "Name"             text    not null,
  "ItemCount"        integer,
  "MinScore"         integer not null default 0,
  "MaxScore"         integer not null,
  "SatepsiStatus"    text    not null default 'sem_classificacao',
  "SatepsiCheckedOn" date,
  "LicenseMode"      text    not null default 'proprietario',
  "MayRenderItems"   boolean not null default false,
  "SourceRef"        text,
  -- OBRIGATÓRIA, e obrigatória para TODOS — não só para os bloqueados.
  -- A profissional tem de saber por que uma coisa está disponível tanto quanto
  -- por que outra não está: "o app não oferece o DASS-21" sem explicação parece
  -- limitação do produto, e ela vai procurar outro caminho. Com a explicação,
  -- ela aprende algo que a protege. Texto em português, escrito para ela e não
  -- para quem programa — esta coluna vai para a tela.
  "StatusExplanation" text   not null,
  -- Um único sinal de "pode ser usado". Havia também um "Active" default TRUE
  -- aqui, e manter os dois era um bug esperando: quem escrevesse
  -- `where "Active"` receberia o DASS-21 de volta. "Enabled" default FALSE é o
  -- sinal, e é o que as restrições abaixo governam.
  "Enabled"          boolean not null default false,
  "Version"          text    not null default '1',
  constraint "Instruments_Code_key" unique ("Code"),

  constraint "Instruments_Satepsi_ck" check ("SatepsiStatus" in (
    'nao_privativo',      -- consta na lista de Instrumentos Não Privativos
    'favoravel',          -- teste psicológico com parecer favorável
    'desfavoravel',       -- teste psicológico com parecer desfavorável
    'nao_avaliado',       -- consta na lista de Testes Não Avaliados
    'sem_classificacao'   -- não consta em lista nenhuma do SATEPSI
  )),

  constraint "Instruments_License_ck" check ("LicenseMode" in (
    'livre',                 -- reprodução permitida pelo detentor, sem pedido
    'dominio_publico',
    'autorizacao_necessaria',-- gratuito ou não, mas exige pedido antes de exibir
    'proprietario',          -- editora, venda restrita
    'vedado_digital'         -- licença proíbe expressamente uso digital
  )),

  -- A REGRA CENTRAL, e ela é uma LISTA DE PERMISSÃO, não de proibição.
  --
  -- Ficar de fora das listas do SATEPSI não é vácuo regulatório — é
  -- classificação pendente, e o padrão é restritivo. O FAQ do CFP diz, com
  -- estas palavras: "os testes que não foram analisados pelo CFP não estão
  -- aprovados e, por isso, não podem ser utilizados para fins profissionais".
  -- E o art. 13 da Res. 31/2022 mostra o caminho: na dúvida o CRP submete à
  -- CCAP, que classifica como instrumento não privativo OU como teste
  -- psicológico — e no segundo caso o instrumento cai na lista de Testes Não
  -- Avaliados, onde o uso vira falta ética pelo art. 12.
  --
  -- Portanto: só é habilitável o que tem POSIÇÃO EXPLÍCITA do CFP. Silêncio
  -- não conta como permissão. Instrumento sem classificação fica desligado até
  -- alguém percorrer o art. 13.
  constraint "Instruments_Enabled_ck" check (
    "Enabled" = false or "SatepsiStatus" in ('nao_privativo','favoravel')
  ),

  -- Status sem data de conferência é afirmação sem lastro: as listas do SATEPSI
  -- mudam, e um status anotado uma vez e nunca reconferido envelhece calado.
  constraint "Instruments_Checked_ck" check (
    "SatepsiStatus" = 'sem_classificacao' or "SatepsiCheckedOn" is not null
  ),

  -- Exibir os itens é reprodução de obra. Só quem tem licença livre ou domínio
  -- público pode. Ninguém habilita isso por engano num instrumento pago.
  -- Exibir os itens é reprodução de obra E é aplicação do instrumento. Exige as
  -- duas permissões: licença que autorize reproduzir, e pronunciamento do CFP.
  -- Instrumento sem classificação pode ter escore registrado após adesão, mas
  -- NUNCA ser aplicado pela tela: se a CCAP vier a chamá-lo de teste
  -- psicológico, a aplicação informatizada dependeria de manual aprovado e
  -- estudo de equivalência (art. 11 e Cap. V) que ninguém tem.
  constraint "Instruments_Render_ck" check (
    "MayRenderItems" = false
    or ("LicenseMode" in ('livre','dominio_publico')
        and "SatepsiStatus" in ('nao_privativo','favoravel'))
  ),

  -- Explicação vazia é o mesmo que explicação ausente.
  constraint "Instruments_Explanation_ck" check (length(trim("StatusExplanation")) > 20),

  constraint "Instruments_Range_ck" check ("MaxScore" > "MinScore")
);


create table "ScaleApplications" (
  "Id"           uuid primary key default gen_random_uuid(),
  "PatientId"    uuid        not null references "Patients"("Id") on delete cascade,
  "SessionId"    uuid        references "Sessions"("Id") on delete set null,
  "InstrumentId" uuid        not null references "Instruments"("Id") on delete restrict,
  "Score"        integer     not null,
  "AppliedAt"    timestamptz not null,
  "AppliedBy"    uuid,
  "RawAnswers"   jsonb,
  "CreatedAt"    timestamptz not null default now(),
  constraint "ScaleApplications_Score_ck" check ("Score" >= 0)
);
create index "ScaleApplications_Patient_Applied_idx"
  on "ScaleApplications" ("PatientId", "InstrumentId", "AppliedAt");

-- "RawAnswers" holds item-level responses, which only exist if the app SHOWED
-- the items and COLLECTED them. For an instrument the app may not render, a
-- populated "RawAnswers" is evidence of exactly the thing the licence forbids —
-- and, for a teste psicológico, of aplicação outside the approved manual
-- (Res. CFP 31/2022, art. 11). Score-only means score only.
create or replace function anamnys_check_scale_application()
returns trigger language plpgsql as $$
declare
  inst record;
  prov uuid;
begin
  select "Code", "MayRenderItems", "SatepsiStatus", "StatusExplanation" into inst
    from "Instruments" where "Id" = new."InstrumentId";

  -- 1. Respostas item a item só de quem o app pode exibir.
  if new."RawAnswers" is not null and not inst."MayRenderItems" then
    raise exception
      'Instrument "%" is score-only: item-level answers may not be stored.', inst."Code"
      using hint = 'Record the score the professional obtained. Storing "RawAnswers" '
                   'implies the app administered the instrument, which its licence '
                   'or the approved manual does not permit.';
  end if;

  -- 2. VEDADO PELO CFP: recusa absoluta, sem caminho alternativo.
  --
  -- Aqui existe infração publicada (art. 12), e nenhuma confirmação a desfaz.
  -- Houve uma versão que aceitava o lançamento se a profissional reconhecesse o
  -- aviso: era pior que o bloqueio. Em prontuário requisitável aquele campo é o
  -- melhor documento possível CONTRA ela num processo ético-disciplinar —
  -- registro datado provando que o sistema avisou ser falta ética e que ela
  -- seguiu. O produto construiria a prova contra a própria usuária.
  if inst."SatepsiStatus" in ('nao_avaliado','desfavoravel') then
    raise exception
      'Instrument "%" may not be recorded: the CFP forbids its use. %',
      inst."Code", inst."StatusExplanation"
      using hint = 'Show "StatusExplanation" verbatim. There is no acknowledgement that '
                   'unlocks this — the prohibition is published, not uncertain.';
  end if;

  -- 3. SEM CLASSIFICAÇÃO: exige adesão registrada da profissional, uma vez por
  -- instrumento. Caso diferente do anterior e tratado de forma diferente: não
  -- há proibição publicada, e o art. 5º atribui a ela a decisão. Ver a nota
  -- extensa em "ProviderInstrumentOptIns" para o fundamento.
  --
  -- O profissional vem do paciente: "ScaleApplications" não guarda ProviderId,
  -- e "Patients" é dono dessa relação.
  if inst."SatepsiStatus" = 'sem_classificacao' then
    select p."ProviderId" into prov from "Patients" p where p."Id" = new."PatientId";
    if not exists (
      select 1 from "ProviderInstrumentOptIns" o
       where o."ProviderId" = prov
         and o."InstrumentId" = new."InstrumentId"
         and o."RevokedAt" is null
    ) then
      raise exception
        'Instrument "%" has no CFP classification and this professional has not opted in. %',
        inst."Code", inst."StatusExplanation"
        using hint = 'Show "StatusExplanation", then record the decision in '
                     '"ProviderInstrumentOptIns" with the text she read. One considered '
                     'opt-in per instrument — never a per-entry confirmation, which only '
                     'teaches people to click without reading.';
    end if;
  end if;

  return new;
end;
$$;

create trigger "ScaleApplications_guard"
  before insert or update on "ScaleApplications"
  for each row execute function anamnys_check_scale_application();


-- Segunda linha de defesa, não a primeira. O gatilho acima já impede que
-- instrumento vedado entre na tabela, então em banco novo esta visão não filtra
-- nada — e mesmo assim ela fica, por dois motivos concretos:
--
--   1. MIGRAÇÃO. Dado importado de outro sistema é o caminho realista para
--      linhas proibidas aparecerem, e um superusuário pode desabilitar gatilho
--      durante uma carga.
--   2. MUDANÇA DE STATUS. Instrumento hoje regular pode ir parar na lista de
--      Testes Não Avaliados amanhã. Nesse dia as linhas antigas continuam na
--      tabela, e é certo que devem sair dos gráficos sem que ninguém precise
--      lembrar de ajustar consulta nenhuma.
--
-- Toda consulta de série longitudinal lê ESTA VISÃO, nunca a tabela.
-- ADESÃO POR PROFISSIONAL A INSTRUMENTO SEM CLASSIFICAÇÃO.
--
-- A distinção que esta tabela existe para respeitar: instrumento JÁ CLASSIFICADO
-- E EXCLUÍDO é uma coisa; instrumento ainda PENDENTE de classificação é outra, e
-- juridicamente elas não se equivalem.
--
--   O art. 12 tipifica falta ética em duas hipóteses ENUMERADAS: parecer
--   desfavorável, ou constar da lista de Testes Não Avaliados. É proibição por
--   lista. O que não está em lista nenhuma não é alcançado por ela.
--
--   O art. 5º dá à profissional a prerrogativa de escolher os instrumentos
--   "desde que fundamentados na literatura científica psicológica e nas normas
--   vigentes do CFP". Para um instrumento pendente com literatura sólida e
--   validação brasileira, as duas condições se cumprem — não há norma vigente
--   que o proíba. Para um da lista de Testes Não Avaliados, a segunda condição
--   falha, e o art. 5º não ampara.
--
--   O art. 4º admite como fonte complementar "instrumentos não psicológicos que
--   possuam respaldo da literatura científica da área".
--
-- O FAQ do SATEPSI diz que teste não analisado não está aprovado e não pode ser
-- usado profissionalmente. É orientação, não ato normativo, e o sujeito da frase
-- é "teste psicológico" — que é exatamente a classificação ainda indeterminada.
-- A frase pressupõe a conclusão que se quer tirar dela. Pesa, e não decide.
--
-- POR QUE AQUI A ADESÃO NÃO É O QUE ERA A "CIÊNCIA" DO INSTRUMENTO VEDADO.
-- Naquele caso, o registro documentava seguir apesar de infração publicada —
-- confissão datada em prontuário requisitável. Aqui não há infração: há
-- incerteza, e o art. 5º atribui à profissional justamente resolvê-la. O
-- registro documenta exercício informado de prerrogativa, que é o que boa
-- documentação clínica deve conter.
--
-- Uma adesão por profissional e por instrumento, não por lançamento. Decisão
-- ponderada uma vez vale mais que confirmação repetida cinquenta vezes, que
-- ensina a clicar sem ler.
create table "ProviderInstrumentOptIns" (
  "Id"               uuid        primary key default gen_random_uuid(),
  "ProviderId"       uuid        not null references "Providers"("Id") on delete cascade,
  "InstrumentId"     uuid        not null references "Instruments"("Id") on delete restrict,
  "AcknowledgedAt"   timestamptz not null default now(),
  -- Cópia do texto que ela leu. A explicação muda quando o status muda no
  -- SATEPSI, e "ela concordou" não significa nada sem saber com o quê.
  "ExplanationShown" text        not null,
  "RevokedAt"        timestamptz,
  constraint "ProviderInstrumentOptIns_key" unique ("ProviderId", "InstrumentId")
);

-- Adesão só existe para o que está pendente. Não há adesão possível a
-- instrumento vedado — é a diferença inteira entre os dois casos.
create or replace function anamnys_check_instrument_optin()
returns trigger language plpgsql as $$
declare
  st   text;
  code text;
begin
  select "SatepsiStatus", "Code" into st, code
    from "Instruments" where "Id" = new."InstrumentId";
  if st <> 'sem_classificacao' then
    raise exception
      'Instrument "%" does not take an opt-in (status: %).', code, st
      using hint = 'Opt-in exists only for instruments the CFP has not yet classified. '
                   'An instrument on the "Testes Não Avaliados" list is forbidden outright '
                   'and no acknowledgement can unlock it; a classified one needs no opt-in.';
  end if;
  return new;
end;
$$;

create trigger "ProviderInstrumentOptIns_guard"
  before insert or update on "ProviderInstrumentOptIns"
  for each row execute function anamnys_check_instrument_optin();


create view "InstrumentSeries" as
  select a.*, i."Code" as "InstrumentCode", i."SatepsiStatus"
    from "ScaleApplications" a
    join "Instruments" i on i."Id" = a."InstrumentId"
   where i."SatepsiStatus" in ('nao_privativo','favoravel');

-- CUIDADO com o art. 5º, que é o artigo mais fácil de usar errado aqui. Ele dá
-- à profissional a prerrogativa de escolher os instrumentos, mas com uma
-- condicional que se perde de vista: "desde que fundamentados na literatura
-- científica psicológica E NAS NORMAS VIGENTES do CFP". Instrumento da lista de
-- Testes Não Avaliados está fora das normas vigentes — o art. 5º NÃO ampara
-- esse uso. Ele ampara a escolha entre instrumentos regulares.
--
-- E CUIDADO com a palavra "aprovado". 'nao_privativo' não é aprovação: o CFP
-- determinou que a coisa NÃO É teste psicológico, logo está fora da
-- certificação, e o próprio SATEPSI diz que não avalia a qualidade psicométrica
-- desses instrumentos. Nenhuma tela pode dizer "aprovado pelo CFP" sobre nada
-- desta tabela. O que estes dois status têm em comum não é aprovação: é o CFP
-- TER SE PRONUNCIADO. É isso que o gatilho exige.


create table "ThemeVocabularies" (
  "Id"          uuid primary key default gen_random_uuid(),
  "Version"     text        not null,
  "PublishedAt" timestamptz,
  "Active"      boolean     not null default false,
  constraint "ThemeVocabularies_Version_key" unique ("Version")
);


-- SupersededByTermId is what keeps historical series intact when a term is
-- renamed, split or merged — the open question in D-01.
create table "ThemeTerms" (
  "Id"                   uuid primary key default gen_random_uuid(),
  "VocabularyId"         uuid    not null references "ThemeVocabularies"("Id") on delete restrict,
  "Code"                 text    not null,
  "Label"                text    not null,
  "ParentTermId"         uuid    references "ThemeTerms"("Id") on delete set null,
  -- When this term became available to tag with. Without it a term added in
  -- year two draws a timeline that appears to start in year two, and the
  -- professional reads a vocabulary change as a clinical change. The chart has
  -- to be able to say "termo introduzido aqui".
  "IntroducedOn"         date    not null default current_date,
  "Status"               text    not null default 'active',
  "SupersededByTermId"   uuid    references "ThemeTerms"("Id") on delete set null,
  constraint "ThemeTerms_Vocab_Code_key" unique ("VocabularyId", "Code"),
  constraint "ThemeTerms_Status_ck"      check ("Status" in ('active','superseded','retired'))
);
create index "ThemeTerms_VocabularyId_idx" on "ThemeTerms" ("VocabularyId");

alter table "PlanObjectives"
  add constraint "PlanObjectives_Term_fk" foreign key ("TermId")
  references "ThemeTerms"("Id") on delete set null;


-- Every derived value points back at the text that produced it. Anything that
-- cannot produce a reference is not displayed — a hard rule, not a preference.
create table "ExcerptRefs" (
  "Id"          uuid primary key default gen_random_uuid(),
  "SourceType"  text        not null,
  "SourceId"    uuid        not null,
  "OffsetStart" integer,
  "OffsetEnd"   integer,
  "DatedAt"     timestamptz not null,
  constraint "ExcerptRefs_SourceType_ck" check ("SourceType" in ('note','transcript','document')),
  constraint "ExcerptRefs_Offsets_ck"    check ("OffsetEnd" is null or "OffsetStart" is null or "OffsetEnd" >= "OffsetStart")
);
create index "ExcerptRefs_Source_idx" on "ExcerptRefs" ("SourceType", "SourceId");


create table "ThemeTags" (
  "Id"           uuid primary key default gen_random_uuid(),
  "NoteId"       uuid        not null references "Notes"("Id") on delete cascade,
  "TermId"       uuid        not null references "ThemeTerms"("Id") on delete restrict,
  "ExcerptRefId" uuid        references "ExcerptRefs"("Id") on delete set null,
  "Confidence"   numeric(4,3),
  "CreatedBy"    text        not null default 'ai',
  "CreatedAt"    timestamptz not null default now(),
  "RemovedAt"    timestamptz,
  constraint "ThemeTags_CreatedBy_ck" check ("CreatedBy" in ('ai','human'))
);
create index "ThemeTags_NoteId_idx" on "ThemeTags" ("NoteId");
create index "ThemeTags_TermId_idx" on "ThemeTags" ("TermId") where "RemovedAt" is null;


-- Quotes the professional recorded. Copy must say so: the system never listened
-- to the patient in the default flow.
create table "PatientQuotes" (
  "Id"           uuid primary key default gen_random_uuid(),
  "NoteId"       uuid        not null references "Notes"("Id") on delete cascade,
  "TermId"       uuid        references "ThemeTerms"("Id") on delete set null,
  "Text"         text        not null,
  "ExcerptRefId" uuid        references "ExcerptRefs"("Id") on delete set null,
  "RecordedAt"   timestamptz not null
);
create index "PatientQuotes_NoteId_idx" on "PatientQuotes" ("NoteId");


-- =============================================================================
--  D — DOCUMENT COMPLIANCE  (F-05, F-06)
-- =============================================================================

-- "Elements" is what the document MUST contain; "Prohibitions" is what it must
-- NOT contain. Both are needed: the most common breach of Res. CFP 006/2019 is
-- a declaração that records symptoms (Art. 9, §1), and a presence-only checker
-- is structurally incapable of seeing it.
create table "ConformityRulesets" (
  "Id"            uuid primary key default gen_random_uuid(),
  "DocumentKind"  text    not null,
  "Version"       text    not null,
  "Elements"      jsonb   not null,
  "Prohibitions"  jsonb   not null default '[]'::jsonb,
  "SourceRef"     text    not null default 'Res. CFP 006/2019',
  "ArticleRef"    text,
  "EffectiveFrom" date    not null,
  constraint "ConformityRulesets_Kind_Version_key" unique ("DocumentKind", "Version"),
  constraint "ConformityRulesets_Kind_ck" check ("DocumentKind" in ('declaracao','atestado','relatorio',
                                                                   'relatorio_multiprofissional','laudo','parecer')),
  constraint "ConformityRulesets_Elements_ck"     check (jsonb_typeof("Elements") = 'array'),
  constraint "ConformityRulesets_Prohibitions_ck" check (jsonb_typeof("Prohibitions") = 'array')
);


create table "ConformityChecks" (
  "Id"              uuid primary key default gen_random_uuid(),
  "DocumentId"      uuid        not null references "ClinicalDocuments"("Id") on delete cascade,
  "RulesetId"       uuid        not null references "ConformityRulesets"("Id") on delete restrict,
  "MissingElements" jsonb       not null default '[]'::jsonb,
  -- Prohibited content the checker believes it found. Separate from
  -- "MissingElements" because the two demand different wording to the
  -- professional: one is "falta", the other is "não pode constar".
  "ForbiddenFound"  jsonb       not null default '[]'::jsonb,
  "Mode"            text        not null default 'advisory',
  "Passed"          boolean     not null,
  "CheckedAt"       timestamptz not null default now(),
  constraint "ConformityChecks_Mode_ck" check ("Mode" in ('blocking','advisory'))
);
create index "ConformityChecks_DocumentId_idx" on "ConformityChecks" ("DocumentId");


create table "RetentionRules" (
  "Id"         uuid primary key default gen_random_uuid(),
  "ProviderId" uuid    not null references "Providers"("Id") on delete cascade,
  "RecordKind" text    not null,
  "GuardYears" integer not null default 5,
  "Basis"      text    not null default 'Res. CFP 001/2009',
  constraint "RetentionRules_Provider_Kind_key" unique ("ProviderId", "RecordKind"),
  constraint "RetentionRules_GuardYears_ck"     check ("GuardYears" >= 5)
);


create table "DisposalRecords" (
  "Id"            uuid primary key default gen_random_uuid(),
  "PatientId"     uuid        not null references "Patients"("Id") on delete restrict,
  "DocumentId"    uuid        references "ClinicalDocuments"("Id") on delete set null,
  "DisposedAt"    timestamptz not null default now(),
  "TermObjectKey" text,
  "PerformedBy"   uuid
);
create index "DisposalRecords_PatientId_idx" on "DisposalRecords" ("PatientId");


-- =============================================================================
--  E — EXTERNAL DOCUMENTS  (F-07 .. F-09)
-- =============================================================================

create table "ExternalDocuments" (
  "Id"         uuid primary key default gen_random_uuid(),
  "PatientId"  uuid        not null,
  "ProviderId" uuid        not null,
  "Filename"   text        not null,
  "MimeType"   text        not null,
  "ObjectKey"  text        not null,
  "ByteSize"   bigint      not null,
  "ScanStatus" text        not null default 'pending',
  "UploadedAt" timestamptz not null default now(),
  constraint "ExternalDocuments_Patient_fk" foreign key ("PatientId", "ProviderId")
                                            references "Patients" ("Id", "ProviderId") on delete restrict,
  constraint "ExternalDocuments_ObjectKey_key" unique ("ObjectKey"),
  constraint "ExternalDocuments_ScanStatus_ck" check ("ScanStatus" in ('pending','clean','infected','failed'))
);
create index "ExternalDocuments_PatientId_idx" on "ExternalDocuments" ("PatientId");


-- Extracts what a document STATES. CorrectedValue sits beside Value rather than
-- overwriting it: what the machine read and what the professional confirmed are
-- two different facts.
create table "ExtractedFacts" (
  "Id"                 uuid primary key default gen_random_uuid(),
  "ExternalDocumentId" uuid        not null references "ExternalDocuments"("Id") on delete cascade,
  "Kind"               text        not null,
  "Value"              text        not null,
  "CorrectedValue"     text,
  "PageRef"            integer,
  "ExcerptRefId"       uuid        references "ExcerptRefs"("Id") on delete set null,
  "ConfirmedBy"        uuid,
  "ConfirmedAt"        timestamptz,
  constraint "ExtractedFacts_Kind_ck" check ("Kind" in ('issuer','registration','issue_date','icd','medication','conclusion'))
);
create index "ExtractedFacts_DocumentId_idx" on "ExtractedFacts" ("ExternalDocumentId");


create table "MedicationEntries" (
  "Id"           uuid primary key default gen_random_uuid(),
  "PatientId"    uuid        not null references "Patients"("Id") on delete cascade,
  "Drug"         text        not null,
  "Dose"         text,
  "Posology"     text,
  "StartedOn"    date        not null,
  "EndedOn"      date,
  "SourceFactId" uuid        references "ExtractedFacts"("Id") on delete set null,
  constraint "MedicationEntries_Dates_ck" check ("EndedOn" is null or "EndedOn" >= "StartedOn")
);
create index "MedicationEntries_Patient_Started_idx" on "MedicationEntries" ("PatientId", "StartedOn");


-- =============================================================================
--  F — SECURE SHARING  (F-17, F-18)
-- =============================================================================

create table "DisclosureAuthorizations" (
  "Id"            uuid primary key default gen_random_uuid(),
  "DocumentId"    uuid        not null references "ClinicalDocuments"("Id") on delete cascade,
  "PatientId"     uuid        not null references "Patients"("Id") on delete restrict,
  "RecipientName" text        not null,
  "RecipientRole" text        not null,
  "Purpose"       text        not null,
  "AuthorizedAt"  timestamptz not null,
  "RecordedBy"    uuid        not null
);
create index "DisclosureAuthorizations_DocumentId_idx" on "DisclosureAuthorizations" ("DocumentId");


create table "ShareLinks" (
  "Id"                    uuid primary key default gen_random_uuid(),
  "DocumentId"            uuid        not null references "ClinicalDocuments"("Id") on delete cascade,
  -- Sharing is hard-blocked without an authorisation record: NOT NULL is the
  -- enforcement, not a UI check.
  "AuthorizationId"       uuid        not null references "DisclosureAuthorizations"("Id") on delete restrict,
  "TokenHash"             text        not null,
  "PasswordHash"          text        not null,
  "ExpiresAt"             timestamptz not null,
  "ViewCap"               integer,
  "ViewsUsed"             integer     not null default 0,
  "DownloadEnabled"       boolean     not null default false,
  "RevokedAt"             timestamptz,
  "CreatedAt"             timestamptz not null default now(),
  constraint "ShareLinks_TokenHash_key" unique ("TokenHash"),
  -- Mandatory expiry, 30 days maximum.
  constraint "ShareLinks_Expiry_ck"     check ("ExpiresAt" > "CreatedAt" and "ExpiresAt" <= "CreatedAt" + interval '30 days'),
  constraint "ShareLinks_ViewCap_ck"    check ("ViewCap" is null or "ViewCap" > 0)
);
create index "ShareLinks_DocumentId_idx" on "ShareLinks" ("DocumentId");


create table "ShareAccesses" (
  "Id"          uuid primary key default gen_random_uuid(),
  "ShareLinkId" uuid        not null references "ShareLinks"("Id") on delete cascade,
  "At"          timestamptz not null default now(),
  "Outcome"     text        not null,
  "CoarseGeo"   text,
  "UserAgent"   text,
  constraint "ShareAccesses_Outcome_ck" check ("Outcome" in ('opened','bad_password','expired','revoked','cap_reached'))
);
create index "ShareAccesses_ShareLinkId_At_idx" on "ShareAccesses" ("ShareLinkId", "At" desc);


-- =============================================================================
--  G — BILLING  (F-19 .. F-22)
-- =============================================================================

-- Was a JSONB array inside "Notes". Extracted because the denial history in
-- F-22 counts rejection reasons across the whole practice.
create table "BillingCodes" (
  "Id"            uuid primary key default gen_random_uuid(),
  "NoteId"        uuid        not null references "Notes"("Id") on delete cascade,
  "System"        integer     not null default 2,
  "ProcedureCode" text        not null,
  "DiagnosisCode" text,
  "Description"   text,
  "Confidence"    numeric(4,3),
  "DenialRisk"    integer,
  "Modifiers"     text[]      not null default '{}',
  "ConfirmedBy"   uuid,
  "ConfirmedAt"   timestamptz,
  constraint "BillingCodes_DenialRisk_ck" check ("DenialRisk" is null or "DenialRisk" between 0 and 100)
);
create index "BillingCodes_NoteId_idx" on "BillingCodes" ("NoteId");
create index "BillingCodes_ProcedureCode_idx" on "BillingCodes" ("ProcedureCode");


create table "Insurers" (
  "Id"          uuid primary key default gen_random_uuid(),
  "ProviderId"  uuid not null references "Providers"("Id") on delete cascade,
  "Name"        text not null,
  "AnsRegistry" text,
  "PortalUrl"   text,
  constraint "Insurers_Provider_Name_key" unique ("ProviderId", "Name")
);


create table "InsurerAuthorizations" (
  "Id"                  uuid primary key default gen_random_uuid(),
  "PatientId"           uuid    not null references "Patients"("Id") on delete cascade,
  "InsurerId"           uuid    not null references "Insurers"("Id") on delete restrict,
  "Number"              text    not null,
  "SessionsAuthorized"  integer not null,
  "SessionsPerformed"   integer not null default 0,
  "ValidFrom"           date    not null,
  "ValidUntil"          date    not null,
  constraint "InsurerAuthorizations_Sessions_ck" check ("SessionsPerformed" >= 0 and "SessionsAuthorized" > 0),
  constraint "InsurerAuthorizations_Dates_ck"    check ("ValidUntil" >= "ValidFrom")
);
create index "InsurerAuthorizations_ValidUntil_idx" on "InsurerAuthorizations" ("ValidUntil");


create table "TissGuides" (
  "Id"              uuid primary key default gen_random_uuid(),
  "NoteId"          uuid        not null references "Notes"("Id") on delete restrict,
  "SessionId"       uuid        references "Sessions"("Id") on delete set null,
  "InsurerId"       uuid        not null references "Insurers"("Id") on delete restrict,
  "AuthorizationId" uuid        references "InsurerAuthorizations"("Id") on delete set null,
  "GuideNumber"     text,
  "Payload"         jsonb,
  "Status"          text        not null default 'draft',
  "SubmittedAt"     timestamptz,
  constraint "TissGuides_Status_ck" check ("Status" in ('draft','ready','submitted','paid','denied'))
);
create index "TissGuides_NoteId_idx"    on "TissGuides" ("NoteId");
create index "TissGuides_InsurerId_idx" on "TissGuides" ("InsurerId");


create table "DenialFlags" (
  "Id"          uuid primary key default gen_random_uuid(),
  "GuideId"     uuid        not null references "TissGuides"("Id") on delete cascade,
  "RuleKey"     text        not null,
  "Message"     text        not null,
  "RaisedAt"    timestamptz not null default now(),
  "DismissedAt" timestamptz
);
create index "DenialFlags_GuideId_idx" on "DenialFlags" ("GuideId");


create table "Denials" (
  "Id"         uuid primary key default gen_random_uuid(),
  "GuideId"    uuid        not null references "TissGuides"("Id") on delete cascade,
  "ReasonCode" text        not null,
  "ReasonText" text,
  "ReceivedAt" timestamptz not null default now(),
  "ResolvedAt" timestamptz
);
create index "Denials_ReasonCode_idx" on "Denials" ("ReasonCode");


-- =============================================================================
--  H — CAPTURE AND CONSENT  (F-23 .. F-28)
-- =============================================================================

create table "RecordingConsents" (
  "Id"                uuid primary key default gen_random_uuid(),
  "PatientId"         uuid        not null references "Patients"("Id") on delete cascade,
  "TreatmentPlanId"   uuid        references "TreatmentPlans"("Id") on delete set null,
  "State"             text        not null default 'NOT_REQUESTED',
  "TemplateVersion"   text,
  "IssuedAt"          timestamptz,
  "SignedAt"          timestamptz,
  "SignatureEvidence" text,
  "ValidUntil"        timestamptz,
  "RevokedAt"         timestamptz,
  "GuardianName"      text,
  "AssentRecordedAt"  timestamptz,
  constraint "RecordingConsents_State_ck"  check ("State" in ('NOT_REQUESTED','SENT','SIGNED','REVOKED','EXPIRED')),
  -- Recording is a pure function of state: SIGNED requires signature evidence.
  constraint "RecordingConsents_Signed_ck" check ("State" <> 'SIGNED' or ("SignedAt" is not null and "SignatureEvidence" is not null))
);
create index "RecordingConsents_PatientId_idx" on "RecordingConsents" ("PatientId");


create table "ConsentEvents" (
  "Id"        uuid primary key default gen_random_uuid(),
  "ConsentId" uuid        not null references "RecordingConsents"("Id") on delete cascade,
  "FromState" text        not null,
  "ToState"   text        not null,
  "At"        timestamptz not null default now(),
  "ActorId"   uuid,
  "Reason"    text
);
create index "ConsentEvents_ConsentId_At_idx" on "ConsentEvents" ("ConsentId", "At");


-- Source material, never the record. Never exported, never shared, never in a
-- dossier — enforced in application code and in the export queries (F-28).
create table "Transcripts" (
  "Id"             uuid primary key default gen_random_uuid(),
  "SessionId"      uuid        not null references "Sessions"("Id") on delete cascade,
  "NoteId"         uuid        references "Notes"("Id") on delete set null,
  "Text"           text        not null,
  "RetentionUntil" date,
  "CreatedAt"      timestamptz not null default now(),
  "DestroyedAt"    timestamptz,
  constraint "Transcripts_SessionId_key" unique ("SessionId")
);


-- Records the FACT of destruction, never the content. This row is the evidence
-- that no audio survived (F-26).
create table "AudioDestructionLogs" (
  "Id"          uuid primary key default gen_random_uuid(),
  "SessionId"   uuid        not null references "Sessions"("Id") on delete cascade,
  "DestroyedAt" timestamptz not null default now(),
  "Outcome"     text        not null,
  constraint "AudioDestructionLogs_Outcome_ck" check ("Outcome" in ('transcribed','failed'))
);
create index "AudioDestructionLogs_SessionId_idx" on "AudioDestructionLogs" ("SessionId");


-- =============================================================================
--  I — OWN CALENDAR, PATIENT PORTAL, INTAKE  (F-29 .. F-31, F-61, F-62)
-- =============================================================================

-- Separate from "Patients" on purpose: the chart carries a five-year retention
-- obligation, a login credential does not. Deleting an account must never
-- collide with Res. CFP 001/2009.
create table "PatientAccounts" (
  "Id"              uuid primary key default gen_random_uuid(),
  "PatientId"       uuid        not null references "Patients"("Id") on delete cascade,
  "Email"           text        not null,
  "Phone"           text,
  "AuthMethod"      text        not null default 'magic_link',
  "EmailVerifiedAt" timestamptz,
  "LastLoginAt"     timestamptz,
  "FailedAttempts"  integer     not null default 0,
  "LockedUntil"     timestamptz,
  "TermsAcceptedAt" timestamptz,
  "DisabledAt"      timestamptz,
  "CreatedAt"       timestamptz not null default now(),
  constraint "PatientAccounts_PatientId_key" unique ("PatientId"),
  constraint "PatientAccounts_Email_key"     unique ("Email"),
  constraint "PatientAccounts_AuthMethod_ck" check ("AuthMethod" in ('magic_link','otp','password'))
);


create table "PatientAuthTokens" (
  "Id"        uuid primary key default gen_random_uuid(),
  "AccountId" uuid        not null references "PatientAccounts"("Id") on delete cascade,
  "TokenHash" text        not null,
  "Purpose"   text        not null,
  "ExpiresAt" timestamptz not null,
  "UsedAt"    timestamptz,
  "CreatedIp" inet,
  "CreatedAt" timestamptz not null default now(),
  constraint "PatientAuthTokens_TokenHash_key" unique ("TokenHash"),
  constraint "PatientAuthTokens_Purpose_ck"    check ("Purpose" in ('login','verify_email','cancel_appointment'))
);
create index "PatientAuthTokens_ExpiresAt_idx" on "PatientAuthTokens" ("ExpiresAt") where "UsedAt" is null;


create table "BookingPolicies" (
  "Id"                   uuid primary key default gen_random_uuid(),
  "ProviderId"           uuid    not null references "Providers"("Id") on delete cascade,
  "MinLeadHours"         integer not null default 24,
  "MaxHorizonDays"       integer not null default 60,
  "CancelLeadHours"      integer not null default 24,
  "RescheduleLeadHours"  integer not null default 24,
  "MaxOpenAppointments"  integer not null default 4,
  "AllowNewPatients"     boolean not null default false,
  "RequiresConfirmation" boolean not null default true,
  constraint "BookingPolicies_ProviderId_key" unique ("ProviderId"),
  constraint "BookingPolicies_Windows_ck" check (
    "MinLeadHours" >= 0 and "MaxHorizonDays" > 0 and
    "CancelLeadHours" >= 0 and "RescheduleLeadHours" >= 0 and "MaxOpenAppointments" > 0)
);


create table "AvailabilityRules" (
  "Id"            uuid primary key default gen_random_uuid(),
  "ProviderId"    uuid    not null references "Providers"("Id") on delete cascade,
  "Weekday"       integer not null,             -- 0 = Sunday
  "StartsAtTime"  time    not null,
  "EndsAtTime"    time    not null,
  "SlotMinutes"   integer not null default 50,
  "EffectiveFrom" date    not null,
  "EffectiveTo"   date,
  "Timezone"      text    not null default 'America/Sao_Paulo',
  constraint "AvailabilityRules_Weekday_ck" check ("Weekday" between 0 and 6),
  constraint "AvailabilityRules_Time_ck"    check ("EndsAtTime" > "StartsAtTime"),
  constraint "AvailabilityRules_Slot_ck"    check ("SlotMinutes" > 0),
  constraint "AvailabilityRules_Range_ck"   check ("EffectiveTo" is null or "EffectiveTo" >= "EffectiveFrom")
);
create index "AvailabilityRules_Provider_idx" on "AvailabilityRules" ("ProviderId", "Weekday");


create table "AvailabilityExceptions" (
  "Id"         uuid primary key default gen_random_uuid(),
  "ProviderId" uuid        not null references "Providers"("Id") on delete cascade,
  "StartsAt"   timestamptz not null,
  "EndsAt"     timestamptz not null,
  "AllDay"     boolean     not null default false,
  "Kind"       text        not null default 'block',
  "Reason"     text,
  constraint "AvailabilityExceptions_Range_ck" check ("EndsAt" > "StartsAt"),
  constraint "AvailabilityExceptions_Kind_ck"  check ("Kind" in ('block','vacation','holiday'))
);
create index "AvailabilityExceptions_Provider_Range_idx"
  on "AvailabilityExceptions" ("ProviderId", "StartsAt", "EndsAt");


create table "AppointmentSeries" (
  "Id"              uuid primary key default gen_random_uuid(),
  "PatientId"       uuid        not null,
  "ProviderId"      uuid        not null,
  "RecurrenceRule"  text        not null,        -- RRULE
  "StartsOn"        date        not null,
  "EndsOn"          date,
  "DefaultMinutes"  integer     not null default 50,
  "DefaultModality" text        not null default 'online',
  "CancelledAt"     timestamptz,
  constraint "AppointmentSeries_Patient_fk" foreign key ("PatientId", "ProviderId")
                                            references "Patients" ("Id", "ProviderId") on delete cascade,
  constraint "AppointmentSeries_Modality_ck" check ("DefaultModality" in ('presencial','online'))
);
create index "AppointmentSeries_ProviderId_idx" on "AppointmentSeries" ("ProviderId");


-- Declared before "ServiceOfferings" and "BookingHolds"; those FKs are added
-- once the referenced tables exist.
create table "Appointments" (
  "Id"                  uuid primary key default gen_random_uuid(),
  "ProviderId"          uuid        not null,
  "PatientId"           uuid        not null,
  "SeriesId"            uuid        references "AppointmentSeries"("Id") on delete set null,
  "SessionId"           uuid        references "Sessions"("Id") on delete set null,
  "HoldId"              uuid,
  "OfferingId"          uuid,
  "ConnectionId"        uuid,
  "StartsAt"            timestamptz not null,
  "EndsAt"              timestamptz not null,
  "Timezone"            text        not null default 'America/Sao_Paulo',
  "Modality"            text        not null default 'online',
  "Status"              text        not null default 'scheduled',
  "CreatedBy"           text        not null default 'provider',
  -- The price in force is copied here at booking time. Raising a price must not
  -- rewrite what an already-booked patient agreed to pay (F-63).
  "PriceCentsSnapshot"  integer,
  "OverridesSeries"     boolean     not null default false,
  "ExternalEventId"     text,
  "CancelledAt"         timestamptz,
  "CancelledBy"         text,
  "CancellationReason"  text,
  "CreatedAt"           timestamptz not null default now(),
  constraint "Appointments_Patient_fk"  foreign key ("PatientId", "ProviderId")
                                        references "Patients" ("Id", "ProviderId") on delete restrict,
  constraint "Appointments_Range_ck"    check ("EndsAt" > "StartsAt"),
  constraint "Appointments_Status_ck"   check ("Status" in ('scheduled','confirmed','attended','cancelled','no_show')),
  constraint "Appointments_CreatedBy_ck" check ("CreatedBy" in ('provider','patient')),
  constraint "Appointments_CancelledBy_ck" check ("CancelledBy" is null or "CancelledBy" in ('provider','patient','system')),
  constraint "Appointments_Cancel_ck"   check (("Status" = 'cancelled') = ("CancelledAt" is not null)),
  constraint "Appointments_Price_ck"    check ("PriceCentsSnapshot" is null or "PriceCentsSnapshot" >= 0)
);
create index "Appointments_Provider_Starts_idx" on "Appointments" ("ProviderId", "StartsAt");
create index "Appointments_Patient_Starts_idx"  on "Appointments" ("PatientId", "StartsAt");
create index "Appointments_ExternalEventId_idx" on "Appointments" ("ExternalEventId")
  where "ExternalEventId" is not null;

-- Two providers cannot be double-booked for the same instant. Checking in
-- application code before writing loses the race when two patients click at the
-- same moment; this is the only guarantee that holds under concurrency (F-62).
alter table "Appointments"
  add constraint "Appointments_no_overlap"
  exclude using gist (
    "ProviderId" with =,
    tstzrange("StartsAt", "EndsAt", '[)') with &&
  ) where ("Status" in ('scheduled','confirmed'));


create table "BookingHolds" (
  "Id"                     uuid primary key default gen_random_uuid(),
  "ProviderId"             uuid        not null references "Providers"("Id") on delete cascade,
  "PatientId"              uuid        references "Patients"("Id") on delete cascade,
  "StartsAt"               timestamptz not null,
  "EndsAt"                 timestamptz not null,
  "ExpiresAt"              timestamptz not null,
  "CreatedBy"              text        not null default 'patient',
  "ConvertedAppointmentId" uuid        references "Appointments"("Id") on delete set null,
  "ReleasedAt"             timestamptz,
  "CreatedAt"              timestamptz not null default now(),
  constraint "BookingHolds_Range_ck"     check ("EndsAt" > "StartsAt"),
  constraint "BookingHolds_CreatedBy_ck" check ("CreatedBy" in ('provider','patient'))
);
create index "BookingHolds_Provider_Starts_idx" on "BookingHolds" ("ProviderId", "StartsAt");
create index "BookingHolds_ExpiresAt_idx" on "BookingHolds" ("ExpiresAt")
  where "ReleasedAt" is null and "ConvertedAppointmentId" is null;

-- Holds cannot overlap each other.
-- NOTE: the predicate cannot test ExpiresAt > now() — now() is not immutable and
-- is not allowed in a constraint predicate. So this constraint treats an expired
-- hold as still live, and a second hold on that slot is refused until the
-- sweeper releases the first. The cross-table trigger below DOES filter on
-- expiry, so an expired hold never blocks an actual appointment — only another
-- hold. Verified behaviour, not an assumption.
-- Run anamnys_release_expired_holds() on a schedule (see 03_jobs.sql).
alter table "BookingHolds"
  add constraint "BookingHolds_no_overlap"
  exclude using gist (
    "ProviderId" with =,
    tstzrange("StartsAt", "EndsAt", '[)') with &&
  ) where ("ReleasedAt" is null and "ConvertedAppointmentId" is null);

alter table "Appointments"
  add constraint "Appointments_Hold_fk" foreign key ("HoldId")
  references "BookingHolds"("Id") on delete set null;

-- Exclusion constraints are per-table, so neither of the two above stops a hold
-- from landing on top of an existing appointment. This trigger closes that gap
-- in both directions.
create or replace function anamnys_check_slot_conflict() returns trigger
language plpgsql as $$
declare
  conflict_count integer;
begin
  if tg_table_name = 'BookingHolds' then
    if new."ReleasedAt" is not null or new."ConvertedAppointmentId" is not null then
      return new;
    end if;
    select count(*) into conflict_count
      from "Appointments" a
     where a."ProviderId" = new."ProviderId"
       and a."Status" in ('scheduled','confirmed')
       and tstzrange(a."StartsAt", a."EndsAt", '[)')
           && tstzrange(new."StartsAt", new."EndsAt", '[)');
    if conflict_count > 0 then
      raise exception 'Slot already taken by an appointment for this provider.'
        using errcode = 'exclusion_violation';
    end if;
  else
    if new."Status" not in ('scheduled','confirmed') then
      return new;
    end if;
    select count(*) into conflict_count
      from "BookingHolds" h
     where h."ProviderId" = new."ProviderId"
       and h."ReleasedAt" is null
       and h."ConvertedAppointmentId" is null
       and h."ExpiresAt" > now()
       and (new."HoldId" is null or h."Id" <> new."HoldId")
       and tstzrange(h."StartsAt", h."EndsAt", '[)')
           && tstzrange(new."StartsAt", new."EndsAt", '[)');
    if conflict_count > 0 then
      raise exception 'Slot is held by another booking in progress.'
        using errcode = 'exclusion_violation';
    end if;
  end if;
  return new;
end;
$$;

create trigger "BookingHolds_slot_conflict" before insert or update on "BookingHolds"
  for each row execute function anamnys_check_slot_conflict();
create trigger "Appointments_slot_conflict" before insert or update on "Appointments"
  for each row execute function anamnys_check_slot_conflict();


-- Optional mirror. Everything works with no row here at all (F-29).
create table "CalendarConnections" (
  "Id"                 uuid primary key default gen_random_uuid(),
  "ProviderId"         uuid        not null references "Providers"("Id") on delete cascade,
  "AccountEmail"       text        not null,
  "RefreshToken"       text        not null,      -- encrypted at rest by the application
  "SyncToken"          text,
  "ChannelId"          text,
  "ChannelResourceId"  text,
  -- Google's notification channel expires in days. Without a renewal job the
  -- sync dies silently; watch this column and alert.
  "ChannelExpiresAt"   timestamptz,
  "Direction"          text        not null default 'two_way',
  "ConnectedAt"        timestamptz not null default now(),
  "RevokedAt"          timestamptz,
  constraint "CalendarConnections_ProviderId_key" unique ("ProviderId"),
  constraint "CalendarConnections_Direction_ck"   check ("Direction" in ('push_only','two_way'))
);
create index "CalendarConnections_ChannelExpiresAt_idx"
  on "CalendarConnections" ("ChannelExpiresAt") where "RevokedAt" is null;

alter table "Appointments"
  add constraint "Appointments_Connection_fk" foreign key ("ConnectionId")
  references "CalendarConnections"("Id") on delete set null;


-- Single outbox for everything with a promised deadline: trial expiry, dunning,
-- retention, authorisation balance, unsigned drafts. The Terms of Use fix exact
-- day counts, so we must be able to prove a notice went out and when.
create table "Notifications" (
  "Id"             uuid primary key default gen_random_uuid(),
  "ProviderId"     uuid        not null references "Providers"("Id") on delete cascade,
  "Kind"           text        not null,
  "SubjectType"    text,
  "SubjectId"      uuid,
  "Channel"        text        not null default 'email',
  "ScheduledFor"   timestamptz not null,
  "SentAt"         timestamptz,
  "DeliveryStatus" text        not null default 'pending',
  "ReadAt"         timestamptz,
  constraint "Notifications_Channel_ck" check ("Channel" in ('email','push','in_app')),
  constraint "Notifications_Status_ck"  check ("DeliveryStatus" in ('pending','sent','failed','cancelled'))
);
create index "Notifications_due_idx" on "Notifications" ("ScheduledFor")
  where "DeliveryStatus" = 'pending';


-- Patient-facing. Stricter content rule than "Notifications": no clinical
-- content, ever. The document link may travel here; the password may not.
create table "Reminders" (
  "Id"             uuid primary key default gen_random_uuid(),
  "AppointmentId"  uuid        not null references "Appointments"("Id") on delete cascade,
  "Channel"        text        not null default 'whatsapp',
  "TemplateKey"    text        not null,
  "ScheduledFor"   timestamptz not null,
  "SentAt"         timestamptz,
  "DeliveryStatus" text        not null default 'pending',
  constraint "Reminders_Channel_ck" check ("Channel" in ('whatsapp','email','sms')),
  constraint "Reminders_Status_ck"  check ("DeliveryStatus" in ('pending','sent','failed','cancelled'))
);
create index "Reminders_due_idx" on "Reminders" ("ScheduledFor") where "DeliveryStatus" = 'pending';


create table "IntakeTemplates" (
  "Id"         uuid primary key default gen_random_uuid(),
  "ProviderId" uuid        not null references "Providers"("Id") on delete cascade,
  "Name"       text        not null,
  "Version"    text        not null default '1',
  "Active"     boolean     not null default true,
  "CreatedAt"  timestamptz not null default now(),
  constraint "IntakeTemplates_Provider_Name_Version_key" unique ("ProviderId", "Name", "Version")
);


create table "IntakeQuestions" (
  "Id"          uuid primary key default gen_random_uuid(),
  "TemplateId"  uuid    not null references "IntakeTemplates"("Id") on delete cascade,
  "Key"         text    not null,
  "Label"       text    not null,
  "AnswerType"  text    not null default 'text',
  "Position"    integer not null default 0,
  -- The chart field the answer files itself into. This link is what delivers
  -- "no re-typing" in F-31.
  "TargetField" text,
  "Required"    boolean not null default false,
  constraint "IntakeQuestions_Template_Key_key" unique ("TemplateId", "Key"),
  constraint "IntakeQuestions_AnswerType_ck"    check ("AnswerType" in ('text','number','date','boolean','choice'))
);


create table "IntakeResponses" (
  "Id"         uuid primary key default gen_random_uuid(),
  "PatientId"  uuid        not null references "Patients"("Id") on delete cascade,
  "SessionId"  uuid        references "Sessions"("Id") on delete set null,
  "QuestionId" uuid        not null references "IntakeQuestions"("Id") on delete restrict,
  "Answer"     text,
  "RecordedAt" timestamptz not null default now()
);
create index "IntakeResponses_PatientId_idx" on "IntakeResponses" ("PatientId");


-- =============================================================================
--  J — PUBLIC PROFILE AND DISCOVERY  (F-63, F-64)
-- =============================================================================

-- ListingState starts at never_listed. Appearing in the directory is an
-- affirmative act, never a side effect of having filled in a profile.
create table "ProviderProfiles" (
  "Id"                  uuid primary key default gen_random_uuid(),
  "ProviderId"          uuid        not null references "Providers"("Id") on delete cascade,
  "Slug"                text        not null,
  "DisplayName"         text        not null,
  "Headline"            text,
  "Bio"                 text,
  "PhotoObjectKey"      text,
  "Languages"           text[]      not null default '{pt-BR}',
  "Modalities"          text[]      not null default '{online}',
  "City"                text,
  "State"               text,
  "ListingState"        text        not null default 'never_listed',
  "ListingOptedInAt"    timestamptz,
  "ListingTermsVersion" text,
  "ListingWithdrawnAt"  timestamptz,
  "CreatedAt"           timestamptz not null default now(),
  "UpdatedAt"           timestamptz not null default now(),
  constraint "ProviderProfiles_ProviderId_key" unique ("ProviderId"),
  constraint "ProviderProfiles_Slug_key"       unique ("Slug"),
  constraint "ProviderProfiles_ListingState_ck" check ("ListingState" in ('never_listed','listed','withdrawn')),
  -- Being listed requires a recorded affirmative act and the version of the
  -- text accepted. Opt-in is enforced here, not only in the UI.
  constraint "ProviderProfiles_OptIn_ck" check (
    "ListingState" <> 'listed' or ("ListingOptedInAt" is not null and "ListingTermsVersion" is not null))
);
create index "ProviderProfiles_listed_idx" on "ProviderProfiles" ("City", "State")
  where "ListingState" = 'listed';
create trigger "ProviderProfiles_touch" before update on "ProviderProfiles"
  for each row execute function anamnys_touch_updated_at();


create table "ProfileListingEvents" (
  "Id"           uuid primary key default gen_random_uuid(),
  "ProfileId"    uuid        not null references "ProviderProfiles"("Id") on delete cascade,
  "FromState"    text        not null,
  "ToState"      text        not null,
  "At"           timestamptz not null default now(),
  "ActorId"      uuid,
  "TermsVersion" text,
  "Reason"       text
);
create index "ProfileListingEvents_ProfileId_At_idx" on "ProfileListingEvents" ("ProfileId", "At");


-- Closed, versioned vocabulary. Free text does not aggregate: "depressão",
-- "transtorno depressivo" and "humor deprimido" must not become three filters.
create table "FocusAreas" (
  "Id"                uuid primary key default gen_random_uuid(),
  "Code"              text    not null,
  "Label"             text    not null,
  "ParentId"          uuid    references "FocusAreas"("Id") on delete set null,
  "VocabularyVersion" text    not null default '1',
  "Active"            boolean not null default true,
  constraint "FocusAreas_Code_key" unique ("Code")
);


create table "ProviderFocusAreas" (
  "ProfileId"    uuid    not null references "ProviderProfiles"("Id") on delete cascade,
  "FocusAreaId"  uuid    not null references "FocusAreas"("Id") on delete restrict,
  "DisplayOrder" integer not null default 0,
  primary key ("ProfileId", "FocusAreaId")
);
create index "ProviderFocusAreas_FocusAreaId_idx" on "ProviderFocusAreas" ("FocusAreaId");


-- Separate from "FocusAreas" on purpose. "Especialista" is a regulated term:
-- only a CFP-registered specialist title may be advertised as one, and the
-- recognised list does not contain topics like depression. Treating a topic is
-- not being a specialist in it — the schema keeps them apart so the UI cannot
-- merge them by accident.
create table "SpecialistTitles" (
  "Id"                uuid primary key default gen_random_uuid(),
  "ProviderId"        uuid        not null references "Providers"("Id") on delete cascade,
  "Title"             text        not null,
  "GrantedAt"         date,
  "RegistryRef"       text,
  "EvidenceObjectKey" text,
  "VerifiedAt"        timestamptz,
  constraint "SpecialistTitles_Provider_Title_key" unique ("ProviderId", "Title")
);


create table "ServiceOfferings" (
  "Id"              uuid primary key default gen_random_uuid(),
  "ProviderId"      uuid        not null references "Providers"("Id") on delete cascade,
  "Kind"            text        not null,
  "DurationMinutes" integer     not null default 50,
  "PriceCents"      integer     not null,
  "Currency"        char(3)     not null default 'BRL',
  "IsPublic"        boolean     not null default true,
  "Active"          boolean     not null default true,
  "EffectiveFrom"   date        not null default current_date,
  "EffectiveTo"     date,
  constraint "ServiceOfferings_Price_ck"    check ("PriceCents" >= 0),
  constraint "ServiceOfferings_Duration_ck" check ("DurationMinutes" > 0),
  constraint "ServiceOfferings_Range_ck"    check ("EffectiveTo" is null or "EffectiveTo" >= "EffectiveFrom"),
  constraint "ServiceOfferings_Kind_ck"     check ("Kind" in ('first_consultation','session','couple','document','group'))
);
create index "ServiceOfferings_Provider_idx" on "ServiceOfferings" ("ProviderId") where "Active";

alter table "Appointments"
  add constraint "Appointments_Offering_fk" foreign key ("OfferingId")
  references "ServiceOfferings"("Id") on delete set null;


-- =============================================================================
--  K — SUBSCRIPTION AND ENTITLEMENT  (F-50 .. F-60)
-- =============================================================================

-- Plans are DATA, not code constants. A plan version is immutable once sold:
-- changing what a plan contains must never alter what an existing subscriber
-- already bought.
create table "Plans" (
  "Id"             uuid primary key default gen_random_uuid(),
  "Code"           text    not null,
  "Name"           text    not null,
  "Version"        integer not null default 1,
  "Active"         boolean not null default true,
  "MonthlyPriceId" text,
  "AnnualPriceId"  text,
  constraint "Plans_Code_Version_key" unique ("Code", "Version")
);


create table "PlanFeatures" (
  "PlanId"     uuid not null references "Plans"("Id") on delete cascade,
  "FeatureKey" text not null,
  primary key ("PlanId", "FeatureKey")
);


create table "Subscriptions" (
  "Id"                   uuid primary key default gen_random_uuid(),
  "ProviderId"           uuid        not null references "Providers"("Id") on delete restrict,
  "PlanId"               uuid        not null references "Plans"("Id") on delete restrict,
  "Status"               text        not null default 'trialing',
  "PeriodStart"          timestamptz,
  "PeriodEnd"            timestamptz,
  "TrialEndsAt"          timestamptz,
  "PaddleCustomerId"     text,
  "PaddleSubscriptionId" text,
  "CancelledAt"          timestamptz,
  "SuspendedAt"          timestamptz,
  "CreatedAt"            timestamptz not null default now(),
  constraint "Subscriptions_ProviderId_key" unique ("ProviderId"),
  constraint "Subscriptions_Status_ck" check ("Status" in ('trialing','active','past_due','paused','cancelled','suspended'))
);
create index "Subscriptions_PaddleSubscriptionId_idx" on "Subscriptions" ("PaddleSubscriptionId");


-- Exists for one reason: idempotency. The same event delivered twice must not
-- be applied twice, and out-of-order delivery is normal (F-53).
create table "WebhookEvents" (
  "Id"              uuid primary key default gen_random_uuid(),
  "ExternalEventId" text        not null,
  "Type"            text        not null,
  "OccurredAt"      timestamptz,
  "ReceivedAt"      timestamptz not null default now(),
  "ProcessedAt"     timestamptz,
  "Payload"         jsonb       not null,
  constraint "WebhookEvents_ExternalEventId_key" unique ("ExternalEventId")
);
create index "WebhookEvents_unprocessed_idx" on "WebhookEvents" ("ReceivedAt") where "ProcessedAt" is null;


-- Capture metering. Reaching the cap blocks CAPTURE only — dictation, typing,
-- review and signature keep working. Never block clinical work already paid for.
create table "UsageCounters" (
  "ProviderId"       uuid    not null references "Providers"("Id") on delete cascade,
  "Period"           text    not null,           -- 'YYYY-MM'
  "CapturedSessions" integer not null default 0,
  "Cap"              integer,
  "WarnedAt"         timestamptz,
  primary key ("ProviderId", "Period"),
  constraint "UsageCounters_Counts_ck" check ("CapturedSessions" >= 0 and ("Cap" is null or "Cap" > 0))
);


create table "AccountExports" (
  "Id"          uuid primary key default gen_random_uuid(),
  "ProviderId"  uuid        not null references "Providers"("Id") on delete cascade,
  "RequestedAt" timestamptz not null default now(),
  "CompletedAt" timestamptz,
  "ObjectKey"   text,
  "Format"      text        not null default 'zip',
  "Scope"       text        not null default 'full'
);
create index "AccountExports_ProviderId_idx" on "AccountExports" ("ProviderId", "RequestedAt" desc);


-- =============================================================================
--  L — PHASE 3  (F-44 .. F-47, F-38)
-- =============================================================================

-- Append-only hash chain. Any post-signature alteration becomes detectable.
create table "DocumentHashes" (
  "Id"           uuid primary key default gen_random_uuid(),
  "DocumentId"   uuid        not null references "ClinicalDocuments"("Id") on delete restrict,
  "Sequence"     bigint      not null,
  "PreviousHash" text,
  "Hash"         text        not null,
  "SealedAt"     timestamptz not null default now(),
  constraint "DocumentHashes_Document_Sequence_key" unique ("DocumentId", "Sequence")
);
create trigger "DocumentHashes_append_only" before update or delete on "DocumentHashes"
  for each row execute function anamnys_forbid_mutation();


create table "Dossiers" (
  "Id"                uuid primary key default gen_random_uuid(),
  "PatientId"         uuid        not null references "Patients"("Id") on delete restrict,
  "GeneratedAt"       timestamptz not null default now(),
  "ObjectKey"         text,
  "Manifest"          jsonb,
  "IntegrityVerified" boolean     not null default false
);
create index "Dossiers_PatientId_idx" on "Dossiers" ("PatientId", "GeneratedAt" desc);


-- Stores the MEASURE, never a label. "Pauses over 8s: 7 occurrences" is in
-- scope; any adjective about the patient's state is not.
create table "SpeechMarkers" (
  "Id"              uuid primary key default gen_random_uuid(),
  "SessionId"       uuid        not null references "Sessions"("Id") on delete cascade,
  "Kind"            text        not null,
  "Value"           numeric     not null,
  "PatientBaseline" numeric,
  "MeasuredAt"      timestamptz not null default now()
);
create index "SpeechMarkers_SessionId_idx" on "SpeechMarkers" ("SessionId");


create table "RoomSessions" (
  "Id"            uuid primary key default gen_random_uuid(),
  "AppointmentId" uuid        not null references "Appointments"("Id") on delete cascade,
  "StartedAt"     timestamptz,
  "EndedAt"       timestamptz,
  "RejoinCount"   integer     not null default 0,
  "RelayUsed"     boolean     not null default false,
  constraint "RoomSessions_AppointmentId_key" unique ("AppointmentId")
);

commit;
