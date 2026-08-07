using System.Drawing;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using EmlArchiveViewer.Services;

namespace EmlArchiveViewer;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\EmlArchiveViewer.SingleInstance";
    private const string ActivateEventName = "Local\\EmlArchiveViewer.Activate";
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private bool _ownsMutex;
    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private IndexingService? _indexingService;
    private bool _isExiting;

    public bool IsExiting => _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionHandlers();

        try
        {
            _singleInstanceMutex = new Mutex(true, MutexName, out var createdNew);
            _ownsMutex = createdNew;
            if (!createdNew)
            {
                try
                {
                    EventWaitHandle.OpenExisting(ActivateEventName).Set();
                }
                catch (Exception exception)
                {
                    CrashLogService.Write("기존 프로그램 창 활성화 실패", exception);
                    MessageBox.Show("EML Archive Viewer가 이미 실행 중입니다. 알림 영역 아이콘을 확인해 주세요.",
                        "EML Archive Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                Shutdown(0);
                return;
            }

            StartActivationListener();
            AppPaths.EnsureCreated();

            var settingsService = new SettingsService();
            var settings = await settingsService.LoadAsync();
            var databaseService = new DatabaseService();
            await DatabaseRecoveryService.InitializeAsync(databaseService);

            var parserService = new EmlParserService();
            _indexingService = new IndexingService(databaseService, parserService, settingsService, settings);
            var startupService = new StartupRegistrationService();

            if (settings.StartWithWindows)
            {
                try
                {
                    startupService.SetEnabled(true);
                }
                catch (Exception exception)
                {
                    CrashLogService.Write("Windows 자동 시작 등록 실패", exception);
                }
            }

            _mainWindow = new MainWindow(databaseService, _indexingService, settingsService,
                startupService, settings);
            MainWindow = _mainWindow;
            CreateTrayIcon();

            var backgroundStart = e.Args.Any(arg =>
                string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));
            if (!backgroundStart || !settings.StartMinimized)
            {
                ShowMainWindow();
            }

            // 창과 트레이를 먼저 준비한 뒤 전체 색인은 백그라운드에서 수행한다.
            await _indexingService.StartAsync();
        }
        catch (Exception exception)
        {
            CrashLogService.Write("프로그램 시작 실패", exception);
            MessageBox.Show(
                "프로그램을 시작하지 못했습니다. 기존 색인은 자동 복구를 시도했습니다.\n\n" +
                exception.Message + "\n\n로그: " + CrashLogService.LogPath,
                "EML Archive Viewer 시작 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitApplication(1);
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLogService.Write("UI 처리 중 복구되지 않은 오류", args.Exception);
            args.Handled = true;
            try
            {
                MessageBox.Show(
                    "오류가 발생했지만 프로그램 종료를 차단했습니다.\n\n" + args.Exception.Message +
                    "\n\n로그: " + CrashLogService.LogPath,
                    "EML Archive Viewer 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
            }
        };

        try
        {
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.CatchException);
            Forms.Application.ThreadException += (_, args) =>
                CrashLogService.Write("알림 영역 처리 중 오류", args.Exception);
        }
        catch (Exception exception)
        {
            CrashLogService.Write("Windows Forms 예외 처리기 등록 실패", exception);
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLogService.Write("프로세스의 처리되지 않은 오류", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLogService.Write("백그라운드 작업 오류", args.Exception);
            args.SetObserved();
        };
    }

    private void StartActivationListener()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _ = Task.Run(() =>
        {
            try
            {
                while (!_isExiting)
                {
                    _activateEvent.WaitOne();
                    if (!_isExiting && !Dispatcher.HasShutdownStarted)
                    {
                        RequestShowMainWindow();
                    }
                }
            }
            catch (Exception exception)
            {
                if (!_isExiting)
                {
                    CrashLogService.Write("단일 실행 활성화 리스너 오류", exception);
                }
            }
        });
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("EML 보관함 열기", null, (_, _) => RequestShowMainWindow());
        menu.Items.Add("지금 다시 색인", null, (_, _) =>
        {
            if (_indexingService is not null)
            {
                _ = _indexingService.ReconcileAllAsync();
            }
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("프로그램 종료", null, (_, _) => RequestExitApplication());

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "EML Archive Viewer",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                RequestShowMainWindow();
            }
        };
    }

    private void RequestShowMainWindow()
    {
        if (_isExiting || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            ShowMainWindowCore();
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ShowMainWindowCore));
        }
        catch (Exception exception)
        {
            CrashLogService.Write("알림 영역에서 창 열기 요청 실패", exception);
        }
    }

    public void ShowMainWindow() => RequestShowMainWindow();

    private void ShowMainWindowCore()
    {
        if (_mainWindow is null || _isExiting || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            _mainWindow.ShowInTaskbar = true;
            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }

            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            _mainWindow.Activate();
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
            _mainWindow.Focus();
        }
        catch (Exception exception)
        {
            CrashLogService.Write("주 창 표시 실패", exception);
        }
    }

    private void RequestExitApplication(int exitCode = 0)
    {
        if (_isExiting || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            ExitApplicationCore(exitCode);
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Send,
                new Action(() => ExitApplicationCore(exitCode)));
        }
        catch (Exception exception)
        {
            CrashLogService.Write("알림 영역에서 종료 요청 실패", exception);
        }
    }

    public void ExitApplication(int exitCode = 0) => RequestExitApplication(exitCode);

    private void ExitApplicationCore(int exitCode)
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        try
        {
            _indexingService?.Dispose();
        }
        catch (Exception exception)
        {
            CrashLogService.Write("색인 서비스 종료 실패", exception);
        }

        if (_trayIcon is not null)
        {
            try
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            catch (Exception exception)
            {
                CrashLogService.Write("알림 영역 아이콘 종료 실패", exception);
            }
            finally
            {
                _trayIcon = null;
            }
        }

        try
        {
            _mainWindow?.Close();
        }
        catch (Exception exception)
        {
            CrashLogService.Write("주 창 종료 실패", exception);
        }

        Shutdown(exitCode);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _activateEvent?.Set();
            _activateEvent?.Dispose();
            if (_ownsMutex)
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            _singleInstanceMutex?.Dispose();
        }
        catch (Exception exception)
        {
            CrashLogService.Write("프로세스 종료 정리 실패", exception);
        }
        base.OnExit(e);
    }
}
