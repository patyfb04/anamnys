using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Anamnys.Server.Data.Entities;

namespace Anamnys.Server.Integrity;

// The one routine that turns a signed record into the bytes that are sealed and verified (design/specs/2026-11-03-
// integrity-chain-design.md §3). A JSON object written by hand (CanonicalJsonWriter below, with its frozen escaping
// rule): fixed key order, no whitespace, every string in Unicode NFC, timestamps in UTC with microseconds (PostgreSQL
// precision), ids lowercase and hyphenated, nulls written as null. "v" comes first so the format can evolve. Never
// changed for version 1: a seal made today must verify years later.
public static class IntegrityCanonical
{
    public const int Version = 1;
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    public static byte[] ForNote(Note note, IReadOnlyList<NoteSection> sections)
    {
        return Write(w =>
        {
            w.WriteNumber("v", Version);
            Text(w, "type", IntegritySeal.Note);
            Id(w, "id", note.Id);
            Id(w, "patientId", note.PatientId);
            Text(w, "format", note.Format);
            w.WriteStartArray("sections");
            foreach (var s in sections.OrderBy(s => s.Position).ThenBy(s => Normalize(s.Key), StringComparer.Ordinal))
            {
                w.WriteStartObject();
                Text(w, "key", s.Key);
                w.WriteNumber("position", s.Position);
                Text(w, "content", s.Content);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            Text(w, "signedByCrp", note.SignedByCrp);
            Time(w, "signedAt", note.SignedAt);
        });
    }

    public static byte[] ForDocument(ClinicalDocument document)
    {
        return Write(w =>
        {
            w.WriteNumber("v", Version);
            Text(w, "type", IntegritySeal.Document);
            Id(w, "id", document.Id);
            Id(w, "patientId", document.PatientId);
            Id(w, "noteId", document.NoteId);
            Text(w, "kind", document.Kind);
            Text(w, "body", document.Body);
            w.WriteStartObject("fields");
            foreach (var (key, value) in document.Fields
                .Select(kv => (Key: Normalize(kv.Key)!, kv.Value))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                Text(w, key, value);
            }
            w.WriteEndObject();
            w.WriteStartArray("confirmations");
            foreach (var c in document.Confirmations)
            {
                w.WriteStringValue(Normalize(c));
            }
            w.WriteEndArray();
            w.WriteStartArray("team");
            foreach (var m in document.Team)
            {
                w.WriteStartObject();
                Text(w, "name", m.Name);
                Text(w, "category", m.Category);
                Text(w, "registry", m.Registry);
                Text(w, "analysis", m.Analysis);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            Id(w, "rulesetId", document.RulesetId);
            Time(w, "signedAt", document.SignedAt);
            Text(w, "signedByCrp", document.SignedByCrp);
            Text(w, "signedByName", document.SignedByName);
            Text(w, "signedCity", document.SignedCity);
            Text(w, "signedPatientName", document.SignedPatientName);
        });
    }

    // Lowercase hex SHA-256.
    public static string Hash(byte[] canonical) => Convert.ToHexStringLower(SHA256.HashData(canonical));

    // The shared format of every timestamp that enters a hash: UTC, microseconds.
    public static string FormatTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static byte[] Write(Action<CanonicalJsonWriter> body)
    {
        var writer = new CanonicalJsonWriter();
        writer.WriteStartObject();
        body(writer);
        writer.WriteEndObject();
        return writer.ToUtf8();
    }

    private static string? Normalize(string? value) => value?.Normalize(NormalizationForm.FormC);

    private static void Text(CanonicalJsonWriter w, string name, string? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
            return;
        }
        w.WriteString(name, Normalize(value));
    }

    private static void Id(CanonicalJsonWriter w, string name, Guid? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
            return;
        }
        w.WriteString(name, value.Value.ToString("D", CultureInfo.InvariantCulture));
    }

    private static void Time(CanonicalJsonWriter w, string name, DateTimeOffset? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
            return;
        }
        w.WriteString(name, FormatTime(value.Value));
    }

    // The JSON is written by hand, so the bytes depend on no runtime table (an encoder such as
    // JavaScriptEncoder.UnsafeRelaxedJsonEscaping escapes code points that are unassigned in the Unicode version compiled
    // into that .NET release, so its output can change with a runtime upgrade). The escaping rule of version 1, for
    // property names and string values alike:
    //   '"' -> \"   '\' -> \\   U+0000..U+001F -> \u00xx (four hex digits, lowercase; never the short forms \n, \t...)
    // Every other character, including U+007F, U+2028/U+2029, unassigned code points and emoji, is written raw as UTF-8.
    // A lone surrogate cannot be encoded and throws. Never changed for version 1.
    private sealed class CanonicalJsonWriter
    {
        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        private readonly StringBuilder _text = new();
        // One entry per open object or array: true while nothing has been written into it yet.
        private readonly Stack<bool> _empty = new();

        public void WriteStartObject() => Open('{', null);
        public void WriteStartObject(string name) => Open('{', name);
        public void WriteEndObject() => Close('}');
        public void WriteStartArray(string name) => Open('[', name);
        public void WriteEndArray() => Close(']');

        public void WriteNumber(string name, long value)
        {
            Name(name);
            _text.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        public void WriteString(string name, string value)
        {
            Name(name);
            Escaped(value);
        }

        public void WriteNull(string name)
        {
            Name(name);
            _text.Append("null");
        }

        public void WriteStringValue(string? value)
        {
            Separator();
            if (value is null)
            {
                _text.Append("null");
                return;
            }
            Escaped(value);
        }

        public byte[] ToUtf8()
        {
            if (_empty.Count != 0)
            {
                throw new InvalidOperationException("Canonical JSON left a container open.");
            }
            return StrictUtf8.GetBytes(_text.ToString());
        }

        private void Open(char bracket, string? name)
        {
            if (name is null)
            {
                Separator();
            }
            else
            {
                Name(name);
            }
            _text.Append(bracket);
            _empty.Push(true);
        }

        private void Close(char bracket)
        {
            _empty.Pop();
            _text.Append(bracket);
        }

        private void Name(string name)
        {
            Separator();
            Escaped(name);
            _text.Append(':');
        }

        private void Separator()
        {
            if (_empty.Count == 0)
            {
                return;
            }
            if (!_empty.Pop())
            {
                _text.Append(',');
            }
            _empty.Push(false);
        }

        private void Escaped(string value)
        {
            _text.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"':
                        _text.Append("\\\"");
                        break;
                    case '\\':
                        _text.Append("\\\\");
                        break;
                    case < ' ':
                        _text.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                        break;
                    default:
                        _text.Append(c);
                        break;
                }
            }
            _text.Append('"');
        }
    }
}
