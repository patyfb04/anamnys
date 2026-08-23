# Esquema do banco

Os três scripts que constroem o banco do Anamnys do zero. Derivados de
`specs/docs/anamnys-modelo-dados.html`; os identificadores `F-nn` nos comentários
apontam para `TODO.txt`.

| Arquivo | O que faz |
| --- | --- |
| `01_schema.sql` | As 71 tabelas, com chaves, restrições, índices, gatilhos e uma visão |
| `02_seed_reference.sql` | Registros que a aplicação lê e não inventa: instrumentos, vocabulários, regras de conferência, planos |
| `03_jobs.sql` | Três rotinas agendadas e as consultas de acompanhamento |

## Rodar

```bash
createdb anamnys_dev
psql -v ON_ERROR_STOP=1 -d anamnys_dev -f 01_schema.sql
psql -v ON_ERROR_STOP=1 -d anamnys_dev -f 02_seed_reference.sql
psql -v ON_ERROR_STOP=1 -d anamnys_dev -f 03_jobs.sql
```

PostgreSQL 15 ou superior. Precisa das extensões `btree_gist` e `pgcrypto` — o
script as cria, o que exige um papel com permissão para isso.

### No Neon

As duas extensões são suportadas, então `01_schema.sql` roda sem alteração.
Três cuidados:

- **Usar o endpoint direto, sem o sufixo `-pooler`, para rodar estes scripts.**
  O endpoint agrupado é PgBouncer em modo transação, e a própria documentação do
  Neon desaconselha usá-lo para migração de esquema. O agrupado serve à
  aplicação; o direto, ao DDL.
- **Não agendar as rotinas de `03_jobs.sql` com `pg_cron`.** O Neon suporta a
  extensão, mas as tarefas só disparam com a computação acordada, e ela hiberna
  por inatividade. A varredura de reservas importa justamente quando ninguém
  está usando o sistema. Usar o Hangfire, que já existe no projeto.
- **A primeira conexão do dia demora.** A computação hibernada leva alguns
  segundos para acordar, e cliente com tempo limite curto desiste antes.

`02_seed_reference.sql` é idempotente: rodar de novo não insere nada.

## Convenção de nomes

Tabelas em PascalCase plural, colunas em PascalCase — o padrão que o EF Core já
usava nas cinco tabelas existentes. Todo identificador vai entre aspas duplas,
porque o PostgreSQL dobra para minúsculas o que não estiver citado.

## O que já foi verificado

Os scripts foram executados contra um PostgreSQL 16 real, em banco vazio, e cada
garantia abaixo foi testada — não inspecionada:

| Comportamento | Resultado |
| --- | --- |
| As três etapas, em banco novo | 71 tabelas, sem erro |
| Nota do paciente A atribuída ao profissional B | recusada pela chave composta |
| Dois compromissos sobrepostos no mesmo profissional | recusado pela restrição de exclusão |
| Reserva em cima de compromisso, e o inverso | recusados pelo gatilho entre tabelas |
| **Duas transações simultâneas no mesmo horário** | **exatamente um compromisso, uma recusa clara** |
| `UPDATE` ou `DELETE` na trilha de auditoria | recusados |
| Perfil listado sem opt-in registrado | recusado |
| Link de compartilhamento com validade acima de 30 dias | recusado |
| Nota assinada sem data de assinatura | recusada |
| Rodar o seed duas vezes | segunda execução insere zero linhas |
| Documento do tipo `relatorio_multiprofissional` | aceito |
| Documento de tipo inexistente (`prontuario`) | recusado pela restrição de tipo |
| `ConformityRulesets."Elements"` gravado como objeto, não lista | recusado |
| Termos filhos de `trabalho` e `imigracao` | 6 e 6, com o pai resolvido pelo código |
| Ativar instrumento da lista de Testes Não Avaliados | recusado |
| Ativar instrumento sem classificação no SATEPSI | recusado |
| Gravar status do SATEPSI sem a data de conferência | recusado |
| Ativar instrumento depois de classificado pela CCAP | aceito |
| Exibir itens de instrumento proprietário | recusado |
| Gravar resposta item a item do BDI-2 | recusada pelo gatilho |
| Gravar **apenas o escore** do BDI-2 | aceita |
| Gravar resposta item a item do PHQ-9 | aceita |
| Lançar escore de `DASS-21` ou `MBI` (vedado, art. 12) | recusado, sem caminho alternativo |
| Lançar escore de `GAD-7` **antes** da adesão da profissional | recusado |
| Lançar escore de `GAD-7` **depois** da adesão | aceito |
| Registrar adesão a instrumento vedado (`DASS-21`) | recusada pelo gatilho |
| Lançar escore de `MOM-D` (não privativo) sem adesão | aceito |
| Ligar exibição de itens em instrumento sem classificação | recusado |
| Recusa carrega a explicação para a profissional | sim, na mensagem do gatilho |
| Instrumento gravado com explicação vazia ou curta | recusado |

