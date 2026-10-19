-- Psychometric scales (design/specs/2026-10-18-psychometric-scales-design.md). Run after
-- 2026-10-17-note-pdf.sql. Idempotent, and all-or-nothing.
--
--   docker exec -i <postgres container> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d anamnysdb -v ON_ERROR_STOP=1' < design/migrations/2026-10-18-scales.sql

BEGIN;

ALTER TABLE "ScaleApplications" ADD COLUMN IF NOT EXISTS "RemovedAt" timestamp with time zone;
ALTER TABLE "ScaleApplications" ADD COLUMN IF NOT EXISTS "RemovedBy" uuid;
ALTER TABLE "ScaleApplications" DROP CONSTRAINT IF EXISTS "ScaleApplications_Removed_ck";
ALTER TABLE "ScaleApplications" ADD CONSTRAINT "ScaleApplications_Removed_ck" CHECK ((("RemovedAt" IS NULL) = ("RemovedBy" IS NULL)));

-- Instrument catalog (seed). Re-running keeps rows already present.
insert into "Instruments"
  ("Code","Name","ItemCount","MinScore","MaxScore","SatepsiStatus","SatepsiCheckedOn",
   "LicenseMode","MayRenderItems","Enabled","StatusExplanation","SourceRef") values

  -- Consta na lista oficial de Instrumentos Não Privativos do SATEPSI, com
  -- análise em 07/05/2020. Isso NÃO é aprovação: significa que o CFP
  -- determinou que o PHQ-9 não é teste psicológico e portanto está fora da
  -- certificação. A qualidade psicométrica não é avaliada pelo CFP, e conferir
  -- respaldo científico é dever do profissional. Como fonte complementar
  -- (art. 4º), nunca pode ser base única de conclusão.
  -- Validação brasileira: Santos IS et al., Cad. Saúde Pública 2013;29(8):1533-43.
  ('PHQ-9', 'Patient Health Questionnaire-9', 9, 0, 27,
   'nao_privativo', date '2026-08-23', 'livre', true, true,
   'O CFP classificou o PHQ-9 como instrumento não privativo (SATEPSI, análise em 07/05/2020). Isso não é aprovação: significa que o CFP entende que ele não é teste psicológico e, por isso, está fora da certificação — o SATEPSI não avalia a qualidade psicométrica dos não privativos. Como fonte complementar (Res. CFP 31/2022, art. 4º), pode apoiar seu raciocínio, mas nunca ser a base única de uma conclusão. Conferir o respaldo científico é responsabilidade sua. A reprodução dos itens é liberada pelo detentor, sem necessidade de autorização.',
   'SATEPSI Instrumentos Não Privativos, análise 07/05/2020; rodapé do instrumento: "No permission required to reproduce, translate, display or distribute"'),

  -- Os dois inventários de "A Mente Vencendo o Humor" (Greenberger & Padesky),
  -- na lista de Não Privativos do SATEPSI. São o achado que faltava: foram
  -- desenhados PARA aplicação seriada em TCC, que é exatamente o uso do
  -- Anamnys — diferente do PHQ-9, que é rastreio adaptado a isso.
  --
  -- Só escore. A Guilford e o site oficial vedam expressamente reprodução em
  -- site, intranet ou plataforma, e vedam uso comercial. "MayRenderItems" fica
  -- false, e a restrição "Instruments_Render_ck" impede ligá-lo por engano,
  -- porque 'proprietario' não está na lista permitida.
  --
  -- E NÃO EXISTEM NORMAS NEM PONTOS DE CORTE. Padesky afirma isso: "We do not
  -- have established norms". Aqui isso é vantagem, não defeito — o esquema já
  -- se recusa a guardar faixa de gravidade. O valor é a curva do próprio
  -- paciente ao longo do tempo, nunca comparação com população.
  --
  -- Tradução brasileira da Artmed (2ª ed., 2017), editorial: não há estudo
  -- psicométrico brasileiro publicado. Os estudos de validade concorrente são
  -- da versão em inglês (Beal et al. 2004 para o de depressão; Cox para o de
  -- ansiedade).
  ('MOM-D', 'Inventário de Depressão — A Mente Vencendo o Humor', 19, 0, 57,
   'nao_privativo', date '2026-08-23', 'proprietario', false, true,
   'O CFP classificou os inventários de A Mente Vencendo o Humor como instrumentos não privativos (SATEPSI). Isso não é aprovação — significa que estão fora da certificação, como fonte complementar (Res. CFP 31/2022, art. 4º), nunca base única de conclusão. O Anamnys registra o escore que você obtiver, mas não exibe os itens: a Guilford e o site oficial vedam reprodução em plataformas e uso comercial. Aplique pelo livro (Artmed, 2ª ed.). Atenção clínica: os autores declaram que não existem normas nem pontos de corte estabelecidos, então leia a evolução do próprio paciente ao longo do tempo, e não o número como classificação.',
   'SATEPSI Instrumentos Não Privativos; Guilford Press FAQ e mindovermood.com/worksheets (vedação de uso em plataforma e uso comercial)'),

  ('MOM-A', 'Inventário de Ansiedade — A Mente Vencendo o Humor', 24, 0, 72,
   'nao_privativo', date '2026-08-23', 'proprietario', false, true,
   'O CFP classificou os inventários de A Mente Vencendo o Humor como instrumentos não privativos (SATEPSI). Isso não é aprovação — significa que estão fora da certificação, como fonte complementar (Res. CFP 31/2022, art. 4º), nunca base única de conclusão. O Anamnys registra o escore que você obtiver, mas não exibe os itens: a Guilford e o site oficial vedam reprodução em plataformas e uso comercial. Aplique pelo livro (Artmed, 2ª ed.). Atenção clínica: os autores declaram que não existem normas nem pontos de corte estabelecidos, então leia a evolução do próprio paciente ao longo do tempo, e não o número como classificação.',
   'SATEPSI Instrumentos Não Privativos; Guilford Press FAQ e mindovermood.com/worksheets (vedação de uso em plataforma e uso comercial)')

