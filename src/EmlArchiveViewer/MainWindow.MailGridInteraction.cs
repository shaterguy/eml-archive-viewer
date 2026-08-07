using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EmlArchiveViewer;

public partial class MainWindow
{
    private bool _suppressMailCellBringIntoView;

    private void MailGrid_PreviewMouseLeftButtonDown_NoAutoScroll(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindVisualParent<DataGridColumnHeader>(source) is not null)
        {
            return;
        }

        var row = FindVisualParent<DataGridRow>(source);
        if (row is null)
        {
            return;
        }

        _preservedHorizontalOffset = FindVisualChild<ScrollViewer>(MailGrid)?.HorizontalOffset ?? 0;
        _suppressMailCellBringIntoView = true;
        try
        {
            MailGrid.SelectedItem = row.Item;
            MailGrid.Focus();
            e.Handled = true;
        }
        finally
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                _suppressMailCellBringIntoView = false;
            }));
        }
    }

    private void MailGrid_RequestBringIntoView_NoAutoScroll(object sender, RequestBringIntoViewEventArgs e)
    {
        if (!_suppressMailCellBringIntoView)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        if (FindVisualParent<DataGridCell>(source) is not null ||
            FindVisualParent<DataGridRow>(source) is not null)
        {
            e.Handled = true;
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
