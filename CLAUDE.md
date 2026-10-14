# CLAUDE.md — 프로젝트 인수인계 노트

Gmail 백업(mbox)/CSV를 SQLite(`mails.db`)로 변환해 검색·열람하는 Windows WinForms 앱 (.NET 9).
사용자 대상 문서는 [README.md](README.md) / [README.ko.md](README.ko.md) 참고 — 기능·CLI·스키마 설명이 정확하게 유지되고 있으니 기능을 바꾸면 두 README도 같이 갱신할 것.

## 빌드 / 실행

```
dotnet build MailViewer/MailViewer.csproj -c Release
MailViewer\bin\Release\net9.0-windows\MailViewer.exe
```

CI: `.github/workflows/build.yml` — 수동 실행(workflow_dispatch) 전용. Windows 러너에서 런타임 미포함/포함 zip을 Artifacts로 업로드, `release` 체크 + `version`(vX.Y.Z) 입력 시 main에서만 Release 생성. 사용자가 Artifacts로 테스트한 뒤 직접 릴리스하는 흐름이므로 push 자동 실행/자동 릴리스로 바꾸지 말 것.
Linux(클라우드 세션)에서도 `-p:EnableWindowsTargeting=true`로 빌드 가능.

의존성: Microsoft.Data.Sqlite, MimeKit, MailKit, ClosedXML, Microsoft.Web.WebView2.
루트의 `mails.db`(약 400MB)와 zip은 실사용 데이터/배포물 — 커밋 금지 (.gitignore 확인).

## 핵심 설계 (반드시 유지할 불변조건)

- **DB에는 검색용 텍스트/메타데이터만 저장.** HTML 본문·첨부는 DB에 없고 `mbox_file`(→ `mbox_files.id`)/`mbox_offset`/`mbox_length`로 원본 mbox에서 MimeKit으로 온디맨드 파싱 (`MessageLoader.cs`, 슬라이스를 Mbox 모드로 파싱하므로 각 레코드는 `From ` 줄로 시작해야 함). 원본 mbox 파일이 없으면 첨부/HTML 열람 불가.
- **mbox 보관 구조** (`MboxStore.cs`): `mbox_files` 테이블에 원본 파일 목록, 경로는 DB 폴더 아래면 상대 경로(폴더째 이동 가능). 보관 폴더(meta `archive_dir`, 기본 `mbox`)의 연도별 `mails-YYYY.mbox` — 연도는 DB `date` 앞 4자리(이상하면 `unknown`). 동기화는 해당 연도 파일에 append(X-GM-THRID/X-Gmail-Labels 헤더 주입, mboxrd `From ` 이스케이프). [연도별 정리](`SplitByYear`)는 원본을 건드리지 않고 연도 파일로 복사 후 DB 위치 갱신, 실패 시 파일 길이 롤백 + DB 롤백, 재실행 시 이미 정리된 행은 건너뜀. 가져오기는 여러 파일 + Message-ID 중복 제거.
- **스키마 버전 = `DbSchema.Version` (현재 6).** v5→v6은 `mbox_files`에 id 0=meta `mbox_path`, 1=meta `sync_mbox_path`로 등록해 기존 행을 그대로 유지. 스키마 변경 시: `Create()` 수정 + `Migrate()`에 무손실 단계 마이그레이션 추가 + 버전 증가. v0/v1은 재가져오기 대상.
- **재가져오기(reimport)는 테이블을 DROP하지만** `gmail_user`/`gmail_pass_enc`(meta), `deleted_keys`(휴지통), `starred_keys`(별표)는 보존 후 복원 (`DbSchema.Create` → `ApplyTombstones`). 새 영속 상태를 추가하면 같은 패턴으로 보존할 것.
- **메일 식별 키**: Message-ID 우선, 없으면(CSV) `date|from_email|subject` — `DbSchema.IdentityKeyExpr`. 삭제/별표 기록이 이 키 기준.
- **FTS5 trigram** 인덱스(`mails_fts`): 3글자 이상 검색어만 FTS, 짧으면 LIKE 폴백 (`MailDb.cs`).
- **스레드**: Gmail `X-GM-THRID`(`gm:` 접두) 우선, CSV는 Re:/Fwd: 제거한 정규화 제목.
- **HTML 렌더링**(WebView2): 스크립트 제거/비활성 + 외부 요청 전면 차단(추적 픽셀 방지), cid: 인라인 이미지만 허용. 이 보안 정책 완화하지 말 것.
- **Gmail 동기화**(IMAP, `GmailSync.cs`): 인증 2방식 — 앱 비밀번호 또는 Google OAuth(`GoogleOAuth.cs`, 루프백 TcpListener + PKCE, `https://mail.google.com/ openid email` 범위, 2단계 인증 불필요). meta `gmail_auth`=`oauth`|`password`(없으면 password). 비밀값(앱 비밀번호, OAuth client secret, refresh token)은 DPAPI 암호화해 meta에 저장. OAuth는 동기화마다 refresh→access 토큰 교환 후 XOAUTH2. 계정 meta 키 목록은 `GmailSync.AccountMetaKeys` 한 곳 — 재가져오기 보존(`DbSchema.Create`)이 이 목록을 씀. 새 메일은 보관 폴더의 연도별 mbox에 append (예전 버전은 `mails.sync.mbox`). Message-ID로 중복 제거.
- **EUC-KR/ks_c_5601-1987** 인코딩을 시작 시 등록 (한국어 구메일 대응) — `Program.cs`.
- **i18n**: 모든 UI 문자열은 `L.cs` (EN 기본/KO). 하드코딩 문자열 넣지 말 것.
- 삭제는 뷰어 전용 소프트 삭제(`deleted=1`) — Gmail 원본에는 영향 없음.

