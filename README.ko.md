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
  - 새 메일은 `mails.sync.mbox`에 원본 저장 → 첨부/HTML 보기 동일하게 동작
  - Message-ID로 중복 방지 (Takeout mbox와 겹쳐도 안전), Gmail 스레드 ID로 기존 스레드에 합류
  - 자동 동기화: 작업 스케줄러에 `MailViewer.exe --sync mails.db` 등록하면 주기 실행 가능

## 구조

- DB에는 검색용 텍스트·메타데이터만 저장 — mbox가 수 GB여도 DB는 텍스트 분량만 커집니다
- HTML 본문·첨부파일은 DB에 복사하지 않고 mbox 파일 내 바이트 위치(`mbox_offset/length`)만 기록 → 볼 때 MimeKit으로 즉석 파싱
- 따라서 **mbox 원본 파일을 지우거나 옮기면 첨부/HTML 보기가 안 됩니다** (검색·텍스트 본문은 계속 동작)
- EUC-KR(ks_c_5601-1987) 등 레거시 한글 인코딩을 시작 시 등록해 오래된 한국어 메일도 정상 디코딩됩니다

## DB 스키마 (`mails.db`, v3)

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
    mbox_file INTEGER    -- 0 = 원본 mbox, 1 = 동기화 mbox
);
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);  -- mbox_path, 동기화 상태, 계정
-- mails_fts: FTS5 trigram 전문검색 인덱스
```

일반 SQLite 파일이라 Python, DB Browser for SQLite 등 다른 도구에서도 그대로 쓸 수 있습니다.

## 명령줄 모드 (헤드리스)

```
MailViewer.exe --import "백업.mbox" "mails.db"
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

주요 의존성: Microsoft.Data.Sqlite, MimeKit, MailKit, ClosedXML, Microsoft.Web.WebView2.
