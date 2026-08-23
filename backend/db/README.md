# Esquema do banco

Os três scripts que constroem o banco do Anamnys do zero. Derivados de
`specs/docs/anamnys-modelo-dados.html`; os identificadores `F-nn` nos comentários
apontam para `TODO.txt`.

| Arquivo | O que faz |
| --- | --- |
| `01_schema.sql` | As 70 tabelas, com chaves, restrições, índices e gatilhos |
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
| As três etapas, em banco novo | 70 tabelas, sem erro |
| Nota do paciente A atribuída ao profissional B | recusada pela chave composta |
| Dois compromissos sobrepostos no mesmo profissional | recusado pela restrição de exclusão |
| Reserva em cima de compromisso, e o inverso | recusados pelo gatilho entre tabelas |
| **Duas transações simultâneas no mesmo horário** | **exatamente um compromisso, uma recusa clara** |
| `UPDATE` ou `DELETE` na trilha de auditoria | recusados |
| Perfil listado sem opt-in registrado | recusado |
| Link de compartilhamento com validade acima de 30 dias | recusado |
| Nota assinada sem data de assinatura | recusada |
| Rodar o seed duas vezes | segunda execução insere zero linhas |

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

Ao regerar o modelo do EF a partir daqui, lembrar de manter `HasConversion<string>()`
nos enums que este esquema guarda como texto (`Status`, `InputMode`, `Format`),
e note que `BillingSystem` continua inteiro, com `2` (BrazilTuss) como padrão.

## O que ainda precisa de decisão

- **D-01** — versionamento do vocabulário de temas. Os termos semeados são um
  ponto de partida, e toda série longitudinal depende de esses códigos não mudarem.
- **D-04** — os códigos TUSS não estão semeados aqui de propósito: os que existem
  no motor de faturamento foram escritos como exemplos e não foram conferidos
  contra a tabela oficial da ANS.
- **D-07** — os identificadores de preço da Paddle ficam nulos em `Plans` até a
  integração ser confirmada.
- **D-10** — os padrões de `BookingPolicies` (24 h de antecedência, 60 dias de
  horizonte, 4 compromissos em aberto) são chute razoável, não decisão tomada.
- **F-05** — as listas de elementos em `ConformityRulesets` estão marcadas
  `draft-1` porque foram escritas a partir da estrutura geral da Res. CFP
  006/2019, sem conferência linha a linha. O valor inteiro de F-05 é a lista
  estar certa; ler a resolução antes de o validador bloquear uma assinatura.
