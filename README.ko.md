# 메일 뷰어 (MailViewer)

[English](README.md) | **한국어**

Gmail 백업(mbox) 또는 CSV를 SQLite DB(`mails.db`)로 변환하고, 전문검색·스레드·첨부파일·HTML 보기·증분 동기화까지 지원하는 Windows 프로그램입니다.

## Gmail 백업 받는 법

1. https://takeout.google.com 접속 → "Google 데이터 내보내기"
2. **Gmail만** 선택 → 내보내기 → 받은 압축을 풀면 `.mbox` 파일이 나옴
3. MailViewer 실행 → [메일 가져오기] → 그 `.mbox` 선택

## 실행

```
MailViewer\bin\Release\net9.0-windows\MailViewer.exe
```

`mails.db`가 없으면(또는 구버전 형식이면) 가져올 파일 선택 창이 뜹니다. mbox와 CSV 모두 지원합니다.

## 기능

- **검색**: 검색어 입력 후 Enter. 공백 = AND. 범위: 전체 / 제목 / 보낸사람 / 받는사람 / 본문 / CC
- **FTS5 전문검색**: 3글자 이상 검색어는 trigram 인덱스 (LIKE 대비 ~25배), 2글자 이하는 자동 LIKE
- **스레드로 묶기**: Gmail 스레드 ID(X-GM-THRID) 기준으로 대화 묶기 (CSV는 Re:/Fwd: 제거 제목 기준). 스레드 클릭 → 전체 대화 시간순 표시. 일반 모드에서 메일 더블클릭해도 스레드 보기
- **첨부만**: 첨부파일 있는 메일만 필터. 목록의 📎 열은 첨부 개수
- **첨부파일 저장**: 메일 선택 시 상단에 📎 버튼들 표시 → 클릭해서 원하는 위치에 저장
- **HTML 원본 탭**: HTML 메일을 인라인 이미지(cid) 포함해 실제 모양으로 렌더링 (WebView2, 스크립트/외부 접속 차단 — 추적 픽셀 무력화)
- **기간 필터**, **발신자 통계**(행 더블클릭 = 그 발신자로 검색), **엑셀 내보내기**(현재 검색 결과 전체)
- **Gmail 동기화**: mbox 백업 이후 새로 온 메일을 IMAP으로 증분 다운로드해 추가
  - ⚙ 버튼에서 계정 설정. 로그인 방식 두 가지:
    - **Google 로그인 (OAuth)**: 브라우저에서 Google 계정으로 로그인. 2단계 인증·앱 비밀번호가 필요 없어서 앱 비밀번호를 못 쓰는 회사(Google Workspace) 계정에서도 동작합니다. 최초 1회 console.cloud.google.com 에서 프로젝트 생성 → Gmail API 사용 설정 → OAuth 동의 화면(Workspace면 가능하면 "내부") → OAuth 클라이언트 ID 생성(유형 **데스크톱 앱**). ⚙에 클라이언트 ID/보안 비밀을 입력(또는 받은 JSON 불러오기)하고 **Google로 로그인** 클릭. 로그인 직후 IMAP 접속까지 바로 확인합니다.
    - **앱 비밀번호**: 2단계 인증 켜고 myaccount.google.com/apppasswords 에서 발급
  - OAuth 참고: "외부" 동의 화면을 *테스트* 상태로 두면 7일마다 로그인이 만료됩니다(동기화 때 다시 로그인 안내). "내부"이거나 게시된 앱은 만료되지 않습니다. 관리자가 외부 앱 접근을 제한한 Workspace라면 클라이언트 ID를 신뢰 앱으로 등록해야 합니다
  - 앱 비밀번호·OAuth 보안 비밀·리프레시 토큰은 Windows DPAPI로 암호화되어 이 PC에만 저장
  - 새 메일은 보관 폴더의 연도별 mbox(`mails-2026.mbox` 등)에 원본 저장 → 첨부/HTML 보기 동일하게 동작. Gmail 스레드 ID·라벨도 헤더로 같이 저장되어, 나중에 이 파일만 다시 가져와도 유지됩니다
  - Message-ID로 중복 방지 (Takeout mbox와 겹쳐도 안전), Gmail 스레드 ID로 기존 스레드에 합류
  - 자동 동기화: 작업 스케줄러에 `MailViewer.exe --sync mails.db` 등록하면 주기 실행 가능

- **연도별 정리**: [연도별 정리] 버튼으로 모든 메일 원본(Takeout mbox, 예전 `mails.sync.mbox`)을 보관 폴더의 연도별 파일로 옮겨 정리
  - 보관 폴더 기본값은 `mails.db` 옆 `mbox` 폴더. 연도 = 메일 날짜 기준 (`mails-2019.mbox`, `mails-2020.mbox` …, 날짜가 이상하면 `mails-unknown.mbox`)
  - 기존 파일은 지우거나 바꾸지 않습니다. 정리가 끝나면 더 이상 쓰지 않는 파일 목록을 알려 주니 확인 후 직접 지우면 됩니다
  - 옮길 용량만큼 빈 디스크 공간이 필요하고, 도중에 실패하면 모두 되돌립니다. 다시 실행하면 이미 정리된 메일은 건너뜁니다
  - 백업·재설치: `mails.db`와 `mbox` 폴더를 통째로 복사하면 그대로 열립니다. DB가 없어도 [메일 가져오기]에서 연도별 파일을 모두 선택하면 됩니다

