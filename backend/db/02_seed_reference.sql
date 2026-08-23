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
-- Faixa de escore apenas. Sem faixas de gravidade: registrar o número é
-- registro, chamá-lo de "moderado" é interpretação clínica e é do profissional.
-- Não acrescentar coluna de severidade aqui depois.
--
-- COMO ESTA LISTA FOI MONTADA. A versão anterior trazia PHQ-9, GAD-7 e BDI-II
-- porque são o trio padrão de prontuário eletrônico em inglês. Não é critério.
-- Cada linha abaixo foi conferida contra três coisas independentes:
--
--   1. As listas do SATEPSI (Res. CFP 31/2022). O art. 12 diz que usar teste
--      com parecer desfavorável OU da lista de Testes Não Avaliados é FALTA
--      ÉTICA — e a sanção recai sobre a psicóloga, não sobre o software. Um
--      catálogo que oferece esses instrumentos induz a infração.
--      https://satepsi.cfp.org.br/testesNaoPrivativos.cfm
--      https://satepsi.cfp.org.br/testesNaoAvaliados.cfm
--   2. O direito autoral, que é pergunta separada da anterior. Guardar escore
--      não reproduz obra; exibir os itens reproduz.
--   3. Existência de validação em português do Brasil.
--
-- ONDE FICA A LINHA. Guardar "PHQ-9 = 14 em 12/03/2026" é registro documental,
-- e o art. 6º parágrafo único da Res. 31/2022 torna o registro OBRIGATÓRIO — o
-- produto ajuda a cumprir a norma. Exibir itens, coletar respostas, corrigir ou
-- interpretar é APLICAÇÃO, e aí incidem o art. 11 (seguir o manual aprovado), o
-- Cap. V (estudo de equivalência) e, se remoto, o art. 2º parágrafo único da
-- Res. CFP 09/2024 (o manual precisa prever aplicação on-line).

-- Catálogo padrão: TRÊS, e todos com posição explícita do CFP na lista de
-- Instrumentos Não Privativos. Silêncio do SATEPSI não coloca nada aqui.
-- "StatusExplanation" é o texto que vai para a tela. Escrito para a
-- profissional, não para quem programa: diz o que o CFP disse, onde disse, e o
-- que isso significa para ela. Saber POR QUE algo está disponível importa tanto
-- quanto saber por que outra coisa não está.
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


-- Desligados. Entram no banco de propósito, com o motivo escrito, para que o
-- app consiga EXPLICAR por que não os oferece quando alguém procurar. Ausência
-- silenciosa parece esquecimento e convida a gambiarra.
--
-- Três motivos distintos, que não devem ser confundidos na interface porque
-- levam a caminhos diferentes:
--   (a) FALTA ÉTICA — consta na lista de Testes Não Avaliados. Art. 12. Não há
--       registro possível, e adesão nenhuma destrava.
--   (b) SEM CLASSIFICAÇÃO — o CFP ainda não se pronunciou. NÃO é o mesmo que
--       proibido: o art. 12 tipifica por lista, e não há lista aqui. Fora do
--       catálogo padrão, mas com escore registrável mediante adesão da
--       profissional (ver "ProviderInstrumentOptIns"). Resolver de vez pede o
--       art. 13 — pedir ao CRP que submeta à CCAP.
--   (c) DIREITO AUTORAL — o CFP permitiria; o detentor da obra, não. Adesão não
--       resolve, porque o impedimento não é do CFP.
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

