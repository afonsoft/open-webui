using Microsoft.EntityFrameworkCore;

namespace OpenWebUI.Server.Data;

/// <summary>
/// Garante a estrutura do banco SQLite: EnsureCreated cria o schema inicial e os
/// ajustes incrementais abaixo adicionam colunas/tabelas novas em bases existentes.
/// </summary>
public static class SchemaBootstrap
{
    /// <summary>Aplica o schema completo (cria e evolui) no contexto informado.</summary>
    public static void EnsureSchema(AppDbContext db)
    {
        db.Database.EnsureCreated();

        var connection = db.Database.GetDbConnection();
        connection.Open();
        try
        {
            using var command = connection.CreateCommand();

            AddColumnIfMissing(command, "Users", "Timezone", "TEXT NULL");
            AddColumnIfMissing(command, "Users", "SettingsJson", "TEXT NOT NULL DEFAULT '{}'");
            AddColumnIfMissing(command, "Chats", "TagsJson", "TEXT NOT NULL DEFAULT '[]'");
            AddColumnIfMissing(command, "Chats", "Pinned", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(command, "Chats", "FolderId", "TEXT NULL");
            AddColumnIfMissing(command, "Chats", "ShareId", "TEXT NULL");

            RebuildChatMessagesIfNeeded(command);

            ExecuteIfTableMissing(command, "ApiKeys", """
                CREATE TABLE "ApiKeys" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_ApiKeys" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "KeyHash" TEXT NOT NULL,
                    "CreatedAt" INTEGER NOT NULL,
                    "UpdatedAt" INTEGER NOT NULL
                )
                """);
            ExecuteIfTableMissing(command, "Folders", """
                CREATE TABLE "Folders" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Folders" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "Name" TEXT NOT NULL,
                    "ParentId" TEXT NULL,
                    "CreatedAt" INTEGER NOT NULL,
                    "UpdatedAt" INTEGER NOT NULL
                )
                """);
            ExecuteIfTableMissing(command, "Prompts", """
                CREATE TABLE "Prompts" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Prompts" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "Command" TEXT NOT NULL,
                    "Title" TEXT NOT NULL,
                    "Content" TEXT NOT NULL,
                    "CreatedAt" INTEGER NOT NULL,
                    "UpdatedAt" INTEGER NOT NULL
                )
                """);
            ExecuteIfTableMissing(command, "Files", """
                CREATE TABLE "Files" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Files" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "Filename" TEXT NOT NULL,
                    "ContentType" TEXT NULL,
                    "StoragePath" TEXT NOT NULL,
                    "Size" INTEGER NOT NULL,
                    "ExtractedText" TEXT NULL,
                    "CreatedAt" INTEGER NOT NULL,
                    "UpdatedAt" INTEGER NOT NULL
                )
                """);
            ExecuteIfTableMissing(command, "ModelEntries", """
                CREATE TABLE "ModelEntries" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_ModelEntries" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "Name" TEXT NOT NULL,
                    "BaseModelId" TEXT NOT NULL,
                    "SystemPrompt" TEXT NULL,
                    "ParamsJson" TEXT NULL,
                    "ProfileImageUrl" TEXT NULL,
                    "SuggestionPromptsJson" TEXT NULL,
                    "IsActive" INTEGER NOT NULL,
                    "CreatedAt" INTEGER NOT NULL,
                    "UpdatedAt" INTEGER NOT NULL
                )
                """);
            ExecuteIfTableMissing(command, "Memories", """
                CREATE TABLE "Memories" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Memories" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "Content" TEXT NOT NULL,
                    "CreatedAt" INTEGER NOT NULL,
                    "UpdatedAt" INTEGER NOT NULL
                )
                """);
            ExecuteIfTableMissing(command, "Notes", """
                CREATE TABLE "Notes" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Notes" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "Title" TEXT NOT NULL,
                    "Content" TEXT NOT NULL,
                    "CreatedAt" INTEGER NOT NULL,
                    "UpdatedAt" INTEGER NOT NULL
                )
                """);
            ExecuteIfTableMissing(command, "Feedbacks", """
                CREATE TABLE "Feedbacks" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Feedbacks" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "ChatId" TEXT NOT NULL,
                    "MessageId" TEXT NOT NULL,
                    "ModelId" TEXT NULL,
                    "Rating" INTEGER NOT NULL,
                    "Reason" TEXT NULL,
                    "CreatedAt" INTEGER NOT NULL,
                    "UpdatedAt" INTEGER NOT NULL
                )
                """);
        }
        finally
        {
            connection.Close();
        }
    }

    /// <summary>
    /// Migra ChatMessages para a chave composta (ChatId, Id) quando a tabela
    /// ainda usa PK simples em Id (bases criadas antes da expansão).
    /// </summary>
    private static void RebuildChatMessagesIfNeeded(System.Data.Common.DbCommand command)
    {
        var chatIdPk = 0;
        var idPk = 0;
        command.CommandText = "SELECT name, pk FROM pragma_table_info('ChatMessages')";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var name = reader.GetString(0);
                var pk = reader.GetInt32(1);
                if (name == "ChatId")
                {
                    chatIdPk = pk;
                }
                else if (name == "Id")
                {
                    idPk = pk;
                }
            }
        }

        // Já migrado: PK composta começando por ChatId.
        if (chatIdPk == 1 && idPk == 2)
        {
            return;
        }

        command.CommandText = """
            BEGIN;
            CREATE TABLE "ChatMessages_new" (
                "ChatId" TEXT NOT NULL,
                "Id" TEXT NOT NULL,
                "Content" TEXT NOT NULL,
                "Model" TEXT NULL,
                "Position" INTEGER NOT NULL,
                "Role" TEXT NOT NULL,
                "Timestamp" INTEGER NOT NULL,
                CONSTRAINT "PK_ChatMessages" PRIMARY KEY ("ChatId", "Id"),
                CONSTRAINT "FK_ChatMessages_Chats_ChatId" FOREIGN KEY ("ChatId") REFERENCES "Chats" ("Id") ON DELETE CASCADE
            );
            INSERT OR IGNORE INTO "ChatMessages_new"
                SELECT "ChatId", "Id", "Content", "Model", "Position", "Role", "Timestamp" FROM "ChatMessages";
            DROP TABLE "ChatMessages";
            ALTER TABLE "ChatMessages_new" RENAME TO "ChatMessages";
            CREATE INDEX "IX_ChatMessages_ChatId_Position" ON "ChatMessages" ("ChatId", "Position");
            COMMIT;
            """;
        try
        {
            command.ExecuteNonQuery();
        }
        catch
        {
            command.CommandText = "ROLLBACK";
            try
            {
                command.ExecuteNonQuery();
            }
            catch
            {
            }

            throw;
        }
    }

    private static void AddColumnIfMissing(
        System.Data.Common.DbCommand command, string table, string column, string definition)
    {
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        var exists = Convert.ToInt32(command.ExecuteScalar()) > 0;
        if (exists)
        {
            return;
        }

        command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
        command.ExecuteNonQuery();
    }

    private static void ExecuteIfTableMissing(
        System.Data.Common.DbCommand command, string table, string createSql)
    {
        command.CommandText =
            $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}'";
        var exists = Convert.ToInt32(command.ExecuteScalar()) > 0;
        if (exists)
        {
            return;
        }

        command.CommandText = createSql;
        command.ExecuteNonQuery();
    }
}
