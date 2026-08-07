using System.Collections.ObjectModel;

namespace EmlArchiveViewer.Models;

public sealed class AppSettings
{
    public List<string> RootFolders { get; set; } = [];
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool CloseHintShown { get; set; }
    public int MailParserVersion { get; set; }
    public double WindowWidth { get; set; } = 1500;
    public double WindowHeight { get; set; } = 900;
    public Dictionary<string, List<string>> ColumnOrderByFolder { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ColumnWidthSetting> ColumnWidths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ColumnWidthSetting
{
    public double Value { get; set; }
    public string UnitType { get; set; } = "Pixel";
}

public sealed class FolderNode
{
    public required string Name { get; init; }
    public string? FullPath { get; init; }
    public string? RootPath { get; init; }
    public string RelativePath { get; init; } = string.Empty;
    public bool IsAllArchives { get; init; }
    public bool IsExpanded { get; set; }
    public bool IsSelected { get; set; }
    public ObservableCollection<FolderNode> Children { get; } = [];
    public override string ToString() => Name;
}

public sealed class MailRecord
{
    public long Id { get; set; }
    public required string FilePath { get; set; }
    public required string RootPath { get; set; }
    public required string RelativeFolderPath { get; set; }
    public required string FileName { get; set; }
    public long FileSize { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public DateTimeOffset? SentDate { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string Recipients { get; set; } = string.Empty;
    public string Cc { get; set; } = string.Empty;
    public string Bcc { get; set; } = string.Empty;
    public string TextBody { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public int AttachmentCount { get; set; }
    public string AttachmentNames { get; set; } = string.Empty;
    public string ParseError { get; set; } = string.Empty;
    public string SentDateDisplay => SentDate?.LocalDateTime.ToString("yyyy.MM.dd HH:mm") ?? string.Empty;
    public string AttachmentDisplay => AttachmentCount > 0 ? $"📎 {AttachmentCount}" : string.Empty;
}

public sealed class AttachmentInfo
{
    public int Index { get; init; }
    public required string FileName { get; init; }
    public string MimeType { get; init; } = string.Empty;
    public long Size { get; init; }
    public string SizeDisplay => Size switch
    {
        < 1024 => $"{Size:N0} B",
        < 1024 * 1024 => $"{Size / 1024d:N1} KB",
        _ => $"{Size / 1024d / 1024d:N1} MB"
    };
}

public enum SearchScope
{
    AllRegisteredFolders,
    SelectedFolderAndDescendants
}

public sealed class SearchCriteria
{
    public string QuickText { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string Recipients { get; set; } = string.Empty;
    public string Cc { get; set; } = string.Empty;
    public string AttachmentName { get; set; } = string.Empty;
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public bool? HasAttachment { get; set; }
    public SearchScope Scope { get; set; }
    public string? SelectedRootPath { get; set; }
    public string? SelectedRelativeFolderPath { get; set; }
}
