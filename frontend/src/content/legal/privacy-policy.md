# Política de Privacidade — Anamnys

**Última atualização:** 2026/08/15

---

## 1. A quem esta política se aplica

Esta política descreve como o **Anamnys** trata dados pessoais, em conformidade com a **Lei nº 13.709/2018 (LGPD)**.

Ela se aplica a dois grupos distintos, com regimes distintos:

- **Profissionais e clínicas** que contratam o serviço;
- **Pacientes** cujos dados são tratados pelo profissional por meio do serviço.

---

## 2. Nossos papéis: onde somos controlador e onde somos operador

| Dados                                                 | Quem é controlador              | Quem é operador   |
| ----------------------------------------------------- | ------------------------------- | ----------------- |
| Cadastro do profissional, cobrança, uso da plataforma | **Anamnys**               | —                 |
| **Dados clínicos de pacientes**                       | **O profissional ou a clínica** | **Anamnys** |

**2.1.** Quanto aos **dados dos pacientes**, o profissional ou a clínica é o **controlador**: decide
por que e como os dados são tratados, e os registros lhe pertencem. O Anamnys atua como
**operador**, tratando os dados exclusivamente para executar o serviço, conforme as instruções do
controlador. **Esta Política e os Termos de Uso constituem, em conjunto, o instrumento que formaliza
a relação entre controlador e operador**, dispensando contrato apartado — sem prejuízo do direito de
o profissional ou a clínica solicitar instrumento específico.

**2.2.** Quanto aos **dados cadastrais e de uso do profissional**, o Anamnys é o
**controlador**.

**2.3.** **Não existem suboperadores com acesso a conteúdo clínico.** Em particular, nenhum
fornecedor de inteligência artificial processa dados de pacientes — os modelos são executados em
nossa própria infraestrutura.

---

## 3. Dados que tratamos

### 3.1. Do profissional (somos controladores)

- Identificação: nome, CPF, e-mail, telefone, registro no conselho de classe;
- Dados de cobrança: coletados e processados diretamente pela **Paddle.com Market Ltd.**, vendedor
  registrado da assinatura; **não temos acesso ao número completo do cartão** e não o armazenamos;
- Dados de uso: registros de acesso, endereço IP, dispositivo, navegador;
- Comunicações de suporte.

### 3.2. Dos pacientes (somos operadores)

- Identificação fornecida pelo profissional: nome, data de nascimento, contato;
- **Dados pessoais sensíveis de saúde**: registros de sessão, evoluções, diagnósticos anotados,
  escalas aplicadas, planos terapêuticos;
- Documentos anexados pelo profissional: laudos, exames, receitas, relatórios, autorizações;
- Transcrições de sessão, quando a captura de áudio estiver ativada com consentimento;
- Dados administrativos: agendamentos, comparecimento, guias e códigos de convênio.

### 3.3. O que **não** tratamos

- Áudio de sessões de forma persistente (ver seção 6);
- Vídeo de sessões (ver seção 8);
- Dados de pacientes para finalidade diversa da prestação do serviço ao profissional;
- Dados para treinamento de modelos de inteligência artificial.

---

## 4. Para que tratamos

| Finalidade                                        | Dados envolvidos                                     |
| ------------------------------------------------- | ---------------------------------------------------- |
| Transcrever e estruturar registros clínicos       | Áudio (transitório), transcrição, texto do registro  |
| Organizar documentos anexados                     | Documentos e dados neles declarados                  |
| Exibir informações do caso ao longo do tempo      | Marcadores, escalas, datas, trechos de origem        |
| Conferir estrutura documental (Res. CFP 006/2019) | Conteúdo do documento em elaboração                  |
| Preparar guias e códigos de convênio              | Conduta registrada, dados do paciente e da operadora |
| Compartilhar documentos com terceiros autorizados | Documento, registro de autorização, log de acesso    |
| Manter trilha de auditoria                        | Autoria, data e natureza de cada ação                |
| Faturar e prestar suporte                         | Dados cadastrais e de uso do profissional            |
| Cumprir obrigações legais e regulatórias          | Conforme exigido                                     |

---

## 5. Bases legais

| Tratamento                                 | Base legal (LGPD)                                                                                                                |
| ------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------- |
| Dados de saúde de pacientes                | Art. 11, II, "f" — **tutela da saúde**, em procedimento realizado por profissional de saúde, com o profissional como controlador |
| Gravação de sessão (quando ativada)        | Art. 11, I — **consentimento** específico e destacado do titular, com termo escrito nos moldes da Res. CFP nº 13/2022            |
| Cadastro e execução do serviço             | Art. 7º, V — **execução de contrato**                                                                                            |
| Cobrança e prevenção a fraude              | Art. 7º, V e IX — contrato e **legítimo interesse**                                                                              |
| Guarda documental e obrigações do conselho | Art. 7º, II — **cumprimento de obrigação legal ou regulatória**                                                                  |
| Segurança e registros de acesso            | Art. 7º, II — obrigação legal (Marco Civil da Internet)                                                                          |

---

## 6. Como o áudio e a transcrição são tratados

**6.1. O áudio é transitório.** Quando a captura está ativa e consentida, o áudio é mantido apenas em
memória durante o processamento. Ele **não é gravado em disco, não integra cópias de segurança e não
é enviado a terceiros**. Imediatamente após a transcrição, é destruído. A destruição é registrada na
trilha de auditoria — o registro contém a data e a identificação da sessão, nunca conteúdo.

**6.2. Em caso de falha na transcrição, o áudio é destruído do mesmo modo.** Não mantemos fila de
reprocessamento com áudio de paciente.

**6.3. A transcrição é armazenada como fonte, não como registro oficial.** Ela existe para que cada
informação exibida possa ser rastreada até o trecho que a originou. **A transcrição não é exportada,
não é compartilhada e não integra dossiês.** O documento oficial é sempre a nota revisada e assinada
pelo profissional.

**6.4. Nomes de terceiros são substituídos antes do armazenamento.** Quando o paciente menciona
outras pessoas, o sistema substitui esses nomes por identificadores, preservando o vínculo declarado
(por exemplo, "meu irmão João" torna-se "meu irmão [P1]"). O mesmo terceiro recebe sempre o mesmo
identificador, e **não mantemos tabela de correspondência reversível**.

**6.5. Limite desta medida, declarado com franqueza.** Trata-se de **pseudonimização**, não de
anonimização: o reconhecimento automático de nomes é imperfeito e o contexto pode, por si só,
permitir identificação. Nos termos do art. 13, §4º da LGPD, o dado permanece **dado pessoal**, com
todas as proteções aplicáveis.

---

## 7. Por quanto tempo guardamos

| Dado                                   | Prazo                                                                                                                           |
| -------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------- |
| Áudio de sessão                        | **Não é armazenado** — destruído após a transcrição                                                                             |
| Registros e documentos clínicos        | Conforme definido pelo profissional, observado o mínimo de **5 anos** (Res. CFP nº 001/2009) e prazos maiores quando aplicáveis |
| Transcrições                           | Configurável pelo controlador, podendo ser inferior ao prazo do prontuário — padrão: **[PRAZO]**                                |
| Documentos anexados                    | Mesmo prazo do prontuário ao qual pertencem                                                                                     |
| Trilha de auditoria                    | Mesmo prazo do documento a que se refere                                                                                        |
| Registros de compartilhamento e acesso | **[PRAZO]**                                                                                                                     |
| Cadastro do profissional               | Enquanto vigente a relação, e por **[PRAZO]** após o encerramento                                                               |
| Registros de acesso à aplicação        | 6 meses (art. 15 do Marco Civil da Internet)                                                                                    |
| Dados fiscais e de cobrança            | Conforme legislação tributária                                                                                                  |

Encerrado o contrato, os dados permanecem disponíveis para exportação por **[60] dias**. Após esse
prazo, são eliminados de forma definitiva, salvo obrigação legal de retenção.

---

## 8. Videochamada

**8.1.** Quando utilizada a sala de atendimento do Anamnys, a comunicação de áudio e vídeo
ocorre **diretamente entre os dispositivos dos participantes** (conexão ponto a ponto).
**O vídeo da sessão não passa pelos nossos servidores.**

**8.2.** Quando as condições de rede exigem um servidor intermediário de retransmissão, ele encaminha
pacotes **cifrados de ponta a ponta**, que não temos capacidade técnica de decifrar.

**8.3.** **O vídeo não é gravado em nenhuma hipótese.**

---

## 9. Com quem compartilhamos

**9.1. Não vendemos, alugamos ou cedemos dados pessoais.**

**9.2. Nenhum fornecedor de inteligência artificial recebe dados de pacientes.** Os modelos de
transcrição e redação são executados em infraestrutura sob nosso controle.

**9.3.** Compartilhamos apenas com:

| Destinatário                        | Finalidade                                              | Dados                                                                                    |
| ----------------------------------- | ------------------------------------------------------- | ---------------------------------------------------------------------------------------- |
| **[PROVEDOR DE NUVEM]**             | Hospedagem em território nacional                       | Dados armazenados, cifrados                                                              |
| **Paddle.com Market Ltd.** (Reino Unido) | Venda, faturamento, cobrança e recolhimento tributário | Nome, e-mail, endereço de faturamento e dados de pagamento do profissional — **nunca dados de pacientes** |
| **[PROVEDOR DE E-MAIL]**            | Comunicações transacionais                              | E-mail do profissional                                                                   |
| Autoridades competentes             | Cumprimento de ordem legal                              | Estritamente o exigido                                                                   |

**9.4.** Destinatários indicados pelo profissional, por meio da funcionalidade de compartilhamento,
recebem apenas o documento específico autorizado.

---

### 9.5. Transferência internacional de dados

**9.5.1. Onde os dados clínicos ficam armazenados.** Os dados clínicos de pacientes são armazenados e
processados em **território brasileiro**, em infraestrutura contratada para esse fim. A execução dos
modelos de transcrição e redação também ocorre nessa infraestrutura.

**9.5.2. Declaramos, com transparência, que há transferência internacional.** Duas situações
concretas a caracterizam:

a) **Acesso administrativo a partir do exterior.** A operação técnica do Anamnys é conduzida a
   partir do **Canadá**. O acesso remoto a dados hospedados no Brasil, ainda que os dados não sejam
   copiados, configura tratamento por agente situado no exterior. Esse acesso é restrito, registrado
   em trilha de auditoria e, em relação a conteúdo clínico, ocorre **apenas mediante solicitação
   expressa de suporte pelo profissional** (seção 10).

b) **Dados cadastrais e de cobrança.** Nome, e-mail e dados de faturamento do **profissional** são
   transferidos à **Paddle.com Market Ltd.**, no Reino Unido, para emissão de documento fiscal,
   cobrança e recolhimento tributário. **Nenhum dado de paciente integra essa transferência.**

**9.5.3. Fundamento da transferência.** As transferências observam o **art. 33 da LGPD** e a
**Resolução ANPD nº 19/2024**, e estão amparadas por:

- **cláusulas contratuais padrão** aprovadas pela ANPD (Anexo II da Resolução nº 19/2024), adotadas
  na relação entre controlador e operador e, quando aplicável, entre operador e suboperador;
- quanto ao **Canadá**, pelo regime de proteção de dados pessoais ali vigente (**PIPEDA** e leis
  provinciais correlatas), reconhecido como adequado pela Comissão Europeia — referência de padrão
  equivalente, ainda que não substitua as cláusulas acima;
- quanto ao **Reino Unido**, pelo regime de proteção de dados ali vigente (**UK GDPR** e
  *Data Protection Act 2018*) e pelo compromisso contratual da Paddle com padrão de proteção
  equivalente ao da LGPD.

**9.5.4.** Não realizamos transferência de dados clínicos para finalidade comercial, publicitária ou
de treinamento de modelos, em nenhuma jurisdição.

