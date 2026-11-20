using Anamnys.Tests.Retention;
using Npgsql;

namespace Anamnys.Tests.Pseudonymization;

internal static class PseudonymizationScratch
{
    // A note waiting for the step: Processing, no transcript, structuring pending; DAP so structuring can run after.
    public static async Task<Guid> ProcessingNoteAsync(RetentionScratch s, Guid provider, Guid patient, DateTimeOffset at, CancellationToken ct,
        string inputMode = "Text", Guid? recordingId = null)
    {
        var note = await s.AddNoteAsync(provider, patient, at, ct, status: "Processing");
        await s.ExecuteAsync(
            """UPDATE "Notes" SET "StructureStatus" = 'pending', "Format" = 'DAP', "InputMode" = @mode, "RecordingId" = @rec WHERE "Id" = @n""",
            ct, ("n", note), ("mode", inputMode), ("rec", (object?)recordingId ?? DBNull.Value));
        return note;
    }

    // Whether any text, json, array or bytea column of any table holds the string (UTF-8 for bytea).
    public static async Task<bool> NameAppearsAnywhereAsync(RetentionScratch s, string name)
    {
        var columns = new List<(string Table, string Column, string Type)>();
        await using (var connection = await s.OpenAsync(CancellationToken.None))
        {
            await using var list = new NpgsqlCommand(
                """
                SELECT table_name, column_name, data_type FROM information_schema.columns
                 WHERE table_schema = 'public' AND data_type IN ('text', 'character varying', 'jsonb', 'json', 'ARRAY', 'bytea')
                   AND table_name IN (SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE')
                """, connection);
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }
        foreach (var (table, column, type) in columns)
        {
            var condition = type == "bytea"
                ? $"position(convert_to(@n, 'UTF8') in \"{column}\") > 0"
                : $"\"{column}\"::text ILIKE '%' || @n || '%'";
            if (await s.CountAsync($"SELECT count(*) FROM \"{table}\" WHERE {condition}", CancellationToken.None, ("n", name)) > 0)
            {
                return true;
            }
        }
        return false;
    }
}
