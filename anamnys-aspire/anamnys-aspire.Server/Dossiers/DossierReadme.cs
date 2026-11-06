using System.Globalization;
using System.Text;
using Anamnys.Server.Exports;
using Anamnys.Server.Integrity;

namespace Anamnys.Server.Dossiers;

// LEIA-ME.txt of the dossier (design/specs/2026-11-04-patient-dossier-design.md §4): what each file is, how to check the
// files and the timestamp tokens, and what is not included. No patient name.
public static class DossierReadme
{
    public static string Text(DossierRequest request, IReadOnlyList<DossierAnchor> anchors)
    {
        var tokens = new StringBuilder();
        foreach (var a in anchors)
        {
            var digest = Convert.ToHexStringLower(TimestampTokens.Imprint(a.HeadHash));
            tokens.Append(CultureInfo.InvariantCulture,
                $"  integridade/carimbos/{a.Sequence}.tsr\r\n    hash do selo nº {a.Sequence}: {a.HeadHash}\r\n    resumo carimbado (SHA-256 do hash acima): {digest}\r\n");
        }
        if (anchors.Count == 0)
        {
            tokens.Append("  (nenhum carimbo de tempo cobre os registros deste dossiê ainda)\r\n");
        }

        var text = $"""
        DOSSIÊ DO PACIENTE Nº {request.Number:000}
        =========================

        Gerado em: {ExportFormat.Display(request.GeneratedAt)} (horário de Brasília)
        Período solicitado: {DossierPdf.PeriodText(request.PeriodFrom, request.PeriodTo)}

        Este arquivo contém dados de saúde do paciente. Entregue-o apenas a quem tem direito e guarde-o
        em local seguro.

        Comece por "dossie.pdf": é o documento a ser arquivado. Ele identifica o profissional e o
        paciente, traz o resumo cronológico do prontuário, as divulgações, a conformidade dos
        documentos, a guarda do prontuário e o resultado da verificação de integridade.

        Arquivos
        --------
        dossie.pdf          O dossiê (capa, resumo cronológico, divulgações, conformidade, guarda e
                            integridade).
        manifesto.json      Lista de todos os arquivos do ZIP (exceto ele mesmo) com o tamanho em bytes
                            e o hash SHA-256 de cada um.
        LEIA-ME.txt         Este arquivo.
        notas/AAAA-MM-DD-nota[-N].pdf
                            PDF de cada nota assinada no período (data da assinatura; -2, -3... quando há
                            mais de uma no mesmo dia).
        documentos/AAAA-MM-DD-<tipo>[-N][-substituido].pdf
                            PDF de cada documento psicológico assinado no período, em todas as versões
                            (data da assinatura). "-substituido" indica uma versão corrigida e substituída
                            por outra versão assinada; ela continua sendo um documento histórico válido.
                            Uma correção ainda em rascunho não marca o original como substituído.
        externos/AAAA-MM-DD-documento-externo[-N].<ext>
                            Documentos recebidos de fora (exames, laudos, receitas...), exatamente como
                            foram enviados (data do envio). O nome original do arquivo não é usado no
                            nome: ele aparece apenas no resumo de dossie.pdf.
        divulgacoes/AAAA-MM-DD-termo[-N].<ext>
                            Termos de autorização de compartilhamento anexados pelo profissional, exatamente
                            como foram enviados (data da autorização). Uma divulgação entra no dossiê
                            quando a autorização, a criação, revogação ou expiração de um link, ou um
                            acesso cai no período; ela vem então com todos os seus links e acessos.
        integridade/comprovante.pdf
                            Comprovante de integridade restrito aos selos das notas e documentos deste
                            dossiê: cada selo com o resultado da verificação, e os carimbos de tempo.
        integridade/carimbos/<sequência>.tsr
                            Token RFC 3161 (DER) de cada carimbo de tempo que cobre esses selos.

        Como conferir os arquivos
        -------------------------
        O hash SHA-256 de cada arquivo está em manifesto.json. Em Linux ou macOS, com o jq instalado,
        dentro da pasta onde o ZIP foi extraído:

          jq -r '.files[] | "\(.sha256)  \(.path)"' manifesto.json | sha256sum -c

        Cada linha deve terminar em "OK". Sem o jq, calcule o hash de um arquivo com
        "sha256sum <arquivo>" (Linux), "shasum -a 256 <arquivo>" (macOS) ou
        "Get-FileHash <arquivo> -Algorithm SHA256" (Windows PowerShell) e compare com o manifesto.

        Como conferir os carimbos de tempo
        ----------------------------------
        Cada nota e documento assinado recebe um selo encadeado ao anterior; o hash do último selo é
        carimbado por uma Autoridade de Carimbo de Tempo (RFC 3161). O carimbo de um selo cobre todos
        os selos de número menor. O que a autoridade carimbou é o SHA-256 do hash do selo (o texto em
        hexadecimal, sem quebra de linha). Com o certificado raiz da autoridade (raiz.pem):

          openssl ts -verify -digest <sha256> -in integridade/carimbos/<sequência>.tsr -token_in -CAfile raiz.pem

        onde <sha256> é o "resumo carimbado" abaixo. Equivalente, a partir do hash do selo:

          printf '%s' <hash do selo> > cabeca.txt
          openssl ts -verify -data cabeca.txt -in integridade/carimbos/<sequência>.tsr -token_in -CAfile raiz.pem

        A resposta esperada é "Verification: OK".

        {tokens.ToString().TrimEnd()}

        O que NÃO está incluído, e por quê
        ----------------------------------
        - Rascunhos de notas e documentos: não fazem parte do prontuário assinado.
        - Transcrições de voz e textos digitados antes da estruturação da nota: só o texto revisado e
          assinado faz parte do prontuário.
        - Valores do histórico de auditoria: o dossiê traz o prontuário, não o histórico de edição.
        - Dados estruturados em planilha (escalas, diagnósticos, medicações, objetivos).
        - Documentos externos bloqueados pela verificação de vírus, cuja verificação falhou ou ainda não
          verificados: aparecem no resumo de dossie.pdf como "arquivo não incluído" ("bloqueado",
          "verificação falhou" ou "pendente de verificação").
        - Documentos externos excluídos pelo profissional.
        - Registros fora do período solicitado.
        - Dados de outros pacientes.
        - O hash SHA-256 deste ZIP e o seu carimbo de tempo: um arquivo não pode conter o próprio hash.
          Eles estão no comprovante de entrega do dossiê, que o profissional obtém no Anamnys.
        """;
        return "﻿" + text.ReplaceLineEndings("\r\n") + "\r\n";
    }
}
