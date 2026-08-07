using Microsoft.Data.Sqlite;

namespace EmlArchiveViewer.Services;

public static class DatabaseRecoveryService
{
    private const int CurrentSchemaVersion = 2;

    public static async Task InitializeAsync(DatabaseService database, CancellationToken cancellationToken = default)
    {
        try
        {
            await database.InitializeAsync();
            await RepairSearchIndexAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or InvalidOperationException)
        {
            CrashLogService.Write("데이터베이스 초기화 실패. 기존 색인을 격리하고 재생성합니다.", exception);
            QuarantineDatabaseFiles();
            await database.InitializeAsync();
            await RepairSearchIndexAsync(cancellationToken);
        }
    }

    private static async Task RepairSearchIndexAsync(CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var integrityCommand = connection.CreateCommand();
        integrityCommand.CommandText = "PRAGMA quick_check;";
        var integrityResult = Convert.ToString(await integrityCommand.ExecuteScalarAsync(cancellationToken));
        if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteException($"SQLite quick_check 실패: {integrityResult}", 11);
        }

        var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken));
        if (version >= CurrentSchemaVersion)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
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
        await migration.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static void QuarantineDatabaseFiles()
    {
        AppPaths.EnsureCreated();
        var recoveryDirectory = Path.Combine(AppPaths.BaseDirectory, "recovery",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(recoveryDirectory);

        foreach (var sourcePath in new[]
                 {
                     AppPaths.DatabasePath,
                     AppPaths.DatabasePath + "-wal",
                     AppPaths.DatabasePath + "-shm"
                 })
        {
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            try
            {
                var destinationPath = Path.Combine(recoveryDirectory, Path.GetFileName(sourcePath));
                File.Move(sourcePath, destinationPath, true);
            }
            catch (Exception exception)
            {
                CrashLogService.Write($"손상 색인 격리 실패: {sourcePath}", exception);
                try
                {
                    File.Delete(sourcePath);
                }
                catch (Exception deleteException)
                {
                    CrashLogService.Write($"손상 색인 삭제 실패: {sourcePath}", deleteException);
                }
            }
        }
    }
}

public static class CrashLogService
{
    private static readonly object Sync = new();
    public static string LogDirectory => Path.Combine(AppPaths.BaseDirectory, "logs");
    public static string LogPath => Path.Combine(LogDirectory, "app.log");

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy.MM.dd HH:mm:ss.fff}] {message}{Environment.NewLine}" +
                    (exception is null ? string.Empty : exception + Environment.NewLine) +
                    Environment.NewLine);
            }
        }
        catch
        {
            // 로깅 실패가 프로그램 종료로 이어지지 않게 한다.
        }
    }
}
