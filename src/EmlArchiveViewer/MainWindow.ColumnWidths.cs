using System.Windows.Controls;
using EmlArchiveViewer.Models;

namespace EmlArchiveViewer;

public partial class MainWindow
{
    private bool _columnWidthsApplied;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        ApplySavedColumnWidths();
    }

    private void ApplySavedColumnWidths()
    {
        if (_columnWidthsApplied || MailGrid.Columns.Count == 0)
        {
            return;
        }

        _columnWidthsApplied = true;
        _settings.ColumnWidths ??= new Dictionary<string, ColumnWidthSetting>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in MailGrid.Columns)
        {
            if (!_settings.ColumnWidths.TryGetValue(GetColumnId(column), out var savedWidth) ||
                !TryCreateDataGridLength(column, savedWidth, out var width))
            {
                continue;
            }

            column.Width = width;
        }
    }

    private void CaptureColumnWidths()
    {
        // Windows 자동 시작 후 창을 한 번도 표시하지 않은 세션에서는
        // XAML 기본값으로 기존 사용자 너비를 덮어쓰지 않는다.
        if (!_columnWidthsApplied || MailGrid.Columns.Count == 0)
        {
            return;
        }

        var widths = new Dictionary<string, ColumnWidthSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in MailGrid.Columns)
        {
            var width = column.Width;
            if (!double.IsFinite(width.Value) || width.Value < 0)
            {
                continue;
            }

            widths[GetColumnId(column)] = new ColumnWidthSetting
            {
                Value = width.Value,
                UnitType = width.UnitType.ToString()
            };
        }

        _settings.ColumnWidths = widths;
    }

    private static bool TryCreateDataGridLength(
        DataGridColumn column,
        ColumnWidthSetting savedWidth,
        out DataGridLength width)
    {
        width = default;
        if (savedWidth is null ||
            !Enum.TryParse(savedWidth.UnitType, ignoreCase: true, out DataGridLengthUnitType unitType))
        {
            return false;
        }

        switch (unitType)
        {
            case DataGridLengthUnitType.Auto:
                width = DataGridLength.Auto;
                return true;
            case DataGridLengthUnitType.SizeToCells:
                width = DataGridLength.SizeToCells;
                return true;
            case DataGridLengthUnitType.SizeToHeader:
                width = DataGridLength.SizeToHeader;
                return true;
            case DataGridLengthUnitType.Star:
                if (!double.IsFinite(savedWidth.Value) || savedWidth.Value <= 0)
                {
                    return false;
                }
                width = new DataGridLength(savedWidth.Value, DataGridLengthUnitType.Star);
                return true;
            case DataGridLengthUnitType.Pixel:
                if (!double.IsFinite(savedWidth.Value) || savedWidth.Value <= 0)
                {
                    return false;
                }

                var pixelWidth = Math.Max(column.MinWidth, savedWidth.Value);
                if (double.IsFinite(column.MaxWidth))
                {
                    pixelWidth = Math.Min(column.MaxWidth, pixelWidth);
                }

                width = new DataGridLength(pixelWidth, DataGridLengthUnitType.Pixel);
                return true;
            default:
                return false;
        }
    }
}
