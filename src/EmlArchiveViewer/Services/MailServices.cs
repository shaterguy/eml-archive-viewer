using System.Diagnostics;
using System.Net;
using System.Text;
using HtmlAgilityPack;
using MimeKit;
using EmlArchiveViewer.Models;

namespace EmlArchiveViewer.Services;

public static class LegacyKoreanTextRepair
{
    private static readonly Encoding Latin1Encoding;
    private static readonly Encoding Windows1252Encoding;
    private static readonly Encoding KoreanEncoding;
    private static readonly Encoding Utf8Encoding = new UTF8Encoding(false, true);

    static LegacyKoreanTextRepair()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Latin1Encoding = Encoding.GetEncoding(28591, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        Windows1252Encoding = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        KoreanEncoding = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static string Repair(string? value)
    {
        if (string.IsNullOrEmpty(value) || !LooksSuspicious(value))
        {
            return value ?? string.Empty;
        }

        var best = value;
        var bestScore = Score(value);
        foreach (var sourceEncoding in new[] { Latin1Encoding, Windows1252Encoding })
        {
            byte[] bytes;
            try
            {
                bytes = sourceEncoding.GetBytes(value);
            }
            catch (EncoderFallbackException)
            {
                continue;
            }

            foreach (var targetEncoding in new[] { KoreanEncoding, Utf8Encoding })
            {
                try
                {
                    var candidate = targetEncoding.GetString(bytes);
                    var candidateScore = Score(candidate);
                    if (CountHangul(candidate) >= 2 && candidateScore > bestScore + 8)
                    {
                        best = candidate;
                        bestScore = candidateScore;
                    }
                }
                catch (DecoderFallbackException)
                {
                }
            }
        }

        return best;
    }

    private static bool LooksSuspicious(string value)
    {
        var suspicious = value.Count(IsSuspiciousSingleByteCharacter);
        if (suspicious < 2)
        {
            return false;
        }

        var hangul = CountHangul(value);
        return hangul == 0 || suspicious > hangul * 2;
    }

    private static int Score(string value)
    {
        var hangul = CountHangul(value);
        var replacement = value.Count(character => character == '\uFFFD');
        var controls = value.Count(character => char.IsControl(character) &&
                                                  character is not ('\r' or '\n' or '\t'));
        var suspicious = value.Count(IsSuspiciousSingleByteCharacter);
        return hangul * 8 - replacement * 30 - controls * 12 - suspicious * 2;
    }

    private static int CountHangul(string value) =>
        value.Count(character => character is >= '\uAC00' and <= '\uD7A3' or
                                           >= '\u1100' and <= '\u11FF' or
                                           >= '\u3130' and <= '\u318F');

    private static bool IsSuspiciousSingleByteCharacter(char character) =>
        character is >= '\u0080' and <= '\u00FF' or
        '\u20AC' or '\u201A' or '\u0192' or '\u201E' or '\u2026' or '\u2020' or '\u2021' or
        '\u02C6' or '\u2030' or '\u0160' or '\u2039' or '\u0152' or '\u017D' or '\u2018' or
        '\u2019' or '\u201C' or '\u201D' or '\u2022' or '\u2013' or '\u2014' or '\u02DC' or
        '\u2122' or '\u0161' or '\u203A' or '\u0153' or '\u017E' or '\u0178';
}

public sealed class EmlParserService
{
    public async Task<MailRecord> ParseAsync(string rootPath, string filePath, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(filePath);
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var message = await MimeMessage.LoadAsync(stream, cancellationToken);
        var relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(rootPath, filePath)) ?? string.Empty;
        if (relativeDirectory == ".")
        {
            relativeDirectory = string.Empty;
        }

        var attachmentNames = message.Attachments
            .Select(GetAttachmentName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        return new MailRecord
        {
            FilePath = Path.GetFullPath(filePath),
            RootPath = Path.GetFullPath(rootPath),
            RelativeFolderPath = relativeDirectory,
            FileName = fileInfo.Name,
            FileSize = fileInfo.Length,
            ModifiedUtc = fileInfo.LastWriteTimeUtc,
            MessageId = message.MessageId ?? string.Empty,
            SentDate = message.Date == DateTimeOffset.MinValue ? null : message.Date,
            Subject = LegacyKoreanTextRepair.Repair(message.Subject),
            Sender = FormatAddresses(message.From),
            Recipients = FormatAddresses(message.To),
            Cc = FormatAddresses(message.Cc),
            Bcc = FormatAddresses(message.Bcc),
            TextBody = LegacyKoreanTextRepair.Repair(message.TextBody),
            HtmlBody = LegacyKoreanTextRepair.Repair(message.HtmlBody),
            AttachmentCount = attachmentNames.Count,
            AttachmentNames = string.Join(" | ", attachmentNames)
        };
    }

    public async Task<MimeMessage> LoadMessageAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await MimeMessage.LoadAsync(stream, cancellationToken);
    }

    public static List<AttachmentInfo> GetAttachments(MimeMessage message)
    {
        return message.Attachments.Select((entity, index) => new AttachmentInfo
        {
            Index = index,
            FileName = GetAttachmentName(entity),
            MimeType = entity.ContentType.MimeType,
            Size = 0
        }).ToList();
    }

