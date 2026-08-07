using System.Drawing;
using System.Windows.Threading;

namespace EmlArchiveViewer;

public partial class App
{
    private DispatcherTimer? _trayIconInitializationTimer;
    private Icon? _trayApplicationIcon;
    private int _trayIconInitializationAttempts;

    public App()
    {
        InitializeComponent();
        Startup += (_, _) => BeginTrayIconInitialization();
        Exit += (_, _) => DisposeTrayApplicationIcon();
    }

    private void BeginTrayIconInitialization()
    {
        if (TryApplyApplicationIconToTray())
        {
            return;
        }

        _trayIconInitializationTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _trayIconInitializationTimer.Tick += TrayIconInitializationTimer_Tick;
        _trayIconInitializationTimer.Start();
    }

    private void TrayIconInitializationTimer_Tick(object? sender, EventArgs e)
    {
        _trayIconInitializationAttempts++;
        if (TryApplyApplicationIconToTray() || _trayIconInitializationAttempts >= 40)
        {
            StopTrayIconInitializationTimer();
        }
    }

    private bool TryApplyApplicationIconToTray()
    {
        if (_trayIcon is null)
        {
            return false;
        }

        var loadedIcon = LoadApplicationIcon();
        if (loadedIcon is null)
        {
            return true;
        }

        _trayApplicationIcon?.Dispose();
        _trayApplicationIcon = loadedIcon;
        _trayIcon.Icon = _trayApplicationIcon;
        return true;
    }

    private static Icon? LoadApplicationIcon()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return null;
            }

            using var extracted = Icon.ExtractAssociatedIcon(executablePath);
            return extracted is null ? null : (Icon)extracted.Clone();
        }
        catch (Exception exception)
        {
            Services.CrashLogService.Write("프로그램 아이콘 로드 실패", exception);
            return null;
        }
    }

    private void StopTrayIconInitializationTimer()
    {
        if (_trayIconInitializationTimer is null)
        {
            return;
        }

        _trayIconInitializationTimer.Stop();
        _trayIconInitializationTimer.Tick -= TrayIconInitializationTimer_Tick;
        _trayIconInitializationTimer = null;
    }

    private void DisposeTrayApplicationIcon()
    {
        StopTrayIconInitializationTimer();
        _trayApplicationIcon?.Dispose();
        _trayApplicationIcon = null;
    }
}
