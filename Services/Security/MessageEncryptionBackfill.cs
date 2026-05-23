using System.Data;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services.Security;

public static class MessageEncryptionBackfill
{
    private const int BatchSize = 500;

    public static async Task EncryptExistingMessagesAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        if (!MessageTextProtector.Enabled)
            return;

        var direct = await EncryptTableAsync(db, logger, "DirectMessages", ct);
        var group = await EncryptTableAsync(db, logger, "GroupMessages", ct);

        logger.LogInformation("Message encryption backfill finished. DirectMessages={DirectCount}, GroupMessages={GroupCount}", direct, group);
    }

    private static async Task<int> EncryptTableAsync(AppDbContext db, ILogger logger, string tableName, CancellationToken ct)
    {
        var total = 0;
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        while (true)
        {
            var rows = new List<(object Id, string Text)>();

            await using (var select = connection.CreateCommand())
            {
                select.CommandText = $"""
                    SELECT "Id", "Text"
                    FROM "{tableName}"
                    WHERE "Text" IS NOT NULL
                      AND "Text" <> ''
                      AND "Text" NOT LIKE 'jzenc1:%'
                    LIMIT {BatchSize}
                    """;

                await using var reader = await select.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var id = reader.GetValue(0);
                    var text = reader.GetString(1);
                    rows.Add((id, text));
                }
            }

            if (rows.Count == 0)
                break;

            foreach (var row in rows)
            {
                var encrypted = MessageTextProtector.ProtectForDatabase(row.Text);

                await using var update = connection.CreateCommand();
                update.CommandText = $"UPDATE \"{tableName}\" SET \"Text\" = @text WHERE \"Id\" = @id";

                var textParam = update.CreateParameter();
                textParam.ParameterName = "text";
                textParam.Value = encrypted;
                update.Parameters.Add(textParam);

                var idParam = update.CreateParameter();
                idParam.ParameterName = "id";
                idParam.Value = row.Id;
                update.Parameters.Add(idParam);

                await update.ExecuteNonQueryAsync(ct);
            }

            total += rows.Count;
            logger.LogInformation("Encrypted {Count} legacy message rows in {Table}. Total={Total}", rows.Count, tableName, total);
        }

        return total;
    }
}