-- AVALIADOS E DESCARTADOS — os outros dois da lista de Não Privativos que
-- pareciam candidatos e não são. Registrado para ninguém refazer a pesquisa:
--
--   TERMÔMETRO DE DISTRESS (NCCN). Um item, 0–10, mais uma lista de problemas.
--   Tem validação brasileira (Decat, Laros & Araújo, Psico-USF 2009), mas
--   VALIDADA EM POPULAÇÃO ONCOLÓGICA. Usar fora de oncologia extrapola a
--   validação. E a NCCN veda reprodução "in any form without express written
--   permission". Cabe num módulo de psico-oncologia, não no acompanhamento
--   geral. Nota lateral: o escore é um número de 0 a 10, então um campo próprio
--   de "sofrimento na última semana, 0 a 10" resolveria a necessidade sem
--   invocar a marca nem a lista de problemas — mas aí não é o instrumento
--   deles, e não deve ser chamado assim.
--
--   COPSOQ e SF-36. Nenhum dos dois é medida de acompanhamento psicoterápico:
--   o COPSOQ é diagnóstico ORGANIZACIONAL de riscos psicossociais, com unidade
--   de análise no setor e não no paciente; o SF-36 é qualidade de vida em saúde
--   geral, insensível a mudança de curto prazo, e saúde mental é 1 dos seus 8
--   domínios.
--
--   E os dois expõem um LIMITE DO ESQUEMA que vale registrar: nenhum deles tem
--   escore único. O COPSOQ produz 11 escores de 0 a 100 e o SF-36 produz 8.
--   "ScaleApplications"."Score" é um integer — um número por aplicação. Se algum
--   dia um instrumento multidimensional entrar, o modelo precisa mudar antes,
--   não na hora.
--
-- CANDIDATOS FUTUROS, não semeados. Todos precisariam vencer DUAS barreiras
-- independentes — a classificação no SATEPSI e a licença autoral — e nenhum
-- vence as duas hoje:
--   WHO-5           5 itens, 0–25 (×4 → 0–100). Sem classificação no SATEPSI.
--                   CC BY-NC-SA 3.0 IGO: a cláusula NC trava produto pago.
--                   Validação BR: de Souza & Hidalgo, 2012.
--   AUDIT           10 itens, 0–40. Sem classificação. OMS: "not for sale or
--                   for use in conjunction with commercial purposes".
--   ASSIST, SRQ-20  mesmo regime OMS, sem classificação.
--   WHOQOL-BREF     26 itens. Sem classificação. Exige autorização da OMS.
--                   Validação BR: Fleck MPA et al., Rev. Saúde Pública
--                   2000;34(2):178-83.
--   PSS-10          10 itens, 0–40. Sem classificação. Pedido gratuito, porém
--                   OBRIGATÓRIO, via ePROVIDE/Mapi. Validação BR: Reis, Hino
--                   & Añez, 2010.
--
-- O SF-36 está na lista de Não Privativos do SATEPSI e seria o único a vencer
-- a primeira barreira — mas é medida de qualidade de vida em saúde geral, não
-- instrumento de acompanhamento clínico em psicologia. Fora de escopo, não
-- fora da norma.
--
-- COMO DESTRAVAR QUALQUER UM. O caminho está no art. 13 da Res. 31/2022 e é do
-- profissional, não nosso: pedir ao CRP que submeta o instrumento à CCAP.
-- A resposta pode ser 'instrumento não privativo' — e aí ele é habilitável —
-- ou 'teste psicológico', e aí ele vai para a lista de Testes Não Avaliados e
-- passa a ser falta ética. O produto não pode antecipar essa resposta.