    private static string FormatAddresses(InternetAddressList addresses) =>
        string.Join("; ", addresses.Mailboxes.Select(mailbox =>
        {
            var name = LegacyKoreanTextRepair.Repair(mailbox.Name);
            return string.IsNullOrWhiteSpace(name) ? mailbox.Address : $"{name} <{mailbox.Address}>";
        }));

    public static string GetAttachmentName(MimeEntity entity)
    {
        var fileName = entity switch
        {
            MimePart part => part.FileName ?? part.ContentDisposition?.FileName ?? "첨부파일",
            MessagePart messagePart => messagePart.ContentDisposition?.FileName ?? messagePart.ContentType.Name ?? "첨부메일.eml",
            _ => "첨부파일"
        };
        return LegacyKoreanTextRepair.Repair(fileName);
    }
}

public sealed class HtmlPreviewService
{
    private static readonly HashSet<string> DangerousElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "iframe", "object", "embed", "form", "input", "button", "textarea", "select", "link", "base"
    };

    public string BuildSafeHtml(MimeMessage message)
    {
        var html = LegacyKoreanTextRepair.Repair(message.HtmlBody);
        if (string.IsNullOrWhiteSpace(html))
        {
            html = $"<pre>{WebUtility.HtmlEncode(LegacyKoreanTextRepair.Repair(message.TextBody))}</pre>";
        }

        var document = new HtmlDocument();
        document.LoadHtml(html);
        foreach (var node in document.DocumentNode.Descendants().ToList())
        {
            if (DangerousElements.Contains(node.Name))
            {
                node.Remove();
                continue;
            }

            foreach (var attribute in node.Attributes.ToList())
            {
                if (attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(attribute.Name, "srcdoc", StringComparison.OrdinalIgnoreCase))
                {
                    node.Attributes.Remove(attribute);
                }
            }

            if (node.Name.Equals("img", StringComparison.OrdinalIgnoreCase))
            {
                var src = node.GetAttributeValue("src", string.Empty);
                if (src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
                {
                    var contentId = src[4..].Trim('<', '>');
                    var part = message.BodyParts.OfType<MimePart>()
                        .FirstOrDefault(item => string.Equals(item.ContentId?.Trim('<', '>'), contentId, StringComparison.OrdinalIgnoreCase));
                    if (part?.Content is not null)
                    {
                        using var memory = new MemoryStream();
                        part.Content.DecodeTo(memory);
                        node.SetAttributeValue("src", $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(memory.ToArray())}");
                    }
                    else
                    {
                        node.Remove();
                    }
                }
                else if (!src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    node.Remove();
                }
            }

            if (node.Name.Equals("a", StringComparison.OrdinalIgnoreCase))
            {
                var href = node.GetAttributeValue("href", string.Empty);
                node.Attributes.Remove("href");
                if (!string.IsNullOrWhiteSpace(href))
                {
                    node.SetAttributeValue("title", href);
                }
            }
        }

        return """
            <!doctype html>
            <html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'">
            <style>body{font-family:'Segoe UI',sans-serif;font-size:14px;margin:18px;color:#202020;line-height:1.45}img{max-width:100%;height:auto}table{border-collapse:collapse;max-width:100%}td,th{padding:3px}pre{white-space:pre-wrap;font-family:'Segoe UI',sans-serif}</style>
            </head><body>
            """ + document.DocumentNode.InnerHtml + "</body></html>";
    }
}

public sealed class AttachmentService
{
    public async Task SaveAttachmentAsync(MimeMessage message, int index, string outputPath, CancellationToken cancellationToken = default)
    {
        var attachment = message.Attachments.ElementAt(index);
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
        switch (attachment)
        {
            case MimePart { Content: not null } part:
                await part.Content.DecodeToAsync(output, cancellationToken);
                break;
            case MessagePart { Message: not null } messagePart:
                await messagePart.Message.WriteToAsync(output, cancellationToken);
                break;
            default:
                await attachment.WriteToAsync(output, cancellationToken);
                break;
        }
    }

    public async Task<IReadOnlyList<string>> SaveAllAttachmentsAsync(MimeMessage message, string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var attachments = message.Attachments.ToList();
        var savedPaths = new List<string>(attachments.Count);
        for (var index = 0; index < attachments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = MakeSafeFileName(EmlParserService.GetAttachmentName(attachments[index]), index + 1);
            var outputPath = GetUniquePath(outputDirectory, fileName);
            await SaveAttachmentAsync(message, index, outputPath, cancellationToken);
            savedPaths.Add(outputPath);
        }
        return savedPaths;
    }

    public async Task OpenAttachmentAsync(MimeMessage message, int index, string fileName, CancellationToken cancellationToken = default)
    {
        var safeName = MakeSafeFileName(fileName, index + 1);
        var folder = Path.Combine(AppPaths.CacheDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var outputPath = Path.Combine(folder, safeName);
        await SaveAttachmentAsync(message, index, outputPath, cancellationToken);
        Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true });
    }

    private static string MakeSafeFileName(string fileName, int fallbackIndex)
    {
        var safeName = string.Concat(fileName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        return string.IsNullOrWhiteSpace(safeName) ? $"첨부파일_{fallbackIndex}" : safeName;
    }

    private static string GetUniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; ; suffix++)
        {
            candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
