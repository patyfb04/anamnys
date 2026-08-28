# Perguntas Frequentes — texto do site

**Última atualização:** 2026/08/15
**Idioma:** português do Brasil

> Antes de publicar, ler `_Notas_de_Implementacao.md` nesta mesma pasta. Há restrições de
> linguagem que não são estilísticas.

---

## Sobre a inteligência artificial

### A IA vai diagnosticar meu paciente?

Não, e isso é deliberado.

O sistema transcreve, organiza, localiza, conta e compara o que **você** registrou. Ele
não produz interpretação clínica, conclusão, hipótese diagnóstica, avaliação de risco nem
recomendação de conduta.

O Manual Orientativo de Registro e Elaboração de Documentos Psicológicos (CFP, 2025), no
item 16, reserva esses atos à competência privativa do profissional e afirma que a
responsabilidade sobre documento profissional *"não pode ser terceirizada a máquinas ou
softwares"*. Uma ferramenta que atravessa essa linha não está sendo mais avançada — está
transferindo risco para você.

### Então o que a IA faz, exatamente?

Formata e dá clareza ao texto. Sugere estrutura de documento. Sistematiza dados de
entrevistas, escalas e observações. Organiza relatórios longos. Localiza onde algo foi
registrado. Conta ocorrências e compara datas.

São exatamente os usos que o Manual do CFP admite.

### Como vocês garantem que ela não ultrapassa esse limite?

Por construção, não por instrução.

O sistema opera com um conjunto fechado de verbos de observação — *aparece*, *ocorre*,
*foi registrado*, *variou de X para Y*. Verbos avaliativos — *sugere*, *indica*, *melhorou*,
*é compatível com* — não fazem parte do vocabulário que ele pode produzir.

E há uma regra rígida: **o que não consegue apontar a passagem de origem não é exibido.**
Se o sistema não sabe mostrar de onde tirou, ele não mostra.

### A IA pode errar?

Pode, e vai. Transcrição erra com nomes próprios, termos técnicos e áudio ruim.
Estruturação erra de ênfase.

É por isso que **todo conteúdo nasce como rascunho** e nada é registrado sem sua revisão.
A conferência é obrigatória e é sua responsabilidade — está nos Termos de Uso, com todas
as letras.

---

## Sobre gravação de sessão

### Vocês gravam minhas sessões?

Só se você ativar, e só com o termo do paciente assinado.

A captura de áudio é **opcional e vem desativada**. Para habilitar, é preciso registrar no
sistema um termo de consentimento livre, prévio, informado e por escrito do paciente, nos
moldes da Resolução CFP nº 13/2022. Sem termo assinado e vigente, a funcionalidade fica
**bloqueada** — não é um aviso que dá para ignorar.

### O que acontece com o áudio?

É transcrito e **destruído em seguida**. Não é gravado em disco, não entra em cópia de
segurança, não é enviado a terceiros. Se a transcrição falhar, o áudio é destruído do
mesmo jeito — não mantemos fila de reprocessamento com áudio de paciente.

A destruição fica registrada na trilha de auditoria, com data e identificação da sessão,
nunca com conteúdo.

### E a transcrição, fica guardada?

Fica, como **fonte** — não como registro oficial.

Ela existe para que cada informação exibida possa ser rastreada até o trecho que a
originou. A transcrição **não é exportada, não é compartilhada e não entra em dossiê**. O
documento oficial é sempre a nota revisada e assinada por você.

### E se o paciente mudar de ideia?

O consentimento pode ser revogado a qualquer momento. A revogação interrompe imediatamente
novas capturas.

Ela **não** apaga registros clínicos já assinados — esses seguem as regras próprias de
guarda documental, e o termo de consentimento diz isso claramente ao paciente antes de ele
assinar.

### Posso usar sem gravar nada?

Pode, e é o modo padrão. Você narra a sessão **depois** que ela termina, com o paciente já
tendo saído. Nesse caso não há áudio de sessão nenhum, e o termo de consentimento não é
necessário.

---

## Sobre segurança e privacidade

### Meus dados vão para a OpenAI ou o Google?

Não. Os modelos de transcrição e de redação rodam na nossa própria infraestrutura.

Isso é a diferença entre "temos contrato com o fornecedor" e "não há fornecedor". A quase
totalidade das ferramentas de IA clínica é intermediária: recebe o seu dado e repassa para
um terceiro processar. Aqui a cadeia termina em nós.

### Onde ficam armazenados os dados?

Os dados clínicos são armazenados e processados **em território brasileiro**, sob
jurisdição brasileira.

A operação técnica é conduzida do **Canadá**. Esse acesso administrativo é restrito,
registrado em trilha de auditoria e, quanto a conteúdo clínico, **só ocorre mediante pedido
expresso de suporte seu**. Está declarado na Política de Privacidade e amparado nas
cláusulas contratuais padrão da ANPD (Resolução nº 19/2024).

Dizemos isso abertamente porque é o que a LGPD exige e porque você descobriria de qualquer
forma.

### Vocês usam meus dados para treinar a IA?

Não. Os modelos são estáticos e não aprendem com o conteúdo processado. Além de estar
vedado em contrato, é tecnicamente inaplicável ao que usamos.

### Quem, aí dentro, pode ver o prontuário do meu paciente?

Em operação normal, ninguém. O acesso técnico só acontece se **você** pedir suporte, é
limitado ao necessário e **aparece na sua trilha de auditoria** — você vê que aconteceu.

### O que é essa substituição de nomes?

Quando o paciente menciona terceiros, o sistema troca os nomes por identificadores e
preserva o vínculo declarado: *"meu irmão João"* vira *"meu irmão [P1]"*. A mesma pessoa
recebe sempre o mesmo identificador, e não guardamos tabela de correspondência reversível.

