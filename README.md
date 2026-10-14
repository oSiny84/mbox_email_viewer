# MailViewer

**English** | [한국어](README.ko.md)

A Windows desktop app that converts a Gmail backup (mbox) or CSV into a SQLite database (`mails.db`), with full-text search, conversation threading, attachments, HTML rendering, and incremental Gmail sync.

## Getting your Gmail backup

1. Go to https://takeout.google.com ("Google Takeout")
2. Select only **Mail** → Export → unzip the download to get a `.mbox` file
3. Run MailViewer → **[메일 가져오기]** (Import) → select the `.mbox` file

## Run

```
MailViewer\bin\Release\net9.0-windows\MailViewer.exe
```

If `mails.db` is missing (or has an outdated schema), a file picker opens so you can import right away. Both mbox and CSV are supported.

## Features

- **Search**: type keywords and press Enter. Space = AND. Scope: All / Subject / From / To / Body / CC
- **FTS5 full-text search**: terms of 3+ characters use a trigram index (~25x faster than LIKE); shorter terms automatically fall back to LIKE
- **Thread view** ("스레드로 묶기"): groups mail into conversations using Gmail's thread ID (X-GM-THRID); CSV imports fall back to normalized subjects (Re:/Fwd: stripped). Click a thread to read the whole conversation in chronological order; double-clicking a mail in flat view also opens its thread
- **Attachments-only filter** ("첨부만"); the 📎 column shows attachment counts
- **Save attachments**: select a mail → 📎 buttons appear at the top of the detail pane → click to save anywhere
- **HTML tab** ("HTML 원본"): renders HTML mail as it was designed, inline (cid:) images included — via WebView2 with scripts stripped/disabled and all external requests blocked (kills tracking pixels)
- **Date range filter**, **sender statistics** (double-click a row to search by that sender), **Excel export** (all mails matching the current search)
- **Gmail sync**: incrementally downloads mail that arrived after your mbox backup, over IMAP
  - Configure via the **⚙** button with one of two sign-in methods:
    - **Google sign-in (OAuth)**: sign in with your Google account in the browser. No 2-Step Verification or app password needed, so it also works for company (Google Workspace) accounts where app passwords are unavailable. One-time setup: in console.cloud.google.com create a project, enable the Gmail API, configure the OAuth consent screen ("Internal" for Workspace if available), and create an OAuth client ID of type **Desktop app**. Enter the client ID/secret in ⚙ (or load the downloaded JSON) and click **Sign in with Google**. The app checks the IMAP login right away.
    - **App password**: enable 2-Step Verification, then create one at myaccount.google.com/apppasswords
  - OAuth notes: an "External" consent screen left in *Testing* status expires the sign-in after 7 days (sync then asks you to sign in again). "Internal" or published apps don't expire. If your Workspace admin restricts third-party app access, the client ID must be added as a trusted app
  - The app password, OAuth client secret, and refresh token are encrypted with Windows DPAPI and stored locally only
  - New mail is appended to `mails.sync.mbox`, so attachments/HTML viewing works identically
  - Deduplicated by Message-ID (safe even where the Takeout backup overlaps); joins existing threads via Gmail thread IDs
  - For scheduled sync, register `MailViewer.exe --sync mails.db` in Windows Task Scheduler

## Architecture

- The DB stores only searchable text and metadata — even a multi-GB mbox yields a DB proportional to text size only
- HTML bodies and attachments are **not** copied into the DB; only their byte offsets (`mbox_offset`/`mbox_length`) are recorded, and messages are parsed on demand with MimeKit
- Therefore **do not delete or move the original mbox file** — search and text bodies keep working without it, but attachment/HTML viewing will not
- Legacy Korean encodings (EUC-KR / ks_c_5601-1987) are registered at startup so older Korean mail decodes correctly

## DB schema (`mails.db`, v3)

```sql
CREATE TABLE mails (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    date TEXT,           -- "yyyy-MM-dd HH:mm"
    from_full TEXT, from_name TEXT, from_email TEXT,
    to_full TEXT, cc TEXT,
    subject TEXT,
    thread_key TEXT,     -- "gm:<X-GM-THRID>" or normalized subject
    labels TEXT,         -- X-Gmail-Labels
    body TEXT,           -- plain text for search/preview
    has_html INTEGER, att_count INTEGER, att_names TEXT,
    mbox_offset INTEGER, mbox_length INTEGER,  -- NULL for CSV imports
    message_id TEXT,     -- for sync deduplication
    mbox_file INTEGER    -- 0 = original mbox, 1 = sync mbox
);
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);  -- mbox_path, sync state, account
-- mails_fts: FTS5 trigram full-text index
```

The DB is plain SQLite — you can query it from Python, DB Browser for SQLite, or any other tool.

## Headless (command-line) modes

```
MailViewer.exe --import "backup.mbox" "mails.db"
MailViewer.exe --export "mails.db" "result.xlsx" [keyword]
MailViewer.exe --dump "mails.db" <mail-id> [out.txt]
MailViewer.exe --sync "mails.db"          (account must be configured in the GUI first)
MailViewer.exe --diag-oauth <address> <access-token> <out.txt>   (test IMAP XOAUTH2 login with a token, e.g. from the OAuth Playground)
```

## Build

Requires .NET 9 SDK on Windows.

```
dotnet build MailViewer/MailViewer.csproj -c Release
```

Key dependencies: Microsoft.Data.Sqlite, MimeKit, MailKit, ClosedXML, Microsoft.Web.WebView2.
