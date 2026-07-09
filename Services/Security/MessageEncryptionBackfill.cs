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

    /// <summary>
    /// E10: old rows were created before the server started storing E2EE envelope metadata.
    /// This pass does not decrypt client E2EE. It only classifies the envelope by prefix/json header,
    /// so v1/v2/v3 messages can be migrated safely and shown with correct compatibility state.
    /// </summary>
    public static async Task BackfillE2eeEnvelopeMetadataAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var direct = await BackfillEnvelopeMetadataTableAsync(db, logger, "DirectMessages", direct: true, ct);
        var group = await BackfillEnvelopeMetadataTableAsync(db, logger, "GroupMessages", direct: false, ct);

        if (direct > 0 || group > 0)
            logger.LogInformation("E2EE envelope metadata backfill finished. DirectMessages={DirectCount}, GroupMessages={GroupCount}", direct, group);
    }

    private static async Task<int> BackfillEnvelopeMetadataTableAsync(AppDbContext db, ILogger logger, string tableName, bool direct, CancellationToken ct)
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
                      AND "E2eeProtocol" IS NULL
                    LIMIT {BatchSize}
                    """;

                await using var reader = await select.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var id = reader.GetValue(0);
                    var text = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    rows.Add((id, text));
                }
            }

            if (rows.Count == 0)
                break;

            var changedInBatch = 0;
            foreach (var row in rows)
            {
                var visibleText = TryUnprotectStorageText(row.Text);
                var info = direct ? E2eeEnvelopeInspector.InspectDirect(visibleText) : E2eeEnvelopeInspector.InspectGroup(visibleText);
                var protocol = info.Version <= 0
                    ? (MessageTextProtector.IsProtected(row.Text) ? "server-storage-protected-or-legacy" : "legacy-plaintext")
                    : info.Protocol;

                await using var update = connection.CreateCommand();
                update.CommandText = $"""
                    UPDATE "{tableName}"
                    SET "E2eeEnvelopeVersion" = @version,
                        "E2eeProtocol" = @protocol
                    WHERE "Id" = @id
                    """;

                var versionParam = update.CreateParameter();
                versionParam.ParameterName = "version";
                versionParam.Value = Math.Max(0, info.Version);
                update.Parameters.Add(versionParam);

                var protocolParam = update.CreateParameter();
                protocolParam.ParameterName = "protocol";
                protocolParam.Value = string.IsNullOrWhiteSpace(protocol) ? DBNull.Value : protocol;
                update.Parameters.Add(protocolParam);

                var idParam = update.CreateParameter();
                idParam.ParameterName = "id";
                idParam.Value = row.Id;
                update.Parameters.Add(idParam);

                await update.ExecuteNonQueryAsync(ct);
                changedInBatch++;
            }

            total += changedInBatch;
            if (changedInBatch == 0)
                break;

            logger.LogInformation("Backfilled E2EE envelope metadata for {Count} rows in {Table}. Total={Total}", changedInBatch, tableName, total);
        }

        return total;
    }

    private static string TryUnprotectStorageText(string text)
    {
        if (!MessageTextProtector.IsProtected(text))
            return text;

        try
        {
            return MessageTextProtector.UnprotectFromDatabase(text);
        }
        catch
        {
            return text;
        }
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