**O limite, dito com franqueza:** isso é pseudonimização, não anonimização. O
reconhecimento de nomes é imperfeito — apelidos e prenomes incomuns escapam — e o contexto
pode identificar por si só. Pela LGPD (art. 13, §4º), o dado continua sendo dado pessoal,
com todas as proteções aplicáveis.

Quem promete "anonimização" nesse cenário está prometendo o que a lei não reconhece.

### E se houver um vazamento?

Comunicamos você — que é o controlador dos dados — em até 48 horas da ciência, e a ANPD e
os titulares nos prazos e condições da LGPD, informando a natureza dos dados, quem foi
atingido, as medidas tomadas e os riscos identificados.

---

## Sobre responsabilidade profissional

### Quem responde pelo documento assinado?

Você, integralmente. Autoria, conteúdo clínico e responsabilidade técnica e ética são do
profissional que assina.

O uso da ferramenta não transfere, não compartilha e não reduz essa responsabilidade. Está
na cláusula 5 dos Termos de Uso, e reproduz o que o Manual do CFP determina.

### Então de que adianta o aviso de documentação incompleta?

Ele reduz a chance de você deixar passar algo — não certifica que está tudo certo.

Os avisos, conferências estruturais e alertas de glosa são **auxiliares e não exaustivos**.
A ausência de aviso não significa conformidade, e a presença dele não substitui o seu
julgamento. Preferimos escrever isso do que deixar você descobrir depois.

### Isso é aceito pelo CFP?

O CFP não credencia nem homologa softwares — não existe "aprovado pelo CFP" para essa
categoria, e desconfie de quem disser que tem.

O que existe é um conjunto de normas sobre como o documento psicológico deve ser feito e o
que pode ser delegado a uma ferramenta. O produto foi construído a partir delas:
Resoluções 001/2009, 006/2019, 09/2024 e 13/2022, e o Manual Orientativo de 2025.

A responsabilidade de usar a ferramenta dentro do que o seu Código de Ética permite
continua sendo sua — como acontece com qualquer instrumento de trabalho.

---

## Sobre uso prático

### Serve para fisioterapia?

Serve. Formatos de evolução, escalas de dor, goniometria e a lógica de convênio estão
contemplados. O aviso de documentação incompleta e a evidência de evolução objetiva são
particularmente úteis em pedido de prorrogação.

### Preciso de internet boa?

Para narrar e revisar, uma conexão comum resolve. A transcrição acontece no servidor, não
no seu aparelho — então celular antigo não é impedimento.

### Funciona no celular?

Funciona. Narrar pelo celular ao final da sessão é o uso mais comum. Revisão e assinatura
também funcionam bem em tela pequena.

### Quanto tempo leva para produzir uma nota?

Menos de dois minutos entre terminar de narrar e ter o rascunho pronto para revisão. A
revisão em si depende de você — costuma levar de um a três minutos.

### Preciso mudar meu jeito de trabalhar?

Não. O sistema aceita narração em ordem livre, do jeito que você contaria a um supervisor.
Não há formulário a preencher nem campo obrigatório a caçar.

### E se eu não gostar?

Cancela quando quiser, sem multa e sem fidelidade. E **exporta tudo** — a exportação
integral está disponível o tempo todo, não só na saída.

---

## Sobre assinatura e pagamento

### Como funciona o teste?

**14 dias** com acesso às funcionalidades do plano escolhido. Terminando o período sem
contratação, o acesso é suspenso e seus dados ficam disponíveis para exportação.

### Por que aparece "Paddle" na minha fatura?

Porque a Paddle é a empresa que processa a venda das assinaturas do Anamnys — o que
se chama de *vendedor registrado*. É ela que emite o documento fiscal e recolhe os
tributos sobre a venda.

Na sua fatura de cartão vai constar **Paddle**, não Anamnys. É esperado, e não é
cobrança indevida.

Pedidos de segunda via, correção de dados de faturamento e reembolso passam por ela — mas
você pode solicitar pelo nosso suporte, que encaminhamos.

### Vocês veem meu cartão de crédito?

Não. Os dados de pagamento são coletados e armazenados diretamente pela Paddle. Não temos
acesso ao número do cartão.

### Existe desconto no plano anual?

Existe. E além do desconto, o plano anual reduz o custo de processamento — parte disso
volta para você no preço.

### Posso trocar de plano?

Pode, a qualquer momento. A mudança vale a partir do ciclo seguinte.

---

## Se a ferramenta deixar de existir

### O que acontece com meus registros?

Essa é uma pergunta legítima para qualquer fornecedor, e mais ainda para uma operação
pequena. A resposta honesta tem três partes.

**Você já tem tudo, o tempo todo.** A exportação integral dos seus dados, em formato
aberto, está disponível permanentemente — não é um favor concedido na saída. Não depende
de a gente estar de bom humor, nem de a gente existir amanhã.

**Os documentos oficiais são PDFs assinados por você**, que você pode arquivar onde
quiser desde o primeiro dia. Eles não dependem da nossa plataforma para continuar válidos.

**Você é o controlador dos dados; nós somos operadores.** Os registros são seus, não
nossos, e isso está escrito na Política de Privacidade. Não é generosidade — é o que a
LGPD determina.

Nossa recomendação prática, que damos contra o nosso próprio interesse comercial: **exporte
periodicamente e guarde uma cópia sua.** Vale para nós e para qualquer sistema em que você
confie o prontuário dos seus pacientes.

---

## Ainda tem dúvida?

Fale direto com quem construiu o produto. Não há central de atendimento, script nem
primeiro nível.

[E-MAIL] · [BOTÃO: Agendar uma conversa]
