using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using EmlArchiveViewer.Models;
using EmlArchiveViewer.Services;
using MimeKit;

namespace EmlArchiveViewer;

public partial class MainWindow : Window
{
    private const string AllArchivesLayoutKey = "__ALL_ARCHIVES__";

    private readonly DatabaseService _database;
    private readonly IndexingService _indexing;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly EmlParserService _parser = new();
    private readonly HtmlPreviewService _htmlPreview = new();
    private readonly AttachmentService _attachmentService = new();
    private readonly ObservableCollection<MailRecord> _mails = [];
    private FolderNode? _selectedFolder;
    private MimeMessage? _selectedMessage;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _quickSearchDebounce;
    private CancellationTokenSource? _indexRefreshDebounce;
    private string _folderTreeSignature = string.Empty;
    private double _preservedHorizontalOffset;
    private bool _changingScopeFromFolder;
    private bool _applyingColumnOrder;

    public MainWindow(DatabaseService database, IndexingService indexing, SettingsService settingsService,
        StartupRegistrationService startupService, AppSettings settings)
    {
        InitializeComponent();
        _database = database;
        _indexing = indexing;
        _settingsService = settingsService;
        _settings = settings;
        Width = Math.Max(MinWidth, settings.WindowWidth);
        Height = Math.Max(MinHeight, settings.WindowHeight);
        MailGrid.ItemsSource = _mails;
        _indexing.IndexChanged += Indexing_IndexChanged;
        _indexing.StatusChanged += (_, status) => Dispatcher.Invoke(() => StatusText.Text = status);
        Loaded += async (_, _) =>
        {
            BuildFolderTree();
            ApplyColumnOrderForSelectedFolder();
            await RunSearchAsync();
        };
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "EML 파일이 저장된 기준 폴더 선택",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await SaveColumnOrderAsync(_selectedFolder);
        await _indexing.AddRootAsync(dialog.FolderName);
        BuildFolderTree(keepSelection: true);
        ApplyColumnOrderForSelectedFolder();
        await RunSearchAsync();
    }