## 파일 안내

| 파일 | 역할 |
|---|---|
| `MainForm.cs` | 메인 UI (목록/검색/스레드/상세) |
| `MailDb.cs` | 검색 쿼리, FTS/LIKE 분기 |
| `DbSchema.cs` | 스키마, 마이그레이션, tombstone 보존 |
| `MboxImporter.cs` / `CsvImporter.cs` | 가져오기 (오프셋 기록, mbox는 여러 파일) |
| `MboxStore.cs` | mbox 파일 목록·상대 경로, 연도별 파일, 레코드 쓰기(Appender), 연도별 정리 |
| `MessageLoader.cs` | 오프셋으로 mbox에서 메시지 파싱 (TNEF/winmail.dat 언팩 포함) |
| `GmailSync.cs` | IMAP 증분 동기화, `--sync` CLI, 계정 저장(앱 비밀번호/OAuth) |
| `GoogleOAuth.cs` | Google OAuth 설치형 앱 흐름(브라우저 로그인, 토큰 교환/갱신), WinForms 비의존 |
| `SettingsForm.cs` | 언어 + 동기화 계정 설정 (OAuth 로그인 / 앱 비밀번호) |
| `L.cs` | i18n 문자열 테이블 |
| `Program.cs` | 진입점, CLI 모드(--import/--export/--dump/--sync/--split-by-year/--diag-sync/--diag-oauth), 인코딩 등록 |

## 상태 (2026-09 기준)

v1.0.1 배포 완료(`publish/`, zip). 2026-10: Gmail 동기화에 Google OAuth 로그인 추가(회사 Workspace 계정에서 앱 비밀번호 불가 대응) — Windows 실사용 확인 완료(회사 계정 OAuth 로그인 → 증분 동기화). Linux에서는 `-p:EnableWindowsTargeting=true`로 빌드 가능. 참고: "내부" 동의 화면은 생성 직후 잠시 `org_internal` 오류가 날 수 있음(반영 지연), 다시 시도하면 됨.
