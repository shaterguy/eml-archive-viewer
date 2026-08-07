using System.Globalization;
using Microsoft.Data.Sqlite;
using EmlArchiveViewer.Models;

namespace EmlArchiveViewer.Services;

public sealed class DatabaseService
{
    private const int CurrentSchemaVersion = 2;
    private const string SearchableTextExpression = """
        m.subject || char(31) || m.sender || char(31) || m.recipients || char(31) ||
        m.cc || char(31) || m.text_body || char(31) || m.attachment_names || char(31) ||
        m.relative_folder_path
        """;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = AppPaths.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true
    }.ToString();

    public async Task InitializeAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            AppPaths.EnsureCreated();
            await using var connection = await OpenAsync();

            var schema = connection.CreateCommand();
            schema.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA foreign_keys=ON;

                CREATE TABLE IF NOT EXISTS mails (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    root_path TEXT NOT NULL COLLATE NOCASE,
                    relative_folder_path TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                    file_name TEXT NOT NULL,
                    file_size INTEGER NOT NULL,
                    modified_utc TEXT NOT NULL,
                    message_id TEXT NOT NULL DEFAULT '',
                    sent_utc TEXT NULL,
                    subject TEXT NOT NULL DEFAULT '',
                    sender TEXT NOT NULL DEFAULT '',
                    recipients TEXT NOT NULL DEFAULT '',
                    cc TEXT NOT NULL DEFAULT '',
                    bcc TEXT NOT NULL DEFAULT '',
                    text_body TEXT NOT NULL DEFAULT '',
                    html_body TEXT NOT NULL DEFAULT '',
                    attachment_count INTEGER NOT NULL DEFAULT 0,
                    attachment_names TEXT NOT NULL DEFAULT '',
                    parse_error TEXT NOT NULL DEFAULT ''
                );

                CREATE INDEX IF NOT EXISTS ix_mails_root_folder ON mails(root_path, relative_folder_path);
                CREATE INDEX IF NOT EXISTS ix_mails_sent_utc ON mails(sent_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_mails_modified ON mails(modified_utc);
                """;
            await schema.ExecuteNonQueryAsync();

            var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync());

            if (version < CurrentSchemaVersion)
            {
                await using var transaction = await connection.BeginTransactionAsync();
                var migration = connection.CreateCommand();
                migration.Transaction = (SqliteTransaction)transaction;
                migration.CommandText = $"""
                    DROP TABLE IF EXISTS mail_fts;
                    CREATE VIRTUAL TABLE mail_fts USING fts5(
                        subject, sender, recipients, cc, text_body, attachment_names, relative_folder_path,
                        tokenize='trigram'
                    );
                    INSERT INTO mail_fts(rowid, subject, sender, recipients, cc, text_body, attachment_names, relative_folder_path)
                    SELECT id, subject, sender, recipients, cc, text_body, attachment_names, relative_folder_path
                    FROM mails;
                    PRAGMA user_version={CurrentSchemaVersion};
                    """;
                await migration.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            else
            {
                var fts = connection.CreateCommand();
                fts.CommandText = """
                    CREATE VIRTUAL TABLE IF NOT EXISTS mail_fts USING fts5(
                        subject, sender, recipients, cc, text_body, attachment_names, relative_folder_path,
                        tokenize='trigram'
                    );
                    """;
                await fts.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpsertAsync(MailRecord mail, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var upsert = connection.CreateCommand();
            upsert.Transaction = (SqliteTransaction)transaction;
            upsert.CommandText = """
                INSERT INTO mails (
                    file_path, root_path, relative_folder_path, file_name, file_size, modified_utc,
                    message_id, sent_utc, subject, sender, recipients, cc, bcc, text_body, html_body,
                    attachment_count, attachment_names, parse_error)
                VALUES (
                    $filePath, $rootPath, $relativeFolderPath, $fileName, $fileSize, $modifiedUtc,
                    $messageId, $sentUtc, $subject, $sender, $recipients, $cc, $bcc, $textBody, $htmlBody,
                    $attachmentCount, $attachmentNames, $parseError)
                ON CONFLICT(file_path) DO UPDATE SET
                    root_path=excluded.root_path,
                    relative_folder_path=excluded.relative_folder_path,
                    file_name=excluded.file_name,
                    file_size=excluded.file_size,
                    modified_utc=excluded.modified_utc,
                    message_id=excluded.message_id,
                    sent_utc=excluded.sent_utc,
                    subject=excluded.subject,
                    sender=excluded.sender,
                    recipients=excluded.recipients,
                    cc=excluded.cc,
                    bcc=excluded.bcc,
                    text_body=excluded.text_body,
                    html_body=excluded.html_body,
                    attachment_count=excluded.attachment_count,
                    attachment_names=excluded.attachment_names,
                    parse_error=excluded.parse_error;
                """;
            AddMailParameters(upsert, mail);
            await upsert.ExecuteNonQueryAsync(cancellationToken);

            var fts = connection.CreateCommand();
            fts.Transaction = (SqliteTransaction)transaction;
            fts.CommandText = """
                DELETE FROM mail_fts WHERE rowid = (SELECT id FROM mails WHERE file_path=$filePath);
                INSERT INTO mail_fts(rowid, subject, sender, recipients, cc, text_body, attachment_names, relative_folder_path)
                SELECT id, subject, sender, recipients, cc, text_body, attachment_names, relative_folder_path
                FROM mails WHERE file_path=$filePath;
                """;
            fts.Parameters.AddWithValue("$filePath", mail.FilePath);
            await fts.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpsertErrorAsync(string rootPath, string filePath, Exception exception,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(filePath);
        var relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(rootPath, filePath)) ?? string.Empty;
        await UpsertAsync(new MailRecord
        {
            FilePath = Path.GetFullPath(filePath),
            RootPath = Path.GetFullPath(rootPath),
            RelativeFolderPath = relativeDirectory == "." ? string.Empty : relativeDirectory,
            FileName = info.Name,
            FileSize = info.Exists ? info.Length : 0,
            ModifiedUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow,
            Subject = "[파싱 오류] " + info.Name,
            ParseError = exception.ToString()
        }, cancellationToken);
    }

    public async Task DeleteByPathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                DELETE FROM mail_fts WHERE rowid = (SELECT id FROM mails WHERE file_path=$path);
                DELETE FROM mails WHERE file_path=$path;
                """;
            command.Parameters.AddWithValue("$path", Path.GetFullPath(filePath));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteRootAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                DELETE FROM mail_fts WHERE rowid IN (SELECT id FROM mails WHERE root_path=$rootPath);
                DELETE FROM mails WHERE root_path=$rootPath;
                """;
            command.Parameters.AddWithValue("$rootPath", Path.GetFullPath(rootPath));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteMissingUnderRootAsync(string rootPath, HashSet<string> existingPaths,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT file_path FROM mails WHERE root_path=$rootPath;";
        command.Parameters.AddWithValue("$rootPath", Path.GetFullPath(rootPath));
        var missing = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var path = reader.GetString(0);
                if (!existingPaths.Contains(path))
                {
                    missing.Add(path);
                }
            }
        }

        foreach (var path in missing)
        {
            await DeleteByPathAsync(path, cancellationToken);
        }
    }

    public async Task<bool> NeedsIndexingAsync(string filePath, long size, DateTime modifiedUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT file_size, modified_utc FROM mails WHERE file_path=$path;";
        command.Parameters.AddWithValue("$path", Path.GetFullPath(filePath));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return true;
        }

        var indexedSize = reader.GetInt64(0);
        var indexedModified = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        return indexedSize != size || indexedModified != modifiedUtc;
    }

    public async Task<List<MailRecord>> SearchAsync(SearchCriteria criteria, int limit = 2000,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        var where = new List<string>();

        AddQuickTextConditions(where, command, criteria.QuickText);
        AddLike(where, command, "m.subject", "$subject", criteria.Subject);
        AddLike(where, command, "m.text_body", "$body", criteria.Body);
        AddLike(where, command, "m.sender", "$sender", criteria.Sender);
        AddLike(where, command, "m.recipients", "$recipients", criteria.Recipients);
        AddLike(where, command, "m.cc", "$cc", criteria.Cc);
        AddLike(where, command, "m.attachment_names", "$attachmentName", criteria.AttachmentName);

        if (criteria.DateFrom is { } dateFrom)
        {
            where.Add("m.sent_utc >= $dateFrom");
            command.Parameters.AddWithValue("$dateFrom", new DateTimeOffset(dateFrom.Date).UtcDateTime.ToString("O"));
        }

        if (criteria.DateTo is { } dateTo)
        {
            where.Add("m.sent_utc < $dateTo");
            command.Parameters.AddWithValue("$dateTo", new DateTimeOffset(dateTo.Date.AddDays(1)).UtcDateTime.ToString("O"));
        }

        if (criteria.HasAttachment is true)
        {
            where.Add("m.attachment_count > 0");
        }
        else if (criteria.HasAttachment is false)
        {
            where.Add("m.attachment_count = 0");
        }

        if (criteria.Scope == SearchScope.SelectedFolderAndDescendants &&
            !string.IsNullOrWhiteSpace(criteria.SelectedRootPath))
        {
            where.Add("m.root_path = $scopeRoot");
            command.Parameters.AddWithValue("$scopeRoot", Path.GetFullPath(criteria.SelectedRootPath));
            var relative = criteria.SelectedRelativeFolderPath?
                .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;
            if (!string.IsNullOrEmpty(relative))
            {
                where.Add("(m.relative_folder_path = $scopeFolder OR m.relative_folder_path LIKE $scopeDescendants)");
                command.Parameters.AddWithValue("$scopeFolder", relative);
                command.Parameters.AddWithValue("$scopeDescendants", relative + Path.DirectorySeparatorChar + "%");
            }
        }

        command.CommandText = $"""
            SELECT m.id, m.file_path, m.root_path, m.relative_folder_path, m.file_name, m.file_size,
                   m.modified_utc, m.message_id, m.sent_utc, m.subject, m.sender, m.recipients,
                   m.cc, m.bcc, m.text_body, m.html_body, m.attachment_count, m.attachment_names, m.parse_error
            FROM mails m
            {(where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where))}
            ORDER BY COALESCE(m.sent_utc, m.modified_utc) DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<MailRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadMail(reader));
        }
        return result;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        connection.CreateFunction<string, string, bool>(
            "contains_compact",
            static (source, query) => ContainsIgnoringWhitespace(source, query),
            isDeterministic: true);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static void AddQuickTextConditions(List<string> where, SqliteCommand command, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var terms = value.Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var index = 0; index < terms.Length; index++)
        {
            var parameterName = $"$quickTerm{index}";
            where.Add($"contains_compact({SearchableTextExpression}, {parameterName}) = 1");
            command.Parameters.AddWithValue(parameterName, terms[index]);
        }
    }

    private static bool ContainsIgnoringWhitespace(string source, string query)
    {
        var compactQuery = RemoveWhitespace(query);
        return compactQuery.Length > 0 &&
               RemoveWhitespace(source).Contains(compactQuery, StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveWhitespace(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    private static void AddMailParameters(SqliteCommand command, MailRecord mail)
    {
        command.Parameters.AddWithValue("$filePath", mail.FilePath);
        command.Parameters.AddWithValue("$rootPath", mail.RootPath);
        command.Parameters.AddWithValue("$relativeFolderPath", mail.RelativeFolderPath);
        command.Parameters.AddWithValue("$fileName", mail.FileName);
        command.Parameters.AddWithValue("$fileSize", mail.FileSize);
        command.Parameters.AddWithValue("$modifiedUtc", mail.ModifiedUtc.ToString("O"));
        command.Parameters.AddWithValue("$messageId", mail.MessageId);
        command.Parameters.AddWithValue("$sentUtc", mail.SentDate?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$subject", mail.Subject);
        command.Parameters.AddWithValue("$sender", mail.Sender);
        command.Parameters.AddWithValue("$recipients", mail.Recipients);
        command.Parameters.AddWithValue("$cc", mail.Cc);
        command.Parameters.AddWithValue("$bcc", mail.Bcc);
        command.Parameters.AddWithValue("$textBody", mail.TextBody);
        command.Parameters.AddWithValue("$htmlBody", mail.HtmlBody);
        command.Parameters.AddWithValue("$attachmentCount", mail.AttachmentCount);
        command.Parameters.AddWithValue("$attachmentNames", mail.AttachmentNames);
        command.Parameters.AddWithValue("$parseError", mail.ParseError);
    }

    private static void AddLike(List<string> where, SqliteCommand command, string column,
        string parameterName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        where.Add($"{column} LIKE {parameterName}");
        command.Parameters.AddWithValue(parameterName, $"%{value.Trim()}%");
    }

    private static MailRecord ReadMail(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        FilePath = reader.GetString(1),
        RootPath = reader.GetString(2),
        RelativeFolderPath = reader.GetString(3),
        FileName = reader.GetString(4),
        FileSize = reader.GetInt64(5),
        ModifiedUtc = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind),
        MessageId = reader.GetString(7),
        SentDate = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Subject = reader.GetString(9),
        Sender = reader.GetString(10),
        Recipients = reader.GetString(11),
        Cc = reader.GetString(12),
        Bcc = reader.GetString(13),
        TextBody = reader.GetString(14),
        HtmlBody = reader.GetString(15),
        AttachmentCount = reader.GetInt32(16),
        AttachmentNames = reader.GetString(17),
        ParseError = reader.GetString(18)
    };
}