O teste concorrente é o critério de aceitação escrito em F-62: conferir conflito
antes de gravar perde a corrida quando dois pacientes clicam junto, e só a
restrição no banco segura.

## Três coisas que o esquema não resolve sozinho

**Reserva vencida bloqueia outra reserva.** A restrição de exclusão não pode
testar `ExpiresAt > now()`, porque `now()` não é imutável e predicado de
restrição precisa ser. Medido, não suposto: uma reserva vencida **não** impede um
compromisso — o gatilho entre tabelas filtra por validade — mas **impede outra
reserva** no mesmo horário. Ou seja, um formulário abandonado não atrapalha o
profissional, e sim o próximo paciente que tentar marcar ali. Invisível de quem
opera. Por isso `anamnys_release_expired_holds()` roda a cada minuto.

**O canal do Google Agenda expira em dias.** Sem rotina de renovação a
sincronização morre em silêncio: o app continua funcionando e só para de ver o
que mudou fora. Vigiar `ChannelExpiresAt` e alertar 48 h antes.

**A transcrição nunca sai.** Não há como o banco impedir que uma consulta de
exportação inclua `"Transcripts"`. A garantia de F-28 é de código: as consultas
de exportação e de compartilhamento precisam ser incapazes de tocar essa tabela,
e isso pede teste, não revisão.

## Conferência da Res. CFP 006/2019

`ConformityRulesets` deixou de ser rascunho. As listas em `cfp-006-2019.1` saíram
do texto da resolução, e cada linha carrega em `ArticleRef` o dispositivo de
origem, para que dê para conferir sem confiar em quem escreveu.

