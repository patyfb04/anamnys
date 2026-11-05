using System.Text;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Integrity;
using FluentAssertions;

namespace Anamnys.Tests.Integrity;

// The canonical content that is sealed and verified (design/specs/2026-11-03-integrity-chain-design.md §3).
public class IntegrityCanonicalTests
{
    private static readonly Guid NoteId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid PatientId = Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");
    private static readonly DateTimeOffset SignedAt = new(2026, 11, 3, 9, 30, 45, 123, TimeSpan.FromHours(-3));

    private static Note SignedNote() => new()
    {
        Id = NoteId,
        PatientId = PatientId,
        Format = "DAP",
        SignedByCrp = "12345/06",
        SignedAt = SignedAt,
    };

    private static NoteSection Section(string key, int position, string content) =>
        new() { Id = Guid.NewGuid(), NoteId = NoteId, Key = key, Position = position, Content = content };

    private static List<NoteSection> Sections() => [Section("data", 0, "a"), Section("assessment", 1, "b"), Section("plan", 2, "c")];

    private static ClinicalDocument SignedDocument() => new()
    {
        Id = Guid.Parse("99999999-8888-7777-6666-555555555555"),
        PatientId = PatientId,
        Kind = "atestado",
        Body = "Corpo",
        Fields = new() { ["b"] = "2", ["a"] = "1" },
        Confirmations = ["z", "y"],
        Team = [new TeamMember("Ana", "Psicóloga", "CRP 06/1", "Análise")],
        SignedAt = SignedAt,
        SignedByCrp = "12345/06",
        SignedByName = "Dra. Teste",
        SignedCity = "São Paulo",
        SignedPatientName = "Ana Silva",
    };

    // The expected JSON is written with single quotes to keep the literals readable.
    private static string Q(string text) => text.Replace('\'', '"');

