namespace Anamnys.Server.Exports;

// LEIA-ME.txt: every folder, file and field of the export (spec 2026-10-23 §4).
public static class ExportReadme
{
    public static string Text(string root, DateTimeOffset generatedAt) =>
        "﻿" + $"""
        EXPORTAÇÃO DA CONTA ANAMNYS
        ===========================

        Gerada em: {ExportFormat.Display(generatedAt)} (horário de Brasília)
        Pasta principal: {root}

        Este arquivo contém tudo o que você registrou no Anamnys, em formatos abertos que podem ser
        lidos sem o Anamnys. Ele contém dados de saúde dos seus pacientes: guarde-o em local seguro.

        Como ler
        --------
        Abra "index.html" em qualquer navegador (Chrome, Firefox, Edge, Safari), mesmo sem internet.
        As páginas não usam programas nem recursos externos e podem ser impressas.
        Os PDFs abrem em qualquer leitor de PDF. Os arquivos .csv abrem no Excel, LibreOffice ou
        Google Planilhas; os arquivos .json servem para importar os dados em outro sistema.

        O que NÃO está incluído
        -----------------------
        - Transcrições de voz e o texto original digitado antes da estruturação da nota: só as seções
          da nota (o texto que você revisou) são exportadas.
        - Áudio das sessões (não é guardado pelo Anamnys).
        - Os valores antigos e novos do histórico de auditoria (apenas data, ação, campo e nota/documento).
        - Dados de outros profissionais.
        - Escalas removidas por você.

        Formatos
        --------
        - Codificação: UTF-8 em todos os arquivos de texto.
        - CSV: UTF-8 com BOM (abre com acentos corretos no Excel), separador ";" (ponto e vírgula),
          primeira linha com os nomes das colunas, textos com ";" ou quebra de linha entre aspas.
          Valores sim/não aparecem como "sim" e "não". Um texto que começa com =, +, -, @, tabulação
          ou retorno de carro recebe um apóstrofo (') na frente, para que a planilha não o execute
          como fórmula; o apóstrofo não faz parte do texto original.
        - Datas: ISO 8601. Datas com hora no horário de Brasília com o deslocamento
          (ex.: 2026-10-19T11:00:00-03:00); datas sem hora como AAAA-MM-DD.
        - JSON: UTF-8, datas ISO 8601 como acima, campos ausentes como null.
        - Identificadores (id) são os códigos internos do Anamnys (UUID). O "código" de uma nota ou
          documento (8 caracteres) é o que aparece no rodapé do PDF.

        Estrutura
        ---------
        {root}
          index.html            Resumo da conta e lista de pacientes, com link para a página de cada um.
          LEIA-ME.txt           Este arquivo.
          conta/perfil.json     Seu perfil profissional.
          conta/agenda.csv      Todos os agendamentos.
          conta/auditoria.csv   Histórico de auditoria das notas, documentos e da conta, sem valores.
          pacientes/NNN-XX/     Uma pasta por paciente, inclusive arquivados. NNN é o número do paciente
                                em ordem alfabética do nome; XX são as iniciais. O nome completo não
                                aparece em nenhum nome de pasta ou arquivo.
            paciente.html       Prontuário legível: cadastro, diagnósticos, medicações, plano terapêutico,
                                escalas, temas, notas (com as seções) e documentos (com links para os PDFs).
            paciente.json       Os mesmos dados, estruturados.
            notas/AAAA-MM-DD-assinada[-N].pdf
                                PDF de cada nota assinada (data do atendimento; -2, -3... quando há mais de
                                uma nota assinada no mesmo dia).
            notas/notas.json    Todas as notas, inclusive rascunhos.
            notas/notas.csv     Todas as notas em planilha.
            documentos/AAAA-MM-DD-<tipo>[-N][-substituido].pdf
                                PDF de cada documento psicológico assinado (data da assinatura). O sufixo
                                "-substituido" indica um documento que foi corrigido e substituído por outro;
                                ele continua sendo um documento histórico válido.
            documentos/documentos.json
                                Todos os documentos, inclusive rascunhos e substituídos.
            escalas.csv, diagnosticos.csv, medicacoes.csv, objetivos.csv, temas.csv

        Situação das notas e documentos
        -------------------------------
        - Nota "assinada": assinada eletronicamente por você; tem PDF.
        - Nota "rascunho": ainda não assinada (em edição, em processamento, aguardando revisão ou com
          falha). Não tem PDF e não tem validade de registro assinado.
        - Documento "assinado": assinado; tem PDF. Em casos raros o PDF de um documento assinado não pode
          ser recuperado nem gerado de novo: o campo "pdf" fica null e a página mostra "PDF indisponível";
          o texto do documento continua em documentos.json e em paciente.html.
        - Documento "substituido": assinado e depois substituído por uma correção (veja "substituidoPor").
        - Documento "rascunho": ainda não assinado; sem PDF.

        conta/perfil.json
        -----------------
        nome, email                  Seu nome e e-mail de acesso.
        crp                          Número/região do CRP.
        cidadeAtendimento            Cidade de atendimento usada nos documentos.
        especialidade                Especialidade cadastrada (MentalHealth = saúde mental).
        formatoNotaPreferido         Formato padrão das notas (DAP, SOAP, Biopsychosocial).
        modoVerificacaoDocumentos    blocking = a verificação de conformidade impede assinar;
                                     advisory = apenas avisa.
        contaCriadaEm                Data de criação da conta.
        exportacaoGeradaEm           Data desta exportação.
        totalPacientes               Número de pacientes exportados.

        conta/agenda.csv
        ----------------
        paciente          Pasta do paciente (NNN-XX).
        inicio, fim       Início e fim do agendamento.
        fuso              Fuso horário do agendamento.
        status            scheduled = agendado, confirmed = confirmado, attended = realizado,
                          cancelled = cancelado, no_show = falta.
        modalidade        presencial ou online.
        criado_por        Quem criou: provider (você) ou patient (o paciente, pelo portal).
        cancelado_em      Data do cancelamento, se houver.
        cancelado_por     Quem cancelou, se houver: provider, patient ou system (automático).
        motivo_cancelamento  Motivo registrado no cancelamento, se houver.
        sessao_id         Sessão vinculada, se houver.

        conta/auditoria.csv
        -------------------
        data              Quando a ação aconteceu.
        acao              Código da ação (ex.: note_created, ai_generated, section_edited, signed,
                          exported, pdf_link_issued, theme_added, theme_removed, document_signed,
                          document_corrected, document_exported, account_export_requested).
        campo             Campo alterado, quando houver (sem os valores).
        nota_id           Nota afetada (veja "id" em notas.json).
        documento_id      Documento afetado (veja "id" em documentos.json).

        paciente.json
        -------------
        pasta                          NNN-XX.
        nome, sobrenome, nascimento    Dados de cadastro.
        email, telefone                Contato.
        idiomaPreferido                Idioma preferido, se informado.
        ultimaConsulta                 Data da última consulta registrada.
        arquivado, arquivadoEm         Se o paciente está arquivado e desde quando.
        cadastradoEm                   Data do cadastro.
        diagnosticos                   Lista: id, descricao, cid (CID-10/11), registradoEm, resolvidoEm.
        medicacoes                     Lista: id, medicamento, dose, posologia, inicio, fim.
        planos                         Planos terapêuticos: id, criadoEm, revisadoEm, encerradoEm e
                                       objetivos (id, descricao, criadoEm).
        escalas                        Aplicações de escalas: id, instrumento (código), nomeInstrumento,
                                       pontuacao, pontuacaoMinima, pontuacaoMaxima, aplicadaEm, sessaoId.
        notas                          Como em notas.json.
        documentos                     Como em documentos.json.

        notas/notas.json (e "notas" em paciente.json)
        ---------------------------------------------
        id, codigo        Identificador e código da nota.
        data              Data do atendimento.
        situacao          assinada ou rascunho.
        status            Estado interno: Draft, Processing, ReadyForReview, Failed (rascunhos),
                          Signed, Exported (assinadas; Exported = PDF já gerado antes).
        formato           DAP, SOAP, Biopsicossocial, PtFunctional.
        criadaEm, atualizadaEm, assinadaEm, crpAssinatura
        pdf               Caminho do PDF dentro da pasta do paciente (só notas assinadas).
        secoes            Seções da nota: chave, titulo, conteudo, editadaPeloProfissional
                          (true quando você editou o texto proposto).
        temas             Temas da nota: codigo, tema, temaPai, origem (ia = proposto pela IA,
                          profissional = adicionado por você), proposto (true = proposta da IA ainda não
                          confirmada por você), adicionadoEm.

        notas/notas.csv
        ---------------
        id; codigo; data; situacao; status; formato; assinada_em; crp_assinatura; pdf; secoes; temas
        "secoes" traz cada seção como "Título: conteúdo", separadas por uma linha em branco;
        "temas" traz os temas separados por ", ".

        documentos/documentos.json
        --------------------------
        id, codigo        Identificador e código do documento.
        tipo, nomeTipo    declaracao, atestado, relatorio, relatorio_multiprofissional, laudo, parecer.
        situacao          assinado, substituido ou rascunho.
        criadoEm, atualizadoEm, assinadoEm
        substituidoPor    id do documento que corrigiu e substituiu este.
        notaId            Nota de origem, se o documento foi criado a partir de uma nota.
        pdf               Caminho do PDF dentro da pasta do paciente (só documentos assinados; null quando
                          o PDF está indisponível, veja acima).
        assinatura        nome, crp, cidade e nomePaciente registrados no momento da assinatura.
        campos            Campos preenchidos: codigo, rotulo, valor.
        equipe            Relatório multiprofissional: nome, categoria, registro, analise de cada membro.
        corpo             Texto do documento (declaração e atestado) ou texto montado dos campos.

        escalas.csv
        -----------
        id; instrumento; nome_instrumento; pontuacao; pontuacao_minima; pontuacao_maxima; aplicada_em; sessao_id

        diagnosticos.csv
        ----------------
        id; descricao; cid; registrado_em; resolvido_em

        medicacoes.csv
        --------------
        id; medicamento; dose; posologia; inicio; fim

        objetivos.csv
        -------------
        plano_id; plano_criado_em; plano_revisado_em; plano_encerrado_em; objetivo_id; objetivo; criado_em
        (um plano sem objetivos aparece em uma linha com as colunas do objetivo vazias)

        temas.csv
        ---------
        nota_id; data_nota; codigo; tema; tema_pai; origem; proposto; adicionado_em
        Uma linha por tema ativo em cada nota (temas removidos não aparecem). Em paciente.html, temas
        propostos pela IA e ainda não confirmados aparecem com "(proposta da IA)".
        """.ReplaceLineEndings("\r\n");
}