    private async void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFolder is null || _selectedFolder.IsAllArchives || string.IsNullOrWhiteSpace(_selectedFolder.RootPath))
        {
            MessageBox.Show(this, "해제할 등록 기준 폴더의 최상위 항목을 선택해 주세요.", "폴더 해제",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_selectedFolder.RelativePath))
        {
            MessageBox.Show(this, "하위 폴더가 아니라 등록한 기준 폴더의 최상위 항목을 선택해 주세요.", "폴더 해제",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, $"등록을 해제하시겠습니까?\n{_selectedFolder.RootPath}\n\n원본 EML 파일은 삭제되지 않습니다.",
            "폴더 해제", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await SaveColumnOrderAsync(_selectedFolder);
        await _indexing.RemoveRootAsync(_selectedFolder.RootPath);
        _selectedFolder = null;
        BuildFolderTree();
        ApplyColumnOrderForSelectedFolder();
        await RunSearchAsync();
    }

    private async void Reindex_Click(object sender, RoutedEventArgs e) => await _indexing.ReconcileAllAsync();
    private async void Search_Click(object sender, RoutedEventArgs e) => await RunSearchAsync();

    private async void SearchControl_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_changingScopeFromFolder)
        {
            await RunSearchAsync();
        }
    }

    private async void QuickSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await RunSearchAsync();
        }
    }

    private void QuickSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _quickSearchDebounce?.Cancel();
        _quickSearchDebounce?.Dispose();
        _quickSearchDebounce = new CancellationTokenSource();
        var token = _quickSearchDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token);
                await await Dispatcher.InvokeAsync(RunSearchAsync);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private async void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        _quickSearchDebounce?.Cancel();
        QuickSearchBox.Clear();
        SubjectSearchBox.Clear();
        BodySearchBox.Clear();
        SenderSearchBox.Clear();
        RecipientSearchBox.Clear();
        CcSearchBox.Clear();
        AttachmentSearchBox.Clear();
        DateFromPicker.SelectedDate = null;
        DateToPicker.SelectedDate = null;
        AttachmentFilterCombo.SelectedIndex = 0;
        await RunSearchAsync();
    }

    private async void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var nextFolder = e.NewValue as FolderNode;
        if (ReferenceEquals(nextFolder, _selectedFolder))
        {
            return;
        }

        await SaveColumnOrderAsync(_selectedFolder);
        _selectedFolder = nextFolder;

        _changingScopeFromFolder = true;
        try
        {
            SearchScopeCombo.SelectedIndex = _selectedFolder is null || _selectedFolder.IsAllArchives ? 0 : 1;
        }
        finally
        {
            _changingScopeFromFolder = false;
        }

        ApplyColumnOrderForSelectedFolder();
        await RunSearchAsync();
    }

    private void Indexing_IndexChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ScheduleIndexRefresh);
            return;
        }
        ScheduleIndexRefresh();
    }

    private void ScheduleIndexRefresh()
    {
        _indexRefreshDebounce?.Cancel();
        _indexRefreshDebounce?.Dispose();
        _indexRefreshDebounce = new CancellationTokenSource();
        var token = _indexRefreshDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(600, token);
                await Dispatcher.InvokeAsync(async () =>
                {
                    if (RefreshFolderTreeIfChanged())
                    {
                        ApplyColumnOrderForSelectedFolder();
                    }
                    await RunSearchAsync();
                });
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private async Task RunSearchAsync()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        try
        {
            StatusText.Text = "검색 중...";
            var criteria = BuildCriteria();
            var results = await _database.SearchAsync(criteria, cancellationToken: token);
            if (!AreSameResults(results))
            {
                var selectedPath = (MailGrid.SelectedItem as MailRecord)?.FilePath;
                var scrollViewer = FindVisualChild<ScrollViewer>(MailGrid);
                var horizontalOffset = scrollViewer?.HorizontalOffset ?? 0;
                var verticalOffset = scrollViewer?.VerticalOffset ?? 0;

                _mails.Clear();
                foreach (var mail in results)
                {
                    _mails.Add(mail);
                }

                if (!string.IsNullOrWhiteSpace(selectedPath))
                {
                    MailGrid.SelectedItem = _mails.FirstOrDefault(mail =>
                        string.Equals(mail.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase));
                }

                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    var viewer = FindVisualChild<ScrollViewer>(MailGrid);
                    viewer?.ScrollToHorizontalOffset(horizontalOffset);
                    viewer?.ScrollToVerticalOffset(verticalOffset);
                });
            }

            ResultCountText.Text = $"{results.Count:N0}개";
            StatusText.Text = criteria.Scope == SearchScope.AllRegisteredFolders
                ? "전체 등록 폴더"
                : $"현재 폴더 및 하위 폴더: {_selectedFolder?.Name ?? "전체"}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = "검색 실패";
            MessageBox.Show(this, exception.Message, "검색 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool AreSameResults(IReadOnlyList<MailRecord> results)
    {
        if (_mails.Count != results.Count)
        {
            return false;
        }

        for (var index = 0; index < results.Count; index++)
        {
            var current = _mails[index];
            var next = results[index];
            if (current.Id != next.Id || current.ModifiedUtc != next.ModifiedUtc ||
                !string.Equals(current.Subject, next.Subject, StringComparison.Ordinal) ||
                !string.Equals(current.RelativeFolderPath, next.RelativeFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private SearchCriteria BuildCriteria()
    {
        var attachmentTag = (AttachmentFilterCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return new SearchCriteria
        {
            QuickText = QuickSearchBox.Text,
            Subject = SubjectSearchBox.Text,
            Body = BodySearchBox.Text,
            Sender = SenderSearchBox.Text,
            Recipients = RecipientSearchBox.Text,
            Cc = CcSearchBox.Text,
            AttachmentName = AttachmentSearchBox.Text,
            DateFrom = DateFromPicker.SelectedDate,
            DateTo = DateToPicker.SelectedDate,
            HasAttachment = attachmentTag switch { "Yes" => true, "No" => false, _ => null },
            Scope = GetSelectedScope(),
            SelectedRootPath = _selectedFolder?.RootPath,
            SelectedRelativeFolderPath = _selectedFolder?.RelativePath
        };
    }

    private SearchScope GetSelectedScope() =>
        (SearchScopeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Current"
            ? SearchScope.SelectedFolderAndDescendants
            : SearchScope.AllRegisteredFolders;

    private async void MailGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RestoreMailHorizontalOffset();
        if (MailGrid.SelectedItem is not MailRecord mail)
        {
            ClearPreview();
            return;
        }

        PreviewSubject.Text = mail.Subject;
        PreviewSender.Text = mail.Sender;
        PreviewRecipients.Text = mail.Recipients;
        PreviewCc.Text = mail.Cc;
        PreviewDate.Text = mail.SentDate?.LocalDateTime.ToString("yyyy.MM.dd HH:mm:ss") ?? string.Empty;
        PreviewPath.Text = mail.FilePath;
        TextPreview.Text = mail.TextBody;
        ErrorPreview.Text = mail.ParseError;

        try
        {
            _selectedMessage = await _parser.LoadMessageAsync(mail.FilePath);
            AttachmentList.ItemsSource = EmlParserService.GetAttachments(_selectedMessage);
            HtmlBrowser.NavigateToString(_htmlPreview.BuildSafeHtml(_selectedMessage));
        }
        catch (Exception exception)
        {
            _selectedMessage = null;
            AttachmentList.ItemsSource = null;
            ErrorPreview.Text = string.IsNullOrWhiteSpace(mail.ParseError)
                ? exception.Message
                : mail.ParseError + Environment.NewLine + exception.Message;
            HtmlBrowser.NavigateToString("<html><body><p>메일을 표시할 수 없습니다.</p></body></html>");
        }
        finally
        {
            RestoreMailHorizontalOffset();
        }
    }

    private void MailGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _preservedHorizontalOffset = FindVisualChild<ScrollViewer>(MailGrid)?.HorizontalOffset ?? 0;
    }

    private void MailGrid_CurrentCellChanged(object? sender, EventArgs e) => RestoreMailHorizontalOffset();

    private void RestoreMailHorizontalOffset()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            FindVisualChild<ScrollViewer>(MailGrid)?.ScrollToHorizontalOffset(_preservedHorizontalOffset);
        });
    }

    private async void MailGrid_ColumnReordered(object sender, DataGridColumnEventArgs e)
    {
        if (!_applyingColumnOrder)
        {
            await SaveColumnOrderAsync(_selectedFolder);
        }
    }

    private async Task SaveColumnOrderAsync(FolderNode? folder)
    {
        if (!IsLoaded || _applyingColumnOrder || MailGrid.Columns.Count == 0)
        {
            return;
        }

        var key = GetColumnLayoutKey(folder);
        _settings.ColumnOrderByFolder[key] = MailGrid.Columns
            .OrderBy(column => column.DisplayIndex)
            .Select(GetColumnId)
            .ToList();
        await _settingsService.SaveAsync(_settings);
    }

    private void ApplyColumnOrderForSelectedFolder()
    {
        if (!_settings.ColumnOrderByFolder.TryGetValue(GetColumnLayoutKey(_selectedFolder), out var order) ||
            order.Count == 0)
        {
            return;
        }

        _applyingColumnOrder = true;
        try
        {
            for (var displayIndex = 0; displayIndex < order.Count; displayIndex++)
            {
                var column = MailGrid.Columns.FirstOrDefault(candidate =>
                    string.Equals(GetColumnId(candidate), order[displayIndex], StringComparison.Ordinal));
                if (column is not null)
                {
                    column.DisplayIndex = Math.Min(displayIndex, MailGrid.Columns.Count - 1);
                }
            }
        }
        finally
        {
            _applyingColumnOrder = false;
        }
    }

    private static string GetColumnId(DataGridColumn column) =>
        string.IsNullOrWhiteSpace(column.SortMemberPath) ? column.Header?.ToString() ?? string.Empty : column.SortMemberPath;

    private static string GetColumnLayoutKey(FolderNode? folder) =>
        folder is null || folder.IsAllArchives || string.IsNullOrWhiteSpace(folder.FullPath)
            ? AllArchivesLayoutKey
            : Path.GetFullPath(folder.FullPath);

    private async void AttachmentList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelectedAttachmentAsync();
    private async void OpenAttachment_Click(object sender, RoutedEventArgs e) => await OpenSelectedAttachmentAsync();

    private async Task OpenSelectedAttachmentAsync()
    {
        if (_selectedMessage is null || AttachmentList.SelectedItem is not AttachmentInfo attachment)
        {
            return;
        }

        try
        {
            await _attachmentService.OpenAttachmentAsync(_selectedMessage, attachment.Index, attachment.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "첨부파일 열기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMessage is null || AttachmentList.SelectedItem is not AttachmentInfo attachment)
        {
            return;
        }

        var dialog = new SaveFileDialog { FileName = attachment.FileName, Title = "첨부파일 저장" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await _attachmentService.SaveAttachmentAsync(_selectedMessage, attachment.Index, dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "첨부파일 저장 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BuildFolderTree(bool keepSelection = false)
    {
        var previousPath = keepSelection ? _selectedFolder?.FullPath : null;
        var previousWasAll = !keepSelection || _selectedFolder is null || _selectedFolder.IsAllArchives;
        var expandedPaths = CaptureExpandedPaths();
        var roots = new ObservableCollection<FolderNode>();
        var allArchives = new FolderNode
        {
            Name = "전체 보관함",
            IsAllArchives = true,
            IsSelected = previousWasAll
        };
        roots.Add(allArchives);

        foreach (var rootPath in _settings.RootFolders.OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase))
        {
            var directoryInfo = new DirectoryInfo(rootPath);
            var root = new FolderNode
            {
                Name = string.IsNullOrWhiteSpace(directoryInfo.Name) ? rootPath : directoryInfo.Name,
                FullPath = rootPath,
                RootPath = rootPath,
                RelativePath = string.Empty,
                IsExpanded = expandedPaths.Contains(Path.GetFullPath(rootPath))
            };
            PopulateChildren(root, rootPath, rootPath, expandedPaths);
            roots.Add(root);
        }

        FolderTree.ItemsSource = roots;
        if (!string.IsNullOrWhiteSpace(previousPath))
        {
            _selectedFolder = FindNodeByPath(roots, previousPath);
            if (_selectedFolder is not null)
            {
                _selectedFolder.IsSelected = true;
            }
        }
        else
        {
            _selectedFolder = allArchives;
        }
        _folderTreeSignature = CreateFolderTreeSignature();
    }

    private bool RefreshFolderTreeIfChanged()
    {
        var currentSignature = CreateFolderTreeSignature();
        if (string.Equals(currentSignature, _folderTreeSignature, StringComparison.Ordinal))
        {
            return false;
        }
        BuildFolderTree(keepSelection: true);
        return true;
    }

    private HashSet<string> CaptureExpandedPaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (FolderTree.ItemsSource is IEnumerable<FolderNode> nodes)
        {
            CaptureExpandedPaths(nodes, result);
        }
        return result;
    }

    private static void CaptureExpandedPaths(IEnumerable<FolderNode> nodes, HashSet<string> result)
    {
        foreach (var node in nodes)
        {
            if (node.IsExpanded && !string.IsNullOrWhiteSpace(node.FullPath))
            {
                result.Add(Path.GetFullPath(node.FullPath));
            }
            CaptureExpandedPaths(node.Children, result);
        }
    }

    private string CreateFolderTreeSignature()
    {
        var paths = new List<string>();
        foreach (var rootPath in _settings.RootFolders.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var fullRoot = Path.GetFullPath(rootPath);
            paths.Add(fullRoot);
            if (!Directory.Exists(fullRoot))
            {
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(fullRoot);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(current)
                                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        var fullPath = Path.GetFullPath(directory);
                        paths.Add(fullPath);
                        pending.Push(fullPath);
                    }
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                }
            }
        }
        return string.Join('\n', paths);
    }

    private static void PopulateChildren(FolderNode parent, string rootPath, string currentPath,
        HashSet<string> expandedPaths)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(currentPath)
                         .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase))
            {
                var fullPath = Path.GetFullPath(directory);
                var child = new FolderNode
                {
                    Name = Path.GetFileName(directory),
                    FullPath = fullPath,
                    RootPath = rootPath,
                    RelativePath = Path.GetRelativePath(rootPath, directory),
                    IsExpanded = expandedPaths.Contains(fullPath)
                };
                PopulateChildren(child, rootPath, directory, expandedPaths);
                parent.Children.Add(child);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static FolderNode? FindNodeByPath(IEnumerable<FolderNode> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
            var child = FindNodeByPath(node.Children, path);
            if (child is not null)
            {
                return child;
            }
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed)
            {
                return typed;
            }
            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }
        return null;
    }

    private void ClearPreview()
    {
        PreviewSubject.Text = string.Empty;
        PreviewSender.Text = string.Empty;
        PreviewRecipients.Text = string.Empty;
        PreviewCc.Text = string.Empty;
        PreviewDate.Text = string.Empty;
        PreviewPath.Text = string.Empty;
        TextPreview.Text = string.Empty;
        ErrorPreview.Text = string.Empty;
        AttachmentList.ItemsSource = null;
        HtmlBrowser.NavigateToString("<html><body></body></html>");
        _selectedMessage = null;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        await SaveColumnOrderAsync(_selectedFolder);
        if (Application.Current is App app && !app.IsExiting)
        {
            e.Cancel = true;
            ShowInTaskbar = false;
            Hide();
            if (!_settings.CloseHintShown)
            {
                _settings.CloseHintShown = true;
                await _settingsService.SaveAsync(_settings);
                MessageBox.Show("프로그램은 종료되지 않고 알림 영역에서 계속 실행됩니다.\n실제 종료는 알림 영역 아이콘의 '프로그램 종료'를 사용하세요.",
                    "EML Archive Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        _quickSearchDebounce?.Cancel();
        _indexRefreshDebounce?.Cancel();
        _searchCancellation?.Cancel();
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        await _settingsService.SaveAsync(_settings);
    }
}