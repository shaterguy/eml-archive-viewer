using System.Text;
using EmlArchiveViewer.Models;
using EmlArchiveViewer.Services;
using MimeKit;

var dataDirectory = Path.Combine(Path.GetTempPath(), "EMLArchiveViewer-Smoke-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("EML_ARCHIVE_VIEWER_DATA_DIR", dataDirectory);
Directory.CreateDirectory(dataDirectory);

try
{
    var settingsService = new SettingsService();
    await File.WriteAllTextAsync(AppPaths.SettingsPath, """
        {
          "WindowWidth": 1280,
          "WindowHeight": 720
        }
        """);

    var migratedSettings = await settingsService.LoadAsync();
    if (migratedSettings.ColumnWidths is null || migratedSettings.ColumnWidths.Count != 0)
    {
        throw new InvalidOperationException("기존 설정 파일에서 열 너비 기본값을 초기화하지 못했습니다.");
    }

    migratedSettings.ColumnWidths["Subject"] = new ColumnWidthSetting
    {
        Value = 420.5,
        UnitType = "Pixel"
    };
    migratedSettings.ColumnWidths["Sender"] = new ColumnWidthSetting
    {
        Value = 1.5,
        UnitType = "Star"
    };
    await settingsService.SaveAsync(migratedSettings);

    var reloadedSettings = await settingsService.LoadAsync();
    if (!reloadedSettings.ColumnWidths.TryGetValue("Subject", out var subjectWidth) ||
        subjectWidth.Value != 420.5 ||
        !string.Equals(subjectWidth.UnitType, "Pixel", StringComparison.Ordinal) ||
        !reloadedSettings.ColumnWidths.TryGetValue("Sender", out var senderWidth) ||
        senderWidth.Value != 1.5 ||
        !string.Equals(senderWidth.UnitType, "Star", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("열 너비 설정 저장 또는 재로드 결과가 올바르지 않습니다.");
    }

    var database = new DatabaseService();
    await DatabaseRecoveryService.InitializeAsync(database);

    var accountingFolder = Path.Combine(dataDirectory, "자료", "2026");
    var generalFolder = Path.Combine(dataDirectory, "일반");
    var searchFolder = Path.Combine(dataDirectory, "검색");
    Directory.CreateDirectory(accountingFolder);
    Directory.CreateDirectory(generalFolder);
    Directory.CreateDirectory(searchFolder);

    var emlPath = Path.Combine(accountingFolder, "sample.eml");
    await File.WriteAllTextAsync(emlPath, "sample");
    var mail = new MailRecord
    {
        FilePath = emlPath,
        RootPath = dataDirectory,
        RelativeFolderPath = Path.Combine("자료", "2026"),
        FileName = "sample.eml",
        FileSize = new FileInfo(emlPath).Length,
        ModifiedUtc = File.GetLastWriteTimeUtc(emlPath),
        MessageId = "smoke-test@example.test",
        SentDate = DateTimeOffset.Now,
        Subject = "샘플 메일 검색 테스트",
        Sender = "sender@example.test",
        Recipients = "recipient@example.test",
        TextBody = "첫 번째 EML 저장 시 FTS 색인이 정상적으로 생성되어야 합니다.",
        AttachmentCount = 1,
        AttachmentNames = "report.pdf"
    };

    var otherEmlPath = Path.Combine(generalFolder, "other.eml");
    await File.WriteAllTextAsync(otherEmlPath, "other");
    var otherMail = new MailRecord
    {
        FilePath = otherEmlPath,
        RootPath = dataDirectory,
        RelativeFolderPath = "일반",
        FileName = "other.eml",
        FileSize = new FileInfo(otherEmlPath).Length,
        ModifiedUtc = File.GetLastWriteTimeUtc(otherEmlPath),
        MessageId = "other@example.test",
        SentDate = DateTimeOffset.Now.AddMinutes(-1),
        Subject = "일반 업무 메일",
        Sender = "other@example.test",
        Recipients = "recipient@example.test",
        TextBody = "폴더 범위 검색에서 제외되어야 하는 메일입니다."
    };

    var orderedSearchPath = Path.Combine(searchFolder, "ordered.eml");
    await File.WriteAllTextAsync(orderedSearchPath, "ordered");
    var orderedSearchMail = new MailRecord
    {
        FilePath = orderedSearchPath,
        RootPath = dataDirectory,
        RelativeFolderPath = "검색",
        FileName = "ordered.eml",
        FileSize = new FileInfo(orderedSearchPath).Length,
        ModifiedUtc = File.GetLastWriteTimeUtc(orderedSearchPath),
        MessageId = "ordered@example.test",
        SentDate = DateTimeOffset.Now.AddMinutes(-2),
        Subject = "서울 중앙 도서관 업무 안내",
        Sender = "ordered@example.test",
        Recipients = "recipient@example.test",
        TextBody = "공백을 무시한 순서 일치 검색 대상입니다."
    };

    var reversedSearchPath = Path.Combine(searchFolder, "reversed.eml");
    await File.WriteAllTextAsync(reversedSearchPath, "reversed");
    var reversedSearchMail = new MailRecord
    {
        FilePath = reversedSearchPath,
        RootPath = dataDirectory,
        RelativeFolderPath = "검색",
        FileName = "reversed.eml",
        FileSize = new FileInfo(reversedSearchPath).Length,
        ModifiedUtc = File.GetLastWriteTimeUtc(reversedSearchPath),
        MessageId = "reversed@example.test",
        SentDate = DateTimeOffset.Now.AddMinutes(-3),
        Subject = "도서관 서울중앙 업무 안내",
        Sender = "reversed@example.test",
        Recipients = "recipient@example.test",
        TextBody = "순서가 반대인 검색 제외 대상입니다."
    };

    var distributedSearchPath = Path.Combine(searchFolder, "distributed.eml");
    await File.WriteAllTextAsync(distributedSearchPath, "distributed");
    var distributedSearchMail = new MailRecord
    {
        FilePath = distributedSearchPath,
        RootPath = dataDirectory,
        RelativeFolderPath = "검색",
        FileName = "distributed.eml",
        FileSize = new FileInfo(distributedSearchPath).Length,
        ModifiedUtc = File.GetLastWriteTimeUtc(distributedSearchPath),
        MessageId = "distributed@example.test",
        SentDate = DateTimeOffset.Now.AddMinutes(-4),
        Subject = "도서관 공지",
        Sender = "서울 담당자 <city@example.test>",
        Recipients = "recipient@example.test",
        TextBody = "중앙 관련 업무 안내입니다."
    };

    await database.UpsertAsync(mail);
    await database.UpsertAsync(otherMail);
    await database.UpsertAsync(orderedSearchMail);
    await database.UpsertAsync(reversedSearchMail);
    await database.UpsertAsync(distributedSearchMail);

    var subjectResults = await database.SearchAsync(new SearchCriteria { QuickText = "샘플" });
    if (subjectResults.Count != 1 || subjectResults[0].Subject != mail.Subject)
    {
        throw new InvalidOperationException("첫 EML 검색 결과가 올바르지 않습니다.");
    }

    var compactOrderedResults = await database.SearchAsync(new SearchCriteria
    {
        QuickText = "서울중앙도서관"
    });
    if (compactOrderedResults.Count != 1 ||
        !string.Equals(compactOrderedResults[0].FilePath, orderedSearchPath, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("공백 없는 검색어의 공백 무시 순서 일치 결과가 올바르지 않습니다.");
    }

    var allTermsResults = await database.SearchAsync(new SearchCriteria
    {
        QuickText = "서울 중앙 도서관"
    });
    var expectedAllTermPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        orderedSearchPath,
        reversedSearchPath,
        distributedSearchPath
    };
    if (allTermsResults.Count != expectedAllTermPaths.Count ||
        allTermsResults.Any(result => !expectedAllTermPaths.Contains(result.FilePath)))
    {
        throw new InvalidOperationException("공백 분리 검색어의 순서 무관 AND 검색 결과가 올바르지 않습니다.");
    }

    var reorderedTermsResults = await database.SearchAsync(new SearchCriteria
    {
        QuickText = "도서관 서울 중앙"
    });
    if (reorderedTermsResults.Count != expectedAllTermPaths.Count ||
        reorderedTermsResults.Any(result => !expectedAllTermPaths.Contains(result.FilePath)))
    {
        throw new InvalidOperationException("공백 분리 검색어의 입력 순서 변경 결과가 올바르지 않습니다.");
    }

    var folderResults = await database.SearchAsync(new SearchCriteria
    {
        Scope = SearchScope.SelectedFolderAndDescendants,
        SelectedRootPath = dataDirectory,
        SelectedRelativeFolderPath = "자료"
    });
    if (folderResults.Count != 1 || !string.Equals(folderResults[0].FilePath, emlPath, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("현재 폴더 및 하위 폴더 검색 범위가 올바르지 않습니다.");
    }

    var attachmentResults = await database.SearchAsync(new SearchCriteria
    {
        AttachmentName = "report.pdf",
        HasAttachment = true
    });
    if (attachmentResults.Count != 1)
    {
        throw new InvalidOperationException("첨부파일 조건 검색 결과가 올바르지 않습니다.");
    }

    mail.Subject = "수정된 제목";
    mail.TextBody = "본문 수정 후 이전 검색어가 제거되고 새 검색어가 저장되어야 합니다.";
    await database.UpsertAsync(mail);

    var oldResults = await database.SearchAsync(new SearchCriteria { QuickText = "샘플" });
    var newResults = await database.SearchAsync(new SearchCriteria { QuickText = "이전 검색어" });
    if (oldResults.Count != 0 || newResults.Count != 1)
    {
        throw new InvalidOperationException("검색 색인 갱신 결과가 올바르지 않습니다.");
    }

    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var cp949 = Encoding.GetEncoding(949);
    const string koreanText = "안녕하세요. 한글 인코딩 복원 테스트입니다.";
    var mojibake = Encoding.Latin1.GetString(cp949.GetBytes(koreanText));
    var repairedText = LegacyKoreanTextRepair.Repair(mojibake);
    if (!string.Equals(repairedText, koreanText, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("CP949 한글 깨짐 복원 결과가 올바르지 않습니다.");
    }

    var repairedHtml = LegacyKoreanTextRepair.Repair($"<p>{mojibake}</p>");
    if (!string.Equals(repairedHtml, $"<p>{koreanText}</p>", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("HTML 본문의 CP949 한글 깨짐 복원 결과가 올바르지 않습니다.");
    }

    var attachmentMessage = new MimeMessage();
    var multipart = new Multipart("mixed")
    {
        new TextPart("plain") { Text = "attachment smoke test" },
        new MimePart("application", "octet-stream")
        {
            FileName = "동일이름.txt",
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes("first")))
        },
        new MimePart("application", "octet-stream")
        {
            FileName = "동일이름.txt",
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes("second")))
        }
    };
    attachmentMessage.Body = multipart;

    var attachmentDirectory = Path.Combine(dataDirectory, "saved-attachments");
    var savedAttachments = await new AttachmentService()
        .SaveAllAttachmentsAsync(attachmentMessage, attachmentDirectory);
    if (savedAttachments.Count != 2 ||
        savedAttachments.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2 ||
        savedAttachments.Any(path => !File.Exists(path)))
    {
        throw new InvalidOperationException("전체 첨부파일 저장 또는 중복 파일명 처리 결과가 올바르지 않습니다.");
    }

    await database.DeleteByPathAsync(emlPath);
    await database.DeleteByPathAsync(otherEmlPath);
    await database.DeleteByPathAsync(orderedSearchPath);
    await database.DeleteByPathAsync(reversedSearchPath);
    await database.DeleteByPathAsync(distributedSearchPath);
    var afterDelete = await database.SearchAsync(new SearchCriteria());
    if (afterDelete.Count != 0)
    {
        throw new InvalidOperationException("메일 삭제 후 색인이 제거되지 않았습니다.");
    }

    Console.WriteLine("SMOKE_TEST_OK: settings layout, database, search semantics, Korean encoding repair, selected/all attachment save");
}
finally
{
    try
    {
        Directory.Delete(dataDirectory, true);
    }
    catch
    {
        // 테스트 성공 여부와 무관한 임시 폴더 정리 실패는 무시한다.
    }
}