-- ── conformity rulesets (F-05) ───────────────────────────────────────────────
-- Derivado do texto da Res. CFP nº 006/2019, artigo por artigo. Cada linha traz
-- em "ArticleRef" o dispositivo de onde a lista saiu, para que qualquer pessoa
-- possa conferir sem confiar em quem escreveu isto.
--
-- Fontes:
--   Res. CFP nº 006/2019 (íntegra)
--     https://www.sepsi.ufscar.br/arquivos/regulamentacao/resolucaocfp-06-2019.pdf
--   Versão comentada do CFP (2019)
--     https://site.cfp.org.br/wp-content/uploads/2019/09/Resolucao-CFP-n-06-2019-comentada.pdf
--   Manual Orientativo de Registro e Elaboração de Documentos Psicológicos
--   (CFP, nov/2025) — orienta a aplicação, não substitui a norma
--     https://site.cfp.org.br/wp-content/uploads/2025/11/Manual_Orientativo.pdf
--
-- REGRAS GERAIS QUE ATRAVESSAM TODAS AS MODALIDADES, e por isso aparecem em
-- todas as listas abaixo em vez de ficarem implícitas:
--   Art. 5º §8º  — todas as laudas numeradas, rubricadas da primeira até a
--                  penúltima, e assinatura na última página. Em documento de
--                  uma lauda só a exigência se satisfaz sozinha; o validador
--                  não pode acusar falta de rubrica numa declaração de 1 página.
--   Art. 17      — o prazo de validade do conteúdo deve ser indicado NO ÚLTIMO
--                  PARÁGRAFO do documento. Vale para toda modalidade, e é o
--                  item que mais falta na prática.
--   Art. 15      — guarda mínima de 5 anos (bate com "RetentionRules".
--                  "GuardYears" >= 5, já no esquema).
--   Art. 16 §1º  — protocolo de entrega assinado pelo solicitante. É evidência
--                  de entrega, não elemento do documento; por isso não entra
--                  nestas listas e sim no fluxo de F-07.
--
-- O QUE NÃO ENTROU DE PROPÓSITO:
--   Art. 5º §7º pede referências teórico-técnicas "observando a especificidade
--   do documento", mas só os arts. 13 §7º e 14 §6º as tornam OBRIGATÓRIAS, e
--   apenas para laudo e parecer. Exigir referências em relatório reprovaria
--   documento correto — era o pior defeito do rascunho anterior.
--
--   Os itens facultativos dos arts. 10 §7º, 11 §6º III, 13 §6º III e 14 §5º II
--   (a ressalva de sigilo, caráter extrajudicial e não responsabilização pelo
--   uso posterior) são "é facultado à(ao) psicóloga(o)". Facultativo não pode
--   virar exigência de validador.
--
-- A VERSÃO ANTERIOR ('draft-1') É REMOVIDA ABAIXO. Ela divergia da norma em
-- quatro pontos e nenhum deles era conservador: exigia solicitante em
-- declaração, exigia referências em relatório, inventava "metodologia" no laudo
-- e não enxergava nenhuma vedação.
delete from "ConformityRulesets" r
 where r."Version" = 'draft-1'
   and not exists (select 1 from "ConformityChecks" c where c."RulesetId" = r."Id");

-- Vigência: a resolução é de 29/03/2019 e, pelo art. 19, entrou em vigor 90
-- dias após a publicação — junho de 2019, segundo os CRPs. O dia exato não foi
-- confirmado e não muda nada aqui: todo documento gerado no sistema é muito
-- posterior. Se a data exata importar algum dia, conferir no DOU.
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
insert into "ThemeVocabularies" ("Version","PublishedAt","Active")
  values ('mh-1', now(), true)
on conflict ("Version") do nothing;

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
  ('sono',                 'Dificuldades de sono'),

  -- Trabalho. "burnout" e "carreira" acima continuam valendo; estes são os
  -- recortes que um paciente procura pelo nome e não encontra em nenhum dos dois.
  --
  -- Os itens de assédio e de afastamento descrevem O QUE SE ATENDE, não perícia.
  -- Avaliação psicológica para fins periciais ou previdenciários é outra
  -- atividade, com regra própria, e não pode ser oferecida a partir desta lista.
  ('assedio_moral',            'Assédio moral no trabalho'),
  ('assedio_sexual_trabalho',  'Assédio sexual no trabalho'),
  ('conflito_trabalho',        'Conflitos e clima no trabalho'),
  ('sobrecarga_trabalho',      'Sobrecarga e jornada'),
  ('equilibrio_vida_trabalho', 'Equilíbrio entre vida e trabalho'),
  ('desemprego',               'Desemprego e recolocação'),
  ('retorno_ao_trabalho',      'Retorno ao trabalho após afastamento'),
  ('lideranca_gestao',         'Liderança e gestão'),
  ('trabalho_remoto',          'Trabalho remoto e isolamento'),
  ('empreender',               'Empreendedorismo e trabalho autônomo'),

  -- Imigração. Um mesmo paciente costuma chegar por um destes e permanecer por
  -- outro, então valem como itens separados e não como um único "imigração".
  ('imigracao',                'Imigração e adaptação'),
  ('choque_cultural',          'Choque cultural'),
  ('saudade_desenraizamento',  'Saudade e desenraizamento'),
  ('barreira_linguistica',     'Barreira linguística'),
  ('familia_transnacional',    'Família à distância'),
  ('recomeco_profissional',    'Recomeço profissional no exterior'),
  ('status_migratorio',        'Incerteza de status migratório'),
  ('racismo_xenofobia',        'Racismo e xenofobia'),
  ('retorno_ao_pais',          'Retorno ao país de origem'),
  ('segunda_geracao',          'Identidade de filhos de imigrantes')
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
