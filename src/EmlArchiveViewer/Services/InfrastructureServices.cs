using System.Text.Json;
using Microsoft.Win32;
using EmlArchiveViewer.Models;

namespace EmlArchiveViewer.Services;

public static class AppPaths
{
    public static string BaseDirectory { get; } = ResolveBaseDirectory();
    public static string DatabasePath => Path.Combine(BaseDirectory, "index.db");
    public static string SettingsPath => Path.Combine(BaseDirectory, "settings.json");
    public static string CacheDirectory => Path.Combine(BaseDirectory, "cache");

    private static string ResolveBaseDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("EML_ARCHIVE_VIEWER_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EMLArchiveViewer");
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsPath))
            {
                return new AppSettings();
            }

            await using var stream = File.OpenRead(AppPaths.SettingsPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions).ConfigureAwait(false)
                ?? new AppSettings();
        }
        catch (Exception exception)
        {
            CrashLogService.Write("설정 파일 읽기 실패. 기본 설정을 사용합니다.", exception);
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            AppPaths.EnsureCreated();
            var tempPath = AppPaths.SettingsPath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions).ConfigureAwait(false);
            }
            File.Move(tempPath, AppPaths.SettingsPath, true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public void Save(AppSettings settings)
    {
        _saveLock.Wait();
        try
        {
            AppPaths.EnsureCreated();
            var tempPath = AppPaths.SettingsPath + ".tmp";
            using (var stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
            }
            File.Move(tempPath, AppPaths.SettingsPath, true);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}

public sealed class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EMLArchiveViewer";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
        if (enabled)
        {
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("실행 파일 경로를 확인할 수 없습니다.");
            key.SetValue(ValueName, $"\"{executablePath}\" --background");
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