**9.5.5.** Alterações relevantes de país de hospedagem ou de destinatários internacionais serão
comunicadas na forma da seção 15, com antecedência mínima de 30 dias.

---

## 10. Segurança

- Criptografia em trânsito (TLS) e em repouso (AES-256);
- Isolamento por profissional: cada um acessa apenas os próprios pacientes;
- Autenticação individual; vedado o compartilhamento de credenciais;
- Trilha de auditoria não editável de leituras, alterações, assinaturas e exportações;
- Armazenamento de documentos inacessível publicamente, com acesso por URL assinada e temporária;
- Registro e revisão periódica de acessos administrativos.

**Acesso da nossa equipe.** Em operação normal, nossa equipe **não acessa conteúdo clínico**. O
acesso técnico ocorre apenas mediante solicitação expressa de suporte pelo profissional, é limitado ao
necessário e fica registrado na trilha de auditoria, visível ao profissional.

---

## 11. Direitos dos titulares

Nos termos dos arts. 17 a 22 da LGPD, o titular pode requerer confirmação de tratamento, acesso,
correção, anonimização ou eliminação, portabilidade, informação sobre compartilhamentos, revogação de
consentimento e oposição a tratamento irregular.

**11.1. A quem o paciente deve se dirigir.** Como o **controlador dos dados clínicos é o profissional
ou a clínica**, pedidos de pacientes devem ser dirigidos a eles. Recebendo pedido diretamente,
encaminhamos ao controlador e prestamos o apoio técnico necessário.

**11.2. Limite ao direito de eliminação.** Registros clínicos estão sujeitos a prazos legais e
regulatórios de guarda. A revogação do consentimento para gravação interrompe novas capturas, mas
**não** elimina registros já assinados.

**11.3. Prazo.** Respondemos em até **15 dias** contados da solicitação.

---

## 12. Cookies

Utilizamos cookies estritamente necessários à autenticação, à segurança e ao funcionamento da
aplicação. **Não utilizamos cookies de publicidade nem de rastreamento entre sites.**
Cookies analíticos, se empregados, dependem de consentimento e podem ser recusados sem prejuízo ao
uso do serviço.

---

## 13. Incidentes de segurança

Havendo incidente que possa acarretar risco ou dano relevante aos titulares, comunicaremos ao
controlador **em até [48] horas** da ciência, e à Autoridade Nacional de Proteção de Dados e aos
titulares nos prazos e condições da LGPD, informando a natureza dos dados, os titulares envolvidos,
as medidas adotadas e os riscos identificados.

---

## 14. Uso para desenvolvimento e melhoria

**14.1. Não utilizamos dados de pacientes para treinar modelos de inteligência artificial.**
Os modelos empregados são estáticos e não aprendem com o conteúdo processado.

**14.2.** Métricas agregadas e sem identificação — volume de uso, tempo de resposta, taxas de erro —
podem ser utilizadas para operação e melhoria do serviço.

**14.3.** Ambientes de teste e desenvolvimento **não** utilizam dados reais de pacientes.

---

## 15. Alterações desta política

Alterações relevantes serão comunicadas com antecedência mínima de **30 dias**, por e-mail e no
serviço. O histórico de versões fica disponível em **[URL]**.

---

## 16. Contato e Encarregado

**Encarregado pelo Tratamento de Dados (DPO):** **[NOME]**
E-mail: **[E-MAIL DO ENCARREGADO]**

Nos termos da **Resolução ANPD nº 18/2024**, o encarregado pode ser pessoa física ou jurídica,
interna ou externa à organização, e atende em português, por canal de acesso facilitado, ainda que o
Anamnys esteja estabelecido no exterior.

**[NOME COMPLETO DO TITULAR]** — Canadá — **[ENDEREÇO]**

Questões relativas a faturamento e dados de pagamento podem também ser dirigidas à
**Paddle.com Market Ltd.**, vendedor registrado da assinatura.

O titular pode também peticionar à **Autoridade Nacional de Proteção de Dados (ANPD)**.
