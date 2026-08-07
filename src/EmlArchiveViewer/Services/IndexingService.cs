using System.Collections.Concurrent;
using EmlArchiveViewer.Models;

namespace EmlArchiveViewer.Services;

public sealed class IndexingService : IDisposable
{
    private readonly DatabaseService _database;
    private readonly EmlParserService _parser;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private PeriodicTimer? _reconcileTimer;
    private Task? _reconcileTask;
    private bool _disposed;

    public event EventHandler? IndexChanged;
    public event EventHandler<string>? StatusChanged;

    public IndexingService(DatabaseService database, EmlParserService parser,
        SettingsService settingsService, AppSettings settings)
    {
        _database = database;
        _parser = parser;
        _settingsService = settingsService;
        _settings = settings;
    }

    public Task StartAsync()
    {
        RebuildWatchers();
        _reconcileTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        _reconcileTask = Task.Run(ReconcileLoopAsync);
        QueueBackground("초기 색인", ReconcileAllAsync);
        return Task.CompletedTask;
    }

    public async Task AddRootAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(path);
        }

        if (_settings.RootFolders.Any(existing =>
                string.Equals(Path.GetFullPath(existing), path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _settings.RootFolders.Add(path);
        await _settingsService.SaveAsync(_settings);
        RebuildWatchers();
        StatusChanged?.Invoke(this, $"폴더 등록됨. 백그라운드 색인 시작: {path}");
        QueueBackground($"폴더 색인: {path}", () => ReconcileRootWithLockAsync(path, _shutdown.Token));
    }

    public async Task RemoveRootAsync(string path)
    {
        _settings.RootFolders.RemoveAll(existing => string.Equals(Path.GetFullPath(existing),
            Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        await _settingsService.SaveAsync(_settings);
        await _database.DeleteRootAsync(path, _shutdown.Token);
        RebuildWatchers();
        IndexChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ReconcileAllAsync()
    {
        if (_disposed || _shutdown.IsCancellationRequested)
        {
            return;
        }

        if (!await _scanLock.WaitAsync(0, _shutdown.Token))
        {
            return;
        }

        try
        {
            foreach (var root in _settings.RootFolders.ToList())
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                try
                {
                    await ReconcileRootAsync(root, _shutdown.Token);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    CrashLogService.Write($"기준 폴더 색인 실패: {root}", exception);
                    StatusChanged?.Invoke(this, $"색인 실패: {root} - {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CrashLogService.Write("전체 색인 실패", exception);
            StatusChanged?.Invoke(this, $"전체 색인 실패: {exception.Message}");
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task ReconcileRootWithLockAsync(string rootPath, CancellationToken cancellationToken)
    {
        await _scanLock.WaitAsync(cancellationToken);
        try
        {
            await ReconcileRootAsync(rootPath, cancellationToken);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task ReconcileLoopAsync()
    {
        try
        {
            while (_reconcileTimer is not null &&
                   await _reconcileTimer.WaitForNextTickAsync(_shutdown.Token))
            {
                await ReconcileAllAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            CrashLogService.Write("주기적 보정 색인 실패", exception);
        }
    }

    private async Task ReconcileRootAsync(string rootPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootPath))
        {
            StatusChanged?.Invoke(this, $"접근할 수 없는 폴더: {rootPath}");
            return;
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexed = 0;
        StatusChanged?.Invoke(this, $"폴더 확인 중: {rootPath}");

        foreach (var filePath in EnumerateEmlFilesSafely(rootPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fullPath = Path.GetFullPath(filePath);
                existing.Add(fullPath);
                var info = new FileInfo(fullPath);
                if (await _database.NeedsIndexingAsync(fullPath, info.Length, info.LastWriteTimeUtc,
                        cancellationToken))
                {
                    await IndexStableFileAsync(rootPath, fullPath, cancellationToken);
                    indexed++;
                    if (indexed % 25 == 0)
                    {
                        StatusChanged?.Invoke(this, $"색인 중: {indexed:N0}개 반영");
                        IndexChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                CrashLogService.Write($"파일 색인 처리 실패: {filePath}", exception);
                StatusChanged?.Invoke(this, $"파일 처리 실패: {Path.GetFileName(filePath)}");
            }
        }

        await _database.DeleteMissingUnderRootAsync(rootPath, existing, cancellationToken);
        StatusChanged?.Invoke(this, $"색인 완료: {existing.Count:N0}개");
        IndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private static IEnumerable<string> EnumerateEmlFilesSafely(string rootPath,
        CancellationToken cancellationToken)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pendingDirectories.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current, "*.eml", SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                CrashLogService.Write($"폴더 파일 열거 실패: {current}", exception);
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current).ToList();
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                CrashLogService.Write($"하위 폴더 열거 실패: {current}", exception);
                continue;
            }

            foreach (var directory in directories)
            {
                pendingDirectories.Push(directory);
            }
        }
    }

    private void RebuildWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        foreach (var root in _settings.RootFolders.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(root, "*.eml")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = true
                };
                watcher.Created += (_, args) => QueueIndex(root, args.FullPath);
                watcher.Changed += (_, args) => QueueIndex(root, args.FullPath);
                watcher.Deleted += (_, args) => QueueBackground("삭제 반영",
                    () => HandleDeleteAsync(args.FullPath));
                watcher.Renamed += (_, args) =>
                {
                    QueueBackground("이동 전 경로 삭제 반영", () => HandleDeleteAsync(args.OldFullPath));
                    QueueIndex(root, args.FullPath);
                };
                watcher.Error += (_, args) =>
                {
                    CrashLogService.Write($"폴더 감시 오류: {root}", args.GetException());
                    QueueBackground("감시 오류 보정", ReconcileAllAsync);
                };
                _watchers.Add(watcher);
            }
            catch (Exception exception)
            {
                CrashLogService.Write($"폴더 감시 시작 실패: {root}", exception);
                StatusChanged?.Invoke(this, $"실시간 감시 실패: {root}");
            }
        }
    }

    private void QueueIndex(string rootPath, string filePath)
    {
        if (_disposed || !filePath.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var fullPath = Path.GetFullPath(filePath);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        if (_pending.TryGetValue(fullPath, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }
        _pending[fullPath] = cancellation;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(800), cancellation.Token);
                await IndexStableFileAsync(rootPath, fullPath, cancellation.Token);
                IndexChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                CrashLogService.Write($"실시간 파일 반영 실패: {fullPath}", exception);
                StatusChanged?.Invoke(this, $"실시간 반영 실패: {Path.GetFileName(fullPath)}");
            }
            finally
            {
                _pending.TryRemove(fullPath, out _);
                cancellation.Dispose();
            }
        }, cancellation.Token);
    }

    private async Task IndexStableFileAsync(string rootPath, string filePath,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(filePath))
            {
                return;
            }

            try
            {
                var before = new FileInfo(filePath);
                var beforeSize = before.Length;
                var beforeWrite = before.LastWriteTimeUtc;
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
                var after = new FileInfo(filePath);
                if (beforeSize != after.Length || beforeWrite != after.LastWriteTimeUtc)
                {
                    continue;
                }

                var mail = await _parser.ParseAsync(rootPath, filePath, cancellationToken);
                await _database.UpsertAsync(mail, cancellationToken);
                StatusChanged?.Invoke(this, $"반영됨: {Path.GetFileName(filePath)}");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
            }
            catch (Exception exception)
            {
                CrashLogService.Write($"EML 파싱 실패: {filePath}", exception);
                try
                {
                    await _database.UpsertErrorAsync(rootPath, filePath, exception, cancellationToken);
                }
                catch (Exception errorRecordException)
                {
                    CrashLogService.Write($"파싱 오류 기록 실패: {filePath}", errorRecordException);
                }
                StatusChanged?.Invoke(this, $"파싱 오류: {Path.GetFileName(filePath)}");
                return;
            }
        }
    }

    private async Task HandleDeleteAsync(string filePath)
    {
        try
        {
            await _database.DeleteByPathAsync(filePath, _shutdown.Token);
            StatusChanged?.Invoke(this, $"삭제 반영: {Path.GetFileName(filePath)}");
            IndexChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            CrashLogService.Write($"삭제 반영 실패: {filePath}", exception);
        }
    }

    private void QueueBackground(string operationName, Func<Task> action)
    {
        if (_disposed)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                CrashLogService.Write(operationName, exception);
                StatusChanged?.Invoke(this, $"{operationName} 실패: {exception.Message}");
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _shutdown.Cancel();
        _reconcileTimer?.Dispose();
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        foreach (var pending in _pending.Values)
        {
            pending.Cancel();
        }
    }
}
