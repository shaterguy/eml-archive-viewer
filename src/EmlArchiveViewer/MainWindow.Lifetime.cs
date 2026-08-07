using System.ComponentModel;
using System.Windows;
using EmlArchiveViewer.Services;

namespace EmlArchiveViewer;

public partial class MainWindow
{
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);

        // XAML에서 연결된 기존 async void Closing 핸들러는 첫 await 전에
        // e.Cancel을 설정하지 못해 창이 실제로 닫힐 수 있으므로 교체한다.
        Closing -= Window_Closing;
        Closing += Window_Closing_Safe;
    }

    private void Window_Closing_Safe(object? sender, CancelEventArgs e)
    {
        CaptureWindowState();

        if (Application.Current is App app && !app.IsExiting)
        {
            // 닫기 이벤트가 반환되기 전에 반드시 취소해야 한다.
            e.Cancel = true;
            ShowInTaskbar = false;
            Hide();

            PersistWindowStateInBackground("창 숨김 상태 저장 실패");

            if (!_settings.CloseHintShown)
            {
                _settings.CloseHintShown = true;
                PersistWindowStateInBackground("알림 영역 안내 상태 저장 실패");
                MessageBox.Show(
                    "프로그램은 종료되지 않고 알림 영역에서 계속 실행됩니다.\n실제 종료는 알림 영역 아이콘의 '프로그램 종료'를 사용하세요.",
                    "EML Archive Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        _quickSearchDebounce?.Cancel();
        _indexRefreshDebounce?.Cancel();
        _searchCancellation?.Cancel();

        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception exception)
        {
            CrashLogService.Write("프로그램 종료 상태 저장 실패", exception);
        }
    }

    private void CaptureWindowState()
    {
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;

        if (!_applyingColumnOrder && MailGrid.Columns.Count > 0)
        {
            var key = GetColumnLayoutKey(_selectedFolder);
            _settings.ColumnOrderByFolder[key] = MailGrid.Columns
                .OrderBy(column => column.DisplayIndex)
                .Select(GetColumnId)
                .ToList();
        }

        CaptureColumnWidths();
    }

    private void PersistWindowStateInBackground(string operationName)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _settingsService.SaveAsync(_settings).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                CrashLogService.Write(operationName, exception);
            }
        });
    }
}