on conflict ("Code") do nothing;

insert into "Instruments"
  ("Code","Name","ItemCount","MinScore","MaxScore","SatepsiStatus","SatepsiCheckedOn",
   "LicenseMode","MayRenderItems","Enabled","StatusExplanation","SourceRef") values

  -- (a) falta ética — art. 12
  ('DASS-21', 'Escala de Depressão, Ansiedade e Estresse', 21, 0, 63,
   'nao_avaliado', date '2026-08-23', 'autorizacao_necessaria', false, false,
   'Este instrumento consta na lista de Testes Não Avaliados do SATEPSI. A Res. CFP 31/2022, art. 12, trata o uso de instrumento dessa lista como falta ética, e a apuração recai sobre você, não sobre o Anamnys — por isso o sistema não o oferece nem aceita registros dele. Para mudar isso, o caminho é o art. 13: peça ao seu CRP que submeta o instrumento à CCAP do CFP para avaliação.',
   'https://satepsi.cfp.org.br/testesNaoAvaliados.cfm'),

  ('HAD', 'Escala Hospitalar de Ansiedade e Depressão', 14, 0, 42,
   'nao_avaliado', date '2026-08-23', 'proprietario', false, false,
   'Este instrumento consta na lista de Testes Não Avaliados do SATEPSI. A Res. CFP 31/2022, art. 12, trata o uso de instrumento dessa lista como falta ética, e a apuração recai sobre você, não sobre o Anamnys — por isso o sistema não o oferece nem aceita registros dele. Para mudar isso, o caminho é o art. 13: peça ao seu CRP que submeta o instrumento à CCAP do CFP para avaliação.',
   'https://satepsi.cfp.org.br/testesNaoAvaliados.cfm'),

  ('ROSENBERG', 'Escala de Autoestima de Rosenberg', 10, 0, 30,
   'nao_avaliado', date '2026-08-23', 'autorizacao_necessaria', false, false,
   'Este instrumento consta na lista de Testes Não Avaliados do SATEPSI. A Res. CFP 31/2022, art. 12, trata o uso de instrumento dessa lista como falta ética, e a apuração recai sobre você, não sobre o Anamnys — por isso o sistema não o oferece nem aceita registros dele. Para mudar isso, o caminho é o art. 13: peça ao seu CRP que submeta o instrumento à CCAP do CFP para avaliação.',
   'https://satepsi.cfp.org.br/testesNaoAvaliados.cfm'),

  ('EPDS', 'Escala de Depressão Pós-Natal de Edimburgo', 10, 0, 30,
   'nao_avaliado', date '2026-08-23', 'autorizacao_necessaria', false, false,
   'Este instrumento consta na lista de Testes Não Avaliados do SATEPSI. A Res. CFP 31/2022, art. 12, trata o uso de instrumento dessa lista como falta ética, e a apuração recai sobre você, não sobre o Anamnys — por isso o sistema não o oferece nem aceita registros dele. Para mudar isso, o caminho é o art. 13: peça ao seu CRP que submeta o instrumento à CCAP do CFP para avaliação.',
   'https://satepsi.cfp.org.br/testesNaoAvaliados.cfm'),

  ('MBI', 'Maslach Burnout Inventory', 22, 0, 132,
   'nao_avaliado', date '2026-08-23', 'proprietario', false, false,
   'Este instrumento consta na lista de Testes Não Avaliados do SATEPSI. A Res. CFP 31/2022, art. 12, trata o uso de instrumento dessa lista como falta ética, e a apuração recai sobre você, não sobre o Anamnys — por isso o sistema não o oferece nem aceita registros dele. Para mudar isso, o caminho é o art. 13: peça ao seu CRP que submeta o instrumento à CCAP do CFP para avaliação. Além disso, é obra proprietária: exibir os itens exigiria licença do detentor.',
   'https://satepsi.cfp.org.br/testesNaoAvaliados.cfm'),

  ('BSQ', 'Body Shape Questionnaire', 34, 34, 204,
   'nao_avaliado', date '2026-08-23', 'autorizacao_necessaria', false, false,
   'Este instrumento consta na lista de Testes Não Avaliados do SATEPSI. A Res. CFP 31/2022, art. 12, trata o uso de instrumento dessa lista como falta ética, e a apuração recai sobre você, não sobre o Anamnys — por isso o sistema não o oferece nem aceita registros dele. Para mudar isso, o caminho é o art. 13: peça ao seu CRP que submeta o instrumento à CCAP do CFP para avaliação.',
   'https://satepsi.cfp.org.br/testesNaoAvaliados.cfm'),

  -- (b) sem classificação. Eu havia habilitado estes dois presumindo que a CCAP
  -- os classificaria como não privativos, do mesmo jeito que fez com o PHQ-9.
  -- A semelhança é grande, e a presunção continua sendo presunção — que é
  -- exatamente o que não se pode fazer aqui. Desligados até o art. 13 correr.
  ('GAD-7', 'Generalized Anxiety Disorder-7', 7, 0, 21,
   'sem_classificacao', null, 'livre', false, false,
   'O CFP ainda não se pronunciou sobre o GAD-7: ele não consta em nenhuma lista do SATEPSI — nem como não privativo, nem como vedado. Isso é diferente de estar proibido. O art. 12 tipifica falta ética por lista, e o GAD-7 não está em nenhuma; o art. 5º lhe dá a prerrogativa de escolher instrumentos fundamentados na literatura científica, e ele tem validação brasileira publicada (Moreno et al., 2016). Por isso o Anamnys não o oferece no catálogo padrão, mas registra seus escores se você aderir explicitamente, ciente de que a classificação está pendente e pode mudar. O que o app nunca fará é exibir os itens: se a CCAP vier a considerá-lo teste psicológico, a aplicação informatizada dependeria de manual aprovado e estudo de equivalência. Para resolver a pendência, o caminho é o art. 13 — pedir ao seu CRP que o submeta à CCAP.',
   'https://satepsi.cfp.org.br/faq.cfm'),

  ('PCL-5', 'PTSD Checklist for DSM-5', 20, 0, 80,
   'sem_classificacao', null, 'dominio_publico', false, false,
   'O CFP ainda não se pronunciou sobre o PCL-5: ele não consta em nenhuma lista do SATEPSI, o que é diferente de estar proibido. O instrumento é de domínio público (National Center for PTSD) e tem adaptação transcultural brasileira publicada (Osório et al., 2017), mas domínio público não substitui classificação do CFP. Fora do catálogo padrão; seus escores podem ser registrados mediante adesão explícita sua, ciente da pendência. Os itens não são exibidos pelo app. Para resolver a pendência, peça ao seu CRP que o submeta à CCAP (Res. CFP 31/2022, art. 13).',
   'https://satepsi.cfp.org.br/faq.cfm'),

  -- (c) direito autoral. Estes dois são testes psicológicos com parecer
  -- FAVORÁVEL — o CFP permitiria. Ficam desligados por causa da editora.
  -- "MayRenderItems" nunca pode virar true aqui: "Instruments_Render_ck"
  -- recusa em 'proprietario'.
  ('BDI-2', 'Inventário de Depressão de Beck — 2ª edição', 21, 0, 63,
   'favoravel', date '2026-08-23', 'proprietario', false, false,
   'O CFP não impede o uso: o BDI-2 é teste psicológico com parecer favorável no SATEPSI. O impedimento é de direito autoral — a obra é da Hogrefe, com venda restrita a psicólogos mediante CRP e plataforma de aplicação própria. O Anamnys não pode exibir os itens sem contrato. Aplique pelo material oficial que você adquiriu; assim que a habilitação comercial existir, você poderá lançar aqui o escore obtido.',
   'Hogrefe Brasil — Escalas Beck'),

  ('BAI', 'Inventário de Ansiedade de Beck', 21, 0, 63,
   'favoravel', date '2026-08-23', 'proprietario', false, false,
   'O CFP não impede o uso: o BAI é teste psicológico com parecer favorável no SATEPSI. O impedimento é de direito autoral, igual ao do BDI-2 — obra da Hogrefe, venda restrita a psicólogos mediante CRP. Aplique pelo material oficial; o Anamnys não exibe os itens sem contrato com a editora.',
   'Hogrefe Brasil — Escalas Beck'),

  -- (b) + (c): sem classificação no SATEPSI E com uso digital vedado pela
  -- licença. Dois impedimentos independentes, ambos suficientes sozinhos.
  ('ORS', 'Outcome Rating Scale', 4, 0, 40,
   'sem_classificacao', null, 'vedado_digital', false, false,
   'Dois impedimentos independentes. O CFP ainda não se pronunciou sobre a ORS — ela não consta em nenhuma lista do SATEPSI, o que por si só a mantém fora do catálogo padrão. Mas o segundo impedimento é mais duro e não se resolve com adesão sua: a licença gratuita do autor cobre apenas papel e caneta, e veda expressamente o uso digital das escalas. Registrar escores aqui exigiria acordo direto com o detentor, além da classificação pela CCAP (art. 13).',
   'scottdmiller.com/downloadmeasures'),

  ('SRS', 'Session Rating Scale', 4, 0, 40,
   'sem_classificacao', null, 'vedado_digital', false, false,
   'Mesma situação da ORS: sem classificação no SATEPSI, e com uso digital das escalas expressamente vedado pela licença do autor. O segundo impedimento não se resolve com adesão sua — depende de acordo com o detentor.',
   'scottdmiller.com/downloadmeasures')

on conflict ("Code") do nothing;

COMMIT;
