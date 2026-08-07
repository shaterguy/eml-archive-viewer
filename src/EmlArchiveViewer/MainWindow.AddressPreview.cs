using System.Windows;
using System.Windows.Controls;

namespace EmlArchiveViewer;

public partial class MainWindow
{
    private void ShowRecipients_Click(object sender, RoutedEventArgs e) =>
        ShowAddressListDialog("받는 사람", PreviewRecipients.Text);

    private void ShowCc_Click(object sender, RoutedEventArgs e) =>
        ShowAddressListDialog("참조", PreviewCc.Text);

    private void ShowAddressListDialog(string label, string addresses)
    {
        var items = addresses
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        if (items.Count == 0)
        {
            MessageBox.Show(this, $"{label} 항목이 없습니다.", label,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var textBox = new TextBox
        {
            Text = string.Join(Environment.NewLine, items),
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(12),
            Padding = new Thickness(8),
            FontFamily = FontFamily,
            FontSize = FontSize
        };

        var dialog = new Window
        {
            Owner = this,
            Title = $"{label} 전체 보기 ({items.Count:N0}명)",
            Width = 900,
            Height = 600,
            MinWidth = 520,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Icon = Icon,
            Content = textBox
        };

        dialog.ShowDialog();
    }
}
