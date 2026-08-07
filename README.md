# EML Archive Viewer

Offline Windows desktop application for indexing, searching, and viewing EML email archives.

## Current release

- Version: `0.1.10`
- Release date: `2026-07-29`
- Release channel: `stable`
- Platform: Windows x64
- Distribution: self-contained single executable
- Network: no internet connection required

## Main features

- Register multiple folders containing EML files.
- Preserve and display their subfolder hierarchy.
- Search all registered folders or the selected folder and descendants.
- Search subject, body, sender, recipients, CC, attachment names, and date ranges.
- Outlook-style folder tree, message list, and large preview pane.
- Real-time file creation, update, rename, and deletion monitoring.
- Start with Windows in the notification area.
- Close button hides the window instead of terminating the process.
- Restore the hidden window by clicking the notification-area icon.
- Store a separate message-list column order for each folder.
- Preserve user-adjusted message-list column widths across restarts.

## v0.1.10 column width persistence

- saves each message-list column width and width unit in the existing local settings file;
- restores pixel, star, auto, size-to-cells, and size-to-header widths after the window is rendered;
- persists current widths when the window is hidden or the application exits;
- remains compatible with settings files created before column-width storage existed;
- prevents a background-only Windows startup session from overwriting saved widths with XAML defaults;
- includes regression coverage for legacy settings migration and width save/reload behavior.

## v0.1.9 search whitespace semantics

- treats a compact query such as `손해보험협회` as an ordered phrase while ignoring whitespace in indexed mail text;
- therefore matches `손해 보험 협회` but excludes reversed text such as `협회 손해보험`;
- treats a space-separated query such as `손해 보험 협회` as an unordered AND search requiring all three terms;
- allows the required terms to appear in different searchable fields while preserving the existing search scope and filters;
- includes regression coverage for ordered compact phrases, reversed phrases, unordered terms, and cross-field matches.

## v0.1.8 notification-area icon initialization

- fixes the notification-area icon remaining on the Windows default application icon;
- retries icon assignment until the notification-area `NotifyIcon` has actually been created;
- loads the icon embedded in the published executable and keeps the native icon handle alive for the application lifetime;
- retains the default Windows icon only as a fallback when executable icon extraction fails.

## v0.1.7 compact address preview and application icon

- keeps sender, recipient, CC, and source-path metadata to a compact single line so very large recipient lists cannot push the message body off screen;
- adds `전체 보기` dialogs for recipients and CC, showing one address per line in a resizable, copyable window;
- applies a dedicated EML archive/search icon to the executable, taskbar window, and notification-area icon;
- generates the multi-size Windows ICO deterministically during the build;
- publishes the verified Windows executable and SHA-256 checksum as GitHub Release assets when the release branch is merged to `main`.

## v0.1.6 Korean encoding and attachment workflow

- detects common Korean CP949/EUC-KR text that was decoded as a Western single-byte charset and restores readable Korean in subjects, address names, text bodies, HTML bodies, and attachment names;
- repairs already indexed mail text in place once, then rebuilds the full-text index without modifying the source EML files;
- labels attachment commands as `선택 파일 열기`, `선택 파일 저장`, and `전체 첨부파일 저장`;
- selects the first attachment by default and shows a clear message when no attachment is selected;
- saves all attachments into a selected folder and preserves every file by adding a numeric suffix when names collide.

## v0.1.5 window lifetime fix

The previous `Closing` event handler awaited settings persistence before setting `CancelEventArgs.Cancel`. An asynchronous event handler returns to WPF at its first incomplete `await`, so the window could finish closing before the handler attempted to cancel and hide it. The process continued in the notification area, but every restore path referenced a WPF window instance that had already been closed. Version 0.1.5:

- replaces the asynchronous closing handler with a synchronous lifetime handler;
- sets `e.Cancel = true` before the close event can return;
- hides the still-live window immediately and persists settings afterward;
- serializes concurrent settings writes;
- saves settings synchronously during an explicit application exit.

## v0.1.4 notification-area dispatcher fix

- marshals notification-area open and exit commands onto the WPF dispatcher;
- restores the hidden window with a single left click;
- removes the duplicate double-click activation handler;
- catches and logs Windows Forms notification-area exceptions.

## v0.1.1 crash fix

The first implementation incorrectly maintained an FTS5 external-content index. Indexing the first EML could cause the error-recording path to fail again and terminate the WPF application. Version 0.1.1:

- uses an independent FTS5 index with serialized SQLite writes;
- migrates or quarantines an incompatible local index automatically;
- shows the main window before background indexing begins;
- contains indexing and watcher exceptions instead of terminating the app;
- records failures in `%LOCALAPPDATA%\EMLArchiveViewer\logs\app.log`;
- includes a Windows runtime smoke test for first insert, full-text search, update, and deletion.

## Validation

The GitHub Actions Windows build must pass all of the following before producing the executable artifact:

1. dependency restore;
2. deterministic multi-size application icon generation;
3. Release compilation with no errors;
4. SQLite runtime smoke test, settings migration and column-width persistence tests, search semantics test, Korean encoding repair test, and attachment save-all test;
5. self-contained single-file publish;
6. verification that exactly one `EMLArchiveViewer.exe` was produced;
7. SHA-256 checksum generation and GitHub Release asset publication on `main`.