    private static string Json(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    [Fact]
    public void ForNote_SameInput_GivesTheSameBytesAndHash()
    {
        // Arrange
        var first = IntegrityCanonical.ForNote(SignedNote(), Sections());
        var second = IntegrityCanonical.ForNote(SignedNote(), Sections());

        // Act
        var hash = IntegrityCanonical.Hash(first);

        // Assert
        second.Should().Equal(first);
        IntegrityCanonical.Hash(second).Should().Be(hash);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ForNote_FixedKeyOrderAndFormats()
    {
        // Arrange / Act
        var json = Json(IntegrityCanonical.ForNote(SignedNote(), Sections()));

        // Assert
        json.Should().Be(Q(
            @"{'v':1,'type':'note','id':'11111111-2222-3333-4444-555555555555','patientId':'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'," +
            @"'format':'DAP','sections':[{'key':'data','position':0,'content':'a'},{'key':'assessment','position':1,'content':'b'}," +
            @"{'key':'plan','position':2,'content':'c'}],'signedByCrp':'12345/06','signedAt':'2026-11-03T12:30:45.123000Z'}"));
    }

    [Fact]
    public void ForNote_SectionsInAnyOrder_AreSortedByPositionThenKey()
    {
        // Arrange
        var shuffled = new List<NoteSection> { Section("plan", 2, "c"), Section("b", 5, "x"), Section("a", 5, "y"), Section("data", 0, "a") };
        var ordered = new List<NoteSection> { Section("data", 0, "a"), Section("plan", 2, "c"), Section("a", 5, "y"), Section("b", 5, "x") };

        // Act
        var fromShuffled = IntegrityCanonical.ForNote(SignedNote(), shuffled);
        var fromOrdered = IntegrityCanonical.ForNote(SignedNote(), ordered);

        // Assert
        fromShuffled.Should().Equal(fromOrdered);
        Json(fromShuffled).Should().Contain(Q(@"'sections':[{'key':'data','position':0"));
    }

    [Fact]
    public void ForNote_NfcAndNfdText_GiveTheSameHash()
    {
        // Arrange
        var nfc = new List<NoteSection> { Section("data", 0, "A sessão começou") };
        var nfd = new List<NoteSection> { Section("data", 0, "A sessão começou".Normalize(NormalizationForm.FormD)) };
        nfd[0].Content.Should().NotBe(nfc[0].Content);

        // Act
        var fromNfc = IntegrityCanonical.Hash(IntegrityCanonical.ForNote(SignedNote(), nfc));
        var fromNfd = IntegrityCanonical.Hash(IntegrityCanonical.ForNote(SignedNote(), nfd));

        // Assert
        fromNfd.Should().Be(fromNfc);
    }

    [Fact]
    public void ForNote_AnyChangeOfContent_ChangesTheHash()
    {
        // Arrange
        var original = IntegrityCanonical.Hash(IntegrityCanonical.ForNote(SignedNote(), Sections()));
        var edited = Sections();
        edited[1].Content = "B";

        // Act
        var hash = IntegrityCanonical.Hash(IntegrityCanonical.ForNote(SignedNote(), edited));

        // Assert
        hash.Should().NotBe(original);
    }

    [Fact]
    public void ForNote_AccentsAndQuote_AreWrittenAsExactUtf8Bytes()
    {
        // Arrange
        var sections = new List<NoteSection> { Section("data", 0, "Disse: \"não sei\" – ação") };

        // Act
        var bytes = IntegrityCanonical.ForNote(SignedNote(), sections);

        // Assert: accents are raw UTF-8, only the quote is escaped (the encoder is pinned, not the runtime default).
        var expected = Encoding.UTF8.GetBytes(Q("{'v':1,'type':'note','id':'11111111-2222-3333-4444-555555555555','patientId':'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee','format':'DAP','sections':[{'key':'data','position':0,'content':'Disse: ") + "\\\"não sei\\\" – ação" + Q("'}],'signedByCrp':'12345/06','signedAt':'2026-11-03T12:30:45.123000Z'}"));
        bytes.Should().Equal(expected);
    }

    [Fact]
    public void ForNote_UnassignedCodePointAndEmoji_AreWrittenRaw_IndependentOfTheRuntimeUnicodeTables()
    {
        // Arrange: U+0378 is unassigned (a table-driven encoder escapes it today and may stop when it gets assigned);
        // U+1F600 is an emoji (a surrogate pair in UTF-16); U+007F and U+2028 are outside the escaped set too.
        var content = "a\u0378b\U0001F600c\u007Fd\u2028e";
        var sections = new List<NoteSection> { Section("data", 0, content) };

        // Act
        var bytes = IntegrityCanonical.ForNote(SignedNote(), sections);

        // Assert: golden bytes, spelled out so they encode the rule and not a table.
        byte[] raw = [0x61, 0xCD, 0xB8, 0x62, 0xF0, 0x9F, 0x98, 0x80, 0x63, 0x7F, 0x64, 0xE2, 0x80, 0xA8, 0x65];
        var expected = Encoding.ASCII.GetBytes(Q("{'v':1,'type':'note','id':'11111111-2222-3333-4444-555555555555','patientId':'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee','format':'DAP','sections':[{'key':'data','position':0,'content':'"))
            .Concat(raw)
            .Concat(Encoding.ASCII.GetBytes(Q("'}],'signedByCrp':'12345/06','signedAt':'2026-11-03T12:30:45.123000Z'}")))
            .ToArray();
        bytes.Should().Equal(expected);
    }

    [Fact]
    public void ForNote_QuoteBackslashAndControlCharacters_AreTheOnlyEscapes()
    {
        // Arrange
        var sections = new List<NoteSection> { Section("data", 0, "q\"b\\n\nt\tz\u0000u\u001F/<>&'") };

        // Act
        var json = Json(IntegrityCanonical.ForNote(SignedNote(), sections));

        // Assert: \" and \\, every control as \u00xx in lowercase hex (no short forms), the rest raw (/ < > & ' too).
        json.Should().Contain("'content':'".Replace('\'', '"') + @"q\""b\\n\u000at\u0009z\u0000u\u001f/<>&'""}");
    }

    [Fact]
    public void ForDocument_FieldNames_FollowTheSameEscapingRule()
    {
        // Arrange
        var document = SignedDocument();
        document.Fields = new() { ["chave \"x\"\n\u0378"] = "v" };

        // Act
        var json = Json(IntegrityCanonical.ForDocument(document));

        // Assert
        json.Should().Contain("\"fields\":{\"chave \\\"x\\\"\\u000a\u0378\":\"v\"}");
    }

    [Fact]
    public void ForNote_Nulls_AreWritten()
    {
        // Arrange
        var note = SignedNote();
        note.SignedByCrp = null;
        note.SignedAt = null;

        // Act
        var json = Json(IntegrityCanonical.ForNote(note, []));

        // Assert
        json.Should().EndWith(Q(@"'sections':[],'signedByCrp':null,'signedAt':null}"));
    }

    [Fact]
    public void ForDocument_FixedKeyOrderFieldsSortedAndStoredOrderKept()
    {
        // Arrange / Act
        var json = Json(IntegrityCanonical.ForDocument(SignedDocument()));

        // Assert
        json.Should().Be(Q(
            @"{'v':1,'type':'document','id':'99999999-8888-7777-6666-555555555555','patientId':'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'," +
            @"'noteId':null,'kind':'atestado','body':'Corpo','fields':{'a':'1','b':'2'},'confirmations':['z','y']," +
            @"'team':[{'name':'Ana','category':'Psicóloga','registry':'CRP 06/1','analysis':'Análise'}],'rulesetId':null," +
            @"'signedAt':'2026-11-03T12:30:45.123000Z','signedByCrp':'12345/06','signedByName':'Dra. Teste','signedCity':'São Paulo'," +
            @"'signedPatientName':'Ana Silva'}"));
    }

    [Fact]
    public void ForDocument_FieldInsertionOrderAndNormalizationForm_DoNotMatter()
    {
        // Arrange
        var first = SignedDocument();
        first.Fields = new() { ["b"] = "Opção", ["a"] = "1" };
        var second = SignedDocument();
        second.Fields = new() { ["a"] = "1", ["b"] = "Opção".Normalize(NormalizationForm.FormD) };

        // Act
        var hashFirst = IntegrityCanonical.Hash(IntegrityCanonical.ForDocument(first));
        var hashSecond = IntegrityCanonical.Hash(IntegrityCanonical.ForDocument(second));

        // Assert
        hashSecond.Should().Be(hashFirst);
    }

    [Fact]
    public void ForDocument_ChangesThatLegitimatelyHappenAfterSigning_DoNotChangeTheHash()
    {
        // Arrange
        var original = IntegrityCanonical.Hash(IntegrityCanonical.ForDocument(SignedDocument()));
        var later = SignedDocument();
        later.SupersededBy = Guid.NewGuid();
        later.RetentionUntil = new DateOnly(2046, 1, 1);
        later.PdfObjectKey = "k";
        later.PdfSha256 = "h";
        later.ExportedAt = SignedAt;
        later.UpdatedAt = SignedAt;

        // Act
        var hash = IntegrityCanonical.Hash(IntegrityCanonical.ForDocument(later));

        // Assert
        hash.Should().Be(original);
    }

    [Fact]
    public void ForDocument_AChangeOfBodyOrTeam_ChangesTheHash()
    {
        // Arrange
        var original = IntegrityCanonical.Hash(IntegrityCanonical.ForDocument(SignedDocument()));
        var body = SignedDocument();
        body.Body = "Outro";
        var team = SignedDocument();
        team.Team = [new TeamMember("Ana", "Psicóloga", "CRP 06/1", "Outra análise")];

        // Act / Assert
        IntegrityCanonical.Hash(IntegrityCanonical.ForDocument(body)).Should().NotBe(original);
        IntegrityCanonical.Hash(IntegrityCanonical.ForDocument(team)).Should().NotBe(original);
    }
}