- **백업**: [백업] 버튼으로 `mails.db`와 메일 원본(보관 폴더의 mbox)을 지정한 폴더(예: OneDrive 안의 폴더)로 복사
  - 처음 누를 때 백업 폴더를 고르고, 이후에는 같은 곳에 백업합니다 (확인 창에서 [아니요]로 위치 변경)
  - 크기·수정 시각이 바뀐 파일만 복사하므로 지난 연도 파일은 다시 올라가지 않습니다. DB는 프로그램이 열려 있어도 안전하게 복사됩니다
  - 작업 폴더는 OneDrive 밖(예: `C:\MailViewer`)에 두고 백업 폴더만 OneDrive에 두면, 동기화 충돌 없이 클라우드 백업이 됩니다
  - 백업 폴더의 파일은 지우지 않습니다. 복원은 백업 폴더 전체를 복사해 오고 그 아래에 프로그램 폴더를 두면 됩니다 (Google 로그인은 PC마다 다시)
  - 정기 백업: 작업 스케줄러에 `MailViewer.exe --backup mails.db` 등록

## 구조

- DB에는 검색용 텍스트·메타데이터만 저장 — mbox가 수 GB여도 DB는 텍스트 분량만 커집니다
- HTML 본문·첨부파일은 DB에 복사하지 않고 mbox 파일 내 바이트 위치(`mbox_offset/length`)만 기록 → 볼 때 MimeKit으로 즉석 파싱
- 따라서 **mbox 원본 파일을 지우거나 옮기면 첨부/HTML 보기가 안 됩니다** (검색·텍스트 본문은 계속 동작). 단, `mails.db` 폴더 아래의 파일은 상대 경로로 기록되므로 DB와 함께 폴더째 옮기는 것은 괜찮습니다
- [메일 가져오기]는 여러 mbox 파일을 한 번에 선택할 수 있고, Message-ID가 같은 메일은 한 번만 들어갑니다
- EUC-KR(ks_c_5601-1987) 등 레거시 한글 인코딩을 시작 시 등록해 오래된 한국어 메일도 정상 디코딩됩니다

## DB 스키마 (`mails.db`, v6)

```sql
CREATE TABLE mails (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    date TEXT,           -- "yyyy-MM-dd HH:mm"
    from_full TEXT, from_name TEXT, from_email TEXT,
    to_full TEXT, cc TEXT,
    subject TEXT,
    thread_key TEXT,     -- "gm:<X-GM-THRID>" 또는 정규화 제목
    labels TEXT,         -- X-Gmail-Labels
    body TEXT,           -- 검색/미리보기용 텍스트
    has_html INTEGER, att_count INTEGER, att_names TEXT,
    mbox_offset INTEGER, mbox_length INTEGER,  -- CSV는 NULL
    message_id TEXT,     -- 동기화 중복 방지용
    mbox_file INTEGER,   -- mbox_files.id
    deleted INTEGER, starred INTEGER
);
CREATE TABLE mbox_files (id INTEGER PRIMARY KEY, path TEXT);  -- 원본 mbox 목록 (DB 폴더 기준 상대 경로 또는 절대 경로)
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);  -- 보관 폴더, 동기화 상태, 계정
-- mails_fts: FTS5 trigram 전문검색 인덱스
```

일반 SQLite 파일이라 Python, DB Browser for SQLite 등 다른 도구에서도 그대로 쓸 수 있습니다.

## 명령줄 모드 (헤드리스)

```
MailViewer.exe --import "백업.mbox" [더.mbox ...] "mails.db"   (마지막 인자가 DB)
MailViewer.exe --split-by-year "mails.db"   (연도별 정리)
MailViewer.exe --backup "mails.db" ["백업폴더"]   (폴더 생략 시 GUI에서 정한 위치)
MailViewer.exe --export "mails.db" "결과.xlsx" [검색어]
MailViewer.exe --dump "mails.db" <메일id> [출력.txt]
MailViewer.exe --sync "mails.db"          (계정은 GUI ⚙에서 미리 설정)
MailViewer.exe --diag-oauth <주소> <액세스토큰> <결과.txt>   (OAuth Playground 등에서 받은 토큰으로 IMAP XOAUTH2 로그인 확인)
```

## 빌드

Windows + .NET 9 SDK 필요.

```
dotnet build MailViewer/MailViewer.csproj -c Release
```

### GitHub Actions로 빌드 (PC에 SDK 불필요)

1. 저장소의 **Actions** 탭 → 왼쪽 **Build** → **Run workflow** → 브랜치 선택 후 실행
2. 몇 분 뒤 실행 화면 아래 **Artifacts**에서 zip 다운로드
   - `MailViewer-<커밋>-win-x64`: .NET 9 데스크톱 런타임 필요 (작음)
   - `MailViewer-<커밋>-win-x64-selfcontained`: 런타임 포함 (설치 없이 실행)
3. 테스트가 끝나면 main 브랜치로 다시 **Run workflow** → **release** 체크 + **version**(예: `v1.0.2`) 입력 → 같은 두 zip이 붙은 **Releases** 생성

주요 의존성: Microsoft.Data.Sqlite, MimeKit, MailKit, ClosedXML, Microsoft.Web.WebView2.
