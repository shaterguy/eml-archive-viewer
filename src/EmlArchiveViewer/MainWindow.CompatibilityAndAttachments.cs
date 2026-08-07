using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using EmlArchiveViewer.Models;
using EmlArchiveViewer.Services;

namespace EmlArchiveViewer;

public sealed class LegacyKoreanTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        LegacyKoreanTextRepair.Repair(value?.ToString());

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public partial class MainWindow
{
    private const int CurrentMailParserVersion = 2;

    private async void Window_Loaded_Compatibility(object sender, RoutedEventArgs e)
    {
        MailGrid.SelectionChanged += MailGrid_SelectionChanged_RepairPreview;
        RepairSelectedPreview();

        if (_settings.MailParserVersion >= CurrentMailParserVersion)
        {
            return;
        }

        try
        {
            StatusText.Text = "한글 호환 색인 갱신 중...";
            var repairedCount = await RepairIndexedMailAsync();
            _settings.MailParserVersion = CurrentMailParserVersion;
            await _settingsService.SaveAsync(_settings);
            StatusText.Text = repairedCount == 0
                ? "한글 호환 색인 확인 완료"
                : $"한글 호환 색인 갱신 완료: {repairedCount:N0}개";
            await RunSearchAsync();
        }
        catch (Exception exception)
        {
            CrashLogService.Write("한글 호환 색인 갱신 실패", exception);
            StatusText.Text = "한글 호환 색인 갱신 실패";
        }
    }

    private static async Task<int> RepairIndexedMailAsync()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var timeout = connection.CreateCommand();
        timeout.CommandText = "PRAGMA busy_timeout=30000;";
        await timeout.ExecuteNonQueryAsync();

        var select = connection.CreateCommand();
        select.CommandText = """
            SELECT id, subject, sender, recipients, cc, bcc, text_body, html_body, attachment_names
            FROM mails;
            """;
        var updates = new List<IndexedMailText>();
        await using (var reader = await select.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var original = new IndexedMailText(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                    reader.GetString(8));
                var repaired = original with
                {
                    Subject = LegacyKoreanTextRepair.Repair(original.Subject),
                    Sender = LegacyKoreanTextRepair.Repair(original.Sender),
                    Recipients = LegacyKoreanTextRepair.Repair(original.Recipients),
                    Cc = LegacyKoreanTextRepair.Repair(original.Cc),
                    Bcc = LegacyKoreanTextRepair.Repair(original.Bcc),
                    TextBody = LegacyKoreanTextRepair.Repair(original.TextBody),
                    HtmlBody = LegacyKoreanTextRepair.Repair(original.HtmlBody),
                    AttachmentNames = LegacyKoreanTextRepair.Repair(original.AttachmentNames)
                };
                if (repaired != original)
                {
                    updates.Add(repaired);
                }
            }
        }

        if (updates.Count == 0)
        {
            return 0;
        }

        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var update in updates)
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE mails SET
                    subject=$subject,
                    sender=$sender,
                    recipients=$recipients,
                    cc=$cc,
                    bcc=$bcc,
                    text_body=$textBody,
                    html_body=$htmlBody,
                    attachment_names=$attachmentNames
                WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$subject", update.Subject);
            command.Parameters.AddWithValue("$sender", update.Sender);
            command.Parameters.AddWithValue("$recipients", update.Recipients);
            command.Parameters.AddWithValue("$cc", update.Cc);
            command.Parameters.AddWithValue("$bcc", update.Bcc);
            command.Parameters.AddWithValue("$textBody", update.TextBody);
            command.Parameters.AddWithValue("$htmlBody", update.HtmlBody);
            command.Parameters.AddWithValue("$attachmentNames", update.AttachmentNames);
            command.Parameters.AddWithValue("$id", update.Id);
            await command.ExecuteNonQueryAsync();
        }

        var rebuildSearch = connection.CreateCommand();
        rebuildSearch.Transaction = (SqliteTransaction)transaction;
        rebuildSearch.CommandText = """
            DELETE FROM mail_fts;
            INSERT INTO mail_fts(rowid, subject, sender, recipients, cc, text_body, attachment_names, relative_folder_path)
            SELECT id, subject, sender, recipients, cc, text_body, attachment_names, relative_folder_path
            FROM mails;
            """;
        await rebuildSearch.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return updates.Count;
    }

    private void MailGrid_SelectionChanged_RepairPreview(object sender, SelectionChangedEventArgs e) =>
        RepairSelectedPreview();

    private void RepairSelectedPreview()
    {
        if (MailGrid.SelectedItem is not MailRecord mail)
        {
            return;
        }

        PreviewSubject.Text = LegacyKoreanTextRepair.Repair(mail.Subject);
        PreviewSender.Text = LegacyKoreanTextRepair.Repair(mail.Sender);
        PreviewRecipients.Text = LegacyKoreanTextRepair.Repair(mail.Recipients);
        PreviewCc.Text = LegacyKoreanTextRepair.Repair(mail.Cc);
        TextPreview.Text = LegacyKoreanTextRepair.Repair(mail.TextBody);
    }

    private async void OpenSelectedAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedAttachment(out var attachment))
        {
            return;
        }

        try
        {
            await _attachmentService.OpenAttachmentAsync(_selectedMessage!, attachment.Index, attachment.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "첨부파일 열기 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveSelectedAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedAttachment(out var attachment))
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = attachment.FileName,
            Title = "선택한 첨부파일 저장"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await _attachmentService.SaveAttachmentAsync(_selectedMessage!, attachment.Index, dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "첨부파일 저장 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveAllAttachments_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMessage is null || AttachmentList.Items.Count == 0)
        {
            MessageBox.Show(this, "저장할 첨부파일이 없습니다.", "전체 첨부파일 저장",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "전체 첨부파일을 저장할 폴더 선택",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var savedPaths = await _attachmentService.SaveAllAttachmentsAsync(_selectedMessage, dialog.FolderName);
            MessageBox.Show(this,
                $"첨부파일 {savedPaths.Count:N0}개를 저장했습니다.\n{dialog.FolderName}",
                "전체 첨부파일 저장", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "전체 첨부파일 저장 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool TryGetSelectedAttachment(out AttachmentInfo attachment)
    {
        if (_selectedMessage is not null && AttachmentList.SelectedItem is AttachmentInfo selected)
        {
            attachment = selected;
            return true;
        }

        attachment = null!;
        MessageBox.Show(this, "첨부파일 목록에서 파일을 선택해 주세요.", "첨부파일 선택",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private sealed record IndexedMailText(
        long Id,
        string Subject,
        string Sender,
        string Recipients,
        string Cc,
        string Bcc,
        string TextBody,
        string HtmlBody,
        string AttachmentNames);
}