Fontes: a [íntegra da resolução](https://www.sepsi.ufscar.br/arquivos/regulamentacao/resolucaocfp-06-2019.pdf),
a [versão comentada do CFP](https://site.cfp.org.br/wp-content/uploads/2019/09/Resolu%C3%A7%C3%A3o-CFP-n-06-2019-comentada.pdf)
e o [Manual Orientativo de Registro e Elaboração de Documentos Psicológicos](https://site.cfp.org.br/wp-content/uploads/2025/11/Manual_Orientativo.pdf)
(CFP, nov/2025), que orienta a aplicação e não substitui a norma.

### O que a leitura mudou

| Onde | `draft-1` | A norma |
| --- | --- | --- |
| Modalidades | cinco | **seis** — o art. 8º lista o *Relatório Multiprofissional* (art. 12) como modalidade própria, e ela não existia no esquema |
| `declaracao` | pedia identificação do **solicitante** | art. 9º §2º pede a pessoa **atendida**; e o §1º **veda** registrar sintomas, situações ou estados psicológicos — é o que separa declaração de atestado |
| `relatorio` | exigia `referencias` | art. 11 §1º tem cinco itens e não inclui referências; a obrigatoriedade está só nos arts. 13 §7º e 14 §6º. O validador reprovaria documento correto |
| `laudo` | exigia `metodologia` | art. 13 §1º não tem esse item; o referencial teórico-metodológico é conteúdo do Procedimento (§4º) |
| `atestado` | seis elementos | art. 10 §6º acrescenta título, solicitante, a descrição das condições psicológicas e o encerramento completo; e o §5º manda escrever em texto corrido, sem parágrafos |
| Todos | nada | art. 5º §8º (laudas numeradas e rubricadas) e **art. 17** (prazo de validade no último parágrafo) valem para toda modalidade |
| Todos | só presença | as vedações não eram representáveis |

### Por que o esquema mudou junto

Três alterações em `01_schema.sql`, todas exigidas pela norma e não por gosto:

- **`ConformityRulesets."Prohibitions"`** e **`ConformityChecks."ForbiddenFound"`**.
  A infração mais comum da 006/2019 é declaração que descreve estado psicológico
  — art. 9º §1º. Um verificador que só sabe procurar o que **falta** é
  estruturalmente incapaz de vê-la. As duas colunas também obrigam a separar as
  mensagens: uma é "falta", a outra é "não pode constar".
- **`ConformityRulesets."ArticleRef"`**. Sem o dispositivo ao lado da regra,
  ninguém consegue auditar a lista, e uma lista de conformidade que não se
  audita não vale nada.
- **`relatorio_multiprofissional`** nas restrições de tipo de
  `ClinicalDocuments` e `ConformityRulesets`.

### O que ficou de fora, de propósito

- **Itens facultativos.** Os arts. 10 §7º, 11 §6º III, 13 §6º III e 14 §5º II
  dizem "é facultado à(ao) psicóloga(o)" destacar sigilo, caráter extrajudicial
  e não responsabilização pelo uso posterior. Facultativo não vira exigência.
- **A ressalva do relatório sobre diagnóstico.** O art. 11 descreve o relatório
  como documento sem finalidade de produzir diagnóstico. Não ficou claro se é
  vedação ou descrição de finalidade, e vedação encravada por interpretação é
  pior do que vedação ausente. Fica registrado, fora da lista, até leitura da
  versão comentada.
- **Protocolo de entrega (art. 16 §1º) e entrevista devolutiva (art. 18).** São
  obrigações do fluxo, não elementos do documento; pertencem a F-07.
- **Rubrica em documento de uma lauda.** O art. 5º §8º manda rubricar "da
  primeira até a penúltima". Em documento de uma página não há o que rubricar, e
  o verificador não pode acusar falta. A regra está na lista; a implementação
  precisa dessa condicional.

## Instrumentos: por que a lista é curta

A versão anterior trazia PHQ-9, GAD-7 e BDI-II porque é o trio padrão de
prontuário eletrônico em inglês. Não era critério. Cada instrumento foi
reconferido contra três coisas independentes — as listas do SATEPSI, o direito
autoral, e a existência de validação brasileira.

### O achado que decide o desenho

O art. 12 da Res. CFP 31/2022 diz que usar teste com parecer desfavorável **ou
que conste na lista de Testes Não Avaliados do SATEPSI é falta ética**. Estão
nessa lista, entre outros: DASS, HAD, Escala de Autoestima de Rosenberg, Escala
de Depressão Pós-Natal de Edimburgo, Maslach Burnout Inventory e Body Shape
Questionnaire — exatamente os instrumentos que um catálogo ingênuo incluiria.

A sanção recai sobre a psicóloga, não sobre o software. Um catálogo que os
oferece **induz** a infração e transfere o custo para quem paga a assinatura. Por
isso as regras viraram restrição de banco e não convenção de código:
`Instruments_Ethics_ck` recusa deixar ativo um instrumento `nao_avaliado` ou
`desfavoravel`.

### Onde fica a linha entre registrar e aplicar

Guardar `PHQ-9 = 14 em 12/03/2026` é **registro documental** — e o art. 6º,
parágrafo único da 31/2022 torna o registro obrigatório, ou seja, o produto ajuda
a cumprir a norma. Não foi encontrada norma do CFP que sujeite ao SATEPSI um
software que apenas armazena escore informado pelo profissional; isso está
registrado como *ausência de norma localizada*, não como permissão expressa.

Exibir itens, coletar respostas, corrigir ou interpretar é **aplicação**, e aí
incidem o art. 11 (seguir rigorosamente o manual aprovado), o Cap. V (estudo de
equivalência para versão informatizada) e, se remoto, o art. 2º parágrafo único
da Res. CFP 09/2024 — que exige manual com padronização específica para
aplicação on-line. A Res. 09/2024 revogou a 11/2018 e a 04/2020; a 31/2022
revogou a 09/2018.

`MayRenderItems` é essa linha, e `ScaleApplications."RawAnswers"` é onde ela
vaza: resposta item a item só existe se o app exibiu e coletou. Um gatilho recusa
gravar `RawAnswers` para instrumento que o app não pode renderizar.

### Silêncio do SATEPSI não é permissão

Esta é a regra que organiza a tabela, e ela é **lista de permissão, não de
proibição**. Ficar de fora das listas do SATEPSI não é vácuo regulatório: é
classificação pendente, e o padrão é restritivo. O FAQ do CFP, com estas
palavras:

> "os testes que não foram analisados pelo CFP não estão aprovados e, por isso,
> não podem ser utilizados para fins profissionais."

E o art. 13 mostra para onde a ausência caminha: na dúvida o CRP submete à CCAP,
que classifica como *instrumento não privativo* **ou** como *teste psicológico* —
e neste segundo caso o instrumento entra na lista de Testes Não Avaliados, onde
o uso vira falta ética. Instrumento sem classificação está a uma consulta de CRP
de ser proibido.

Por isso `Instruments_Enabled_ck` exige `SatepsiStatus in ('nao_privativo',
'favoravel')` para habilitar. Nenhuma linha depende de alguém lembrar da regra.

**E nada aqui é "aprovado pelo CFP" — nenhuma tela pode dizer isso.**
`favoravel` significa que o CFP certificou um teste psicológico. `nao_privativo`
significa o contrário: o CFP determinou que a coisa **não é** teste psicológico e
portanto está fora da certificação. O próprio SATEPSI diz que a qualidade
psicométrica dos não privativos não é avaliada pelo CFP e que conferir respaldo
científico é dever do profissional.

### O que está habilitado

Um só, e não por falta de opções.

| Código | Itens · escore | SATEPSI | Licença | Exibe itens |
| --- | --- | --- | --- | --- |
| `PHQ-9` | 9 · 0–27 | não privativo (análise 07/05/2020) | livre | sim |

Os demais entram no banco desligados e **com a explicação escrita**, por três razões
que a interface não deve confundir:

| Motivo | Instrumentos |
| --- | --- |
| **Falta ética** — lista de Testes Não Avaliados, art. 12 | `DASS-21`, `HAD`, `ROSENBERG`, `EPDS`, `MBI`, `BSQ` |
| **Sem classificação** — o CFP nunca se pronunciou; destravar exige o art. 13 | `GAD-7`, `PCL-5`, `ORS`, `SRS` |
| **Direito autoral** — o CFP permitiria, o detentor não | `BDI-2`, `BAI` (Hogrefe), `ORS`, `SRS` (uso digital vedado) |

`GAD-7` e `PCL-5` estavam habilitados na primeira versão desta tabela. Eu os
liberei presumindo que a CCAP os classificaria como não privativos, do mesmo
jeito que fez com o PHQ-9. A semelhança é grande e a presunção continua sendo
presunção. A licença autoral dos dois é livre — o impedimento é regulatório.

`SatepsiCheckedOn` existe porque as listas mudam: status anotado uma vez e nunca
reconferido é afirmação sem lastro que envelhece calada. A restrição
`Instruments_Checked_ck` recusa gravar status sem a data.

### Oferecer e registrar são coisas diferentes

`Enabled` governa o que o **catálogo oferece**. Não governa o que pode ser
**registrado**. Registrar não é endossar: recusar o lançamento não desfaz a
aplicação, produz prontuário que mente por omissão — contra o art. 6º parágrafo
único, que torna o registro obrigatório — e a profissional escreve na prosa da
nota de qualquer jeito, agora sem estrutura.

**Cuidado com o art. 5º.** Ele dá à profissional a prerrogativa de escolher os
instrumentos, mas com uma condicional fácil de perder: *"desde que fundamentados
na literatura científica psicológica **e nas normas vigentes do CFP**"*. Um
instrumento da lista de Testes Não Avaliados está fora das normas vigentes — o
art. 5º **não** ampara esse uso. Ele ampara a escolha entre instrumentos
regulares, não a escolha de um instrumento vedado. Usar esse artigo como
justificativa geral é um erro; foi cometido aqui antes de ser corrigido.

**Pendente não é o mesmo que excluído**, e o esquema trata os dois casos de
formas diferentes porque juridicamente eles não se equivalem.

O art. 12 tipifica falta ética em duas hipóteses **enumeradas**: parecer
desfavorável, ou constar da lista de Testes Não Avaliados. É proibição por lista,
e o que não está em lista nenhuma não é alcançado por ela. Já o art. 5º dá à
profissional a prerrogativa de escolher os instrumentos *"desde que fundamentados
na literatura científica psicológica e nas normas vigentes do CFP"* — para um
instrumento pendente, com literatura sólida e validação brasileira, as duas
condições se cumprem, porque não há norma vigente que o proíba. Para um da lista
de Testes Não Avaliados, a segunda falha, e o art. 5º não ampara.

O FAQ do SATEPSI diz que teste não analisado não está aprovado e não pode ser
usado profissionalmente. É orientação, não ato normativo, e o sujeito da frase é
*"teste psicológico"* — que é exatamente a classificação ainda indeterminada. A
frase pressupõe a conclusão que se quer tirar dela. Pesa, e não decide.

Daí os três regimes: **vedado** é recusa absoluta; **pendente** fica fora do
catálogo padrão mas admite registro mediante adesão da profissional; **com
pronunciamento** entra no catálogo.

| Status | Catálogo padrão | Registro | Série |
| --- | --- | --- | --- |
| `nao_privativo`, `favoravel` | sim | sim | sim |
| `sem_classificacao` — `GAD-7`, `PCL-5` | não | **mediante adesão** | sim |
| `nao_avaliado`, `desfavoravel` — `DASS-21`, `MBI`… | não | **não, sem exceção** | não |
| `favoravel` porém pago — `BDI-2`, `BAI` | não, por licença | sim, escore digitado | sim |

**Cuidado com a palavra "aprovado", inclusive ao ler esta tabela.**
`nao_privativo` não é aprovação: o CFP determinou que a coisa **não é** teste
psicológico, logo está fora da certificação, e o SATEPSI não avalia a qualidade
psicométrica desses instrumentos. O que os dois status habilitados têm em comum
não é aprovação — é **o CFP ter se pronunciado**. Nenhuma tela pode dizer
"aprovado pelo CFP" sobre nada desta tabela.

Migração não abre exceção. Dado histórico de instrumento sem classificação exige
resolver a classificação antes, pelo art. 13, e não desligar o gatilho.

### Ninguém é bloqueado sem saber por quê

`StatusExplanation` é obrigatória para **todos** os instrumentos, habilitados
inclusive — a profissional precisa saber por que uma coisa está disponível tanto
quanto por que outra não está. O texto é português escrito para ela, vai para a
tela, e a mensagem de recusa do gatilho o carrega junto. Um "não" sem explicação
parece limitação do produto e manda a pessoa procurar um jeito de contornar; com
a explicação, ela aprende algo que a protege — e, quando cabe, qual é o caminho
do art. 13 para destravar.

Cada explicação diz três coisas: o que o CFP disse, onde está isso, e o que
significa na prática dela. Elas separam impedimento **regulatório** de
impedimento **autoral**, que é a distinção que mais confunde: o `GAD-7` está
bloqueado sem que ninguém detenha direito sobre ele, e o `BDI-2` está bloqueado
com o CFP inteiramente a favor do seu uso.

### Nada de "prosseguir mesmo assim"

Para instrumento **vedado**, não há confirmação que destrave. Houve uma versão
com campo de ciência (`RestrictionAcknowledgedAt`) que aceitava o lançamento se a
profissional reconhecesse o aviso. Era pior que o bloqueio: em prontuário
requisitável aquele campo é o melhor documento possível **contra** ela num
processo ético-disciplinar — registro datado provando que o sistema avisou ser
falta ética e que ela seguiu assim mesmo. O produto construiria a prova contra a
própria usuária e chamaria isso de transparência.

**Esse argumento não se estende ao caso pendente, e a diferença é o ponto todo.**
Lá o registro documentava seguir apesar de infração publicada. Aqui não há
infração: há incerteza, e o art. 5º atribui à profissional justamente resolvê-la.
`ProviderInstrumentOptIns` documenta exercício informado de prerrogativa — que é
o que boa documentação clínica deve conter, não uma confissão.

A adesão é **por profissional e por instrumento, uma vez** — nunca por
lançamento. Decisão ponderada uma vez vale mais que confirmação repetida
cinquenta vezes, que só ensina a clicar sem ler. A tabela guarda cópia do texto
lido (`ExplanationShown`), porque a explicação muda quando o status muda no
SATEPSI e "ela concordou" não significa nada sem saber com o quê. Um gatilho
impede adesão a instrumento que não seja `sem_classificacao`.

Exibir os itens continua fora, mesmo com adesão: renderizar é aplicar, e se a
CCAP vier a chamar o instrumento de teste psicológico, a aplicação informatizada
dependeria de manual aprovado e estudo de equivalência que ninguém tem.

Vale como regra geral do Anamnys, não só para instrumentos: sempre que o produto
souber que algo é irregular e oferecer um "confirmo, seguir assim mesmo", ele
está transferindo risco para quem clica e guardando a prova.

A visão `InstrumentSeries` é segunda linha, não primeira: em banco novo ela não
filtra nada, porque o gatilho já impede a escrita. Ela importa quando um
instrumento hoje regular vai parar na lista amanhã — nesse dia as linhas antigas
saem dos gráficos sem que ninguém precise lembrar de ajustar consulta nenhuma.

### Uma coisa que nenhum instrumento resolve

Instrumento não privativo **não pode ser base única de conclusão** — o CFP diz
isso expressamente. Uma série de PHQ-9 é fonte complementar (art. 4º), nunca a
conclusão. Se a interface algum dia apresentar a curva como se fosse resposta, o
problema não estará no banco.

## Risco não é tema

`ideacao_suicida` e `autolesao` foram propostos para o vocabulário de temas e
descartados. O motivo decide o desenho de qualquer coisa parecida no futuro, por
isso fica escrito aqui e não só no comentário do seed.

Quem aplica a etiqueta é a IA: o F-02 restringe o prompt de estruturação ao
vocabulário ativo, e `ThemeTags."CreatedBy"` tem `default 'ai'`. Máquina
escolhendo `ideacao_suicida` é máquina emitindo inferência de risco, e é a linha
que o *Manual Orientativo* do CFP (2025) traça. A nota já registra isso e
registra melhor — na prosa está o entendimento do profissional, com a nuance que
fez daquilo um julgamento, escrito e assinado por ele.

A série longitudinal também não justifica. Para `sono` a tendência é o dado; para
risco decide-se na primeira menção, não pela curva. E a detecção de ausência
(F-12) produziria a frase *"registrado em 4 das 9 primeiras sessões; sem registro
nas 11 seguintes"* — afirmação de que o risco cessou, gerada por software, sobre
a coisa em que errar custa mais caro.

O pior efeito, porém, é o de parecer o que não é. O sistema vê apenas o que foi
escrito em nota estruturada; um paciente em risco pode nunca dizer as palavras.
Sinal silenciosamente incompleto com aparência de vigilância convida a uma
confiança que o produto não sustenta — a mesma falha do `pg_cron` no Neon, que
falha calada e parece bem.

Fecha o caso o fato de o esquema não conseguir garantir "só humano aplica este
termo": `CreatedBy` tem default `'ai'` e não há regra por termo. Seria convenção,
não restrição.

Se um dia o risco precisar de estrutura, entra como **plano de segurança criado e
assinado deliberadamente pelo profissional** — nunca como termo extraível deste
vocabulário. É item de backlog e decisão, não linha de seed.

## Violência não é tema, é gatilho de um dever

`violencia_domestica` saiu do vocabulário. Por motivo diferente do anterior, e a
diferença importa para o desenho.

Não é conclusão clínica — é fato relatado, da mesma natureza de `luto`. O que a
derruba é outra coisa. A detecção de ausência (F-12) geraria *"registrada em 4
das 9 primeiras sessões; sem registro nas 11 seguintes"*, e em violência
doméstica o relato parar é característica conhecida do ciclo: o sistema
inverteria o sinal. Pior do que na ideação suicida, onde o silêncio ao menos é
ambíguo.

E existe dever legal de notificar. A Lei 10.778/2003 põe sob notificação
compulsória os casos com indícios ou confirmação de violência contra a mulher
atendida em serviços de saúde públicos **e privados**; a Lei 13.931/2019
acrescentou comunicação à autoridade policial em 24 horas; para criança e
adolescente, o art. 13 do ECA obriga comunicação ao Conselho Tutelar. O alcance
disso sobre o psicólogo autônomo em consultório é matéria em disputa — os CRPs
publicam orientação técnica justamente sobre essa quebra de sigilo — e o produto
não pode decidir por ele.

Daí o ponto: etiqueta estruturada e consultável não é a mesma coisa que a frase
na prosa. É prova legível por máquina de que o caso foi identificado pelo
sistema, em prontuário requisitável, sem nenhum registro de que algo tenha sido
feito. Não protege o profissional; expõe.

Se isso entrar um dia, entra como **registro do ato** — quem notificou, quando, a
quem, com que fundamento —, não como assunto marcado numa nota. Mesmo caminho do
plano de segurança.

`discriminacao` ficou. Testada contra os mesmos critérios, não compartilha
nenhum: marcá-la não infere estado psicológico, a recorrência ao longo do
acompanhamento tem uso clínico real, e não há dever legal pendurado.

## Conciliação com o EF Core

Cinco tabelas já existiam por migração: `Providers`, `Patients`, `Notes`,
`RecoveryCodes`, `ContactMessages`. Este script é **greenfield** — recria tudo em
banco vazio. Como só há dado de desenvolvimento, o caminho limpo é recriar o
banco e gerar uma migração nova a partir do resultado.

Três diferenças em relação ao que o EF tem hoje, todas deliberadas:

- **`Notes` perdeu as colunas `jsonb`.** `StructuredContent`, `BillingCodes` e
  `AuditTrail` viraram `NoteSections`, `BillingCodes` e `AuditEntries`. Era o
  ponto mais caro de adiar: a migração fica mais difícil a cada nota criada.
- **`Notes` ganhou `SessionId` e `Format`.** O formato estava enterrado dentro do
  JSON e não dava para consultar.
- **`Notes` e `Patients` ganharam chave composta.** `Notes` referencia
  `("PatientId","ProviderId")`, e não só o paciente, para que o banco recuse uma
  nota atribuída ao profissional errado. Antes `ProviderId` era só um índice, sem
  chave estrangeira nenhuma.

E, desde a conferência da 006/2019: `ConformityRulesets` ganhou `Prohibitions`
e `ArticleRef`, `ConformityChecks` ganhou `ForbiddenFound`, `ThemeTerms` ganhou
`IntroducedOn`, e as restrições de tipo de documento passaram a aceitar
`relatorio_multiprofissional`.

Da conferência da 31/2022: `Instruments` ganhou `ItemCount`, `MinScore`,
`SatepsiStatus`, `SatepsiCheckedOn`, `LicenseMode`, `MayRenderItems`,
`SourceRef`, `Enabled` e `StatusExplanation` (**obrigatória**, e é o texto que
vai para a tela), mais seis restrições, uma visão `InstrumentSeries` e um gatilho
sobre `ScaleApplications."RawAnswers"`. Note que `Enabled` tem default **false**:
instrumento novo nasce desligado, e habilitar exige posição do CFP. Ao regerar o
modelo do EF, `SatepsiStatus` e `LicenseMode` são enums guardados como texto.

Ao regerar o modelo do EF a partir daqui, lembrar de manter `HasConversion<string>()`
nos enums que este esquema guarda como texto (`Status`, `InputMode`, `Format`),
e note que `BillingSystem` continua inteiro, com `2` (BrazilTuss) como padrão.

## O que ainda precisa de decisão

- **D-01** — versionamento do vocabulário de temas, **parcialmente resolvido**.
  A parte aditiva está decidida e implementada: acrescentar termo é seguro,
  e `ThemeTerms."IntroducedOn"` guarda a data de entrada para que a linha do
  tempo saiba distinguir "o tema começou" de "o termo passou a existir". O que
  continua em aberto é renomear, dividir ou fundir termo — aí a série antiga
  perde sentido, e a ligação `SupersededByTermId` ainda não tem regra escrita.
- **D-04** — os códigos TUSS não estão semeados aqui de propósito: os que existem
  no motor de faturamento foram escritos como exemplos e não foram conferidos
  contra a tabela oficial da ANS.
- **D-07** — os identificadores de preço da Paddle ficam nulos em `Plans` até a
  integração ser confirmada.
- **D-10** — os padrões de `BookingPolicies` (24 h de antecedência, 60 dias de
  horizonte, 4 compromissos em aberto) são chute razoável, não decisão tomada.
- **F-05** — **resolvido.** `draft-1` foi substituído por `cfp-006-2019.1`,
  derivado do texto da Res. CFP 006/2019 artigo por artigo. Ver a seção
  *Conferência da Res. CFP 006/2019* abaixo.
