using System.Text.Json;

namespace MailViewer;

/// <summary>UI 문자열 로컬라이제이션. 기본 English, 설정 파일(%APPDATA%\MailViewer\settings.json)로 전환.</summary>
public static class L
{
    public static string Lang { get; private set; } = "en";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MailViewer", "settings.json");

    public static void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("language", out var v) && v.GetString() is "ko" or "en")
                Lang = v.GetString()!;
        }
        catch { /* 설정 파일 문제 시 기본값(en) 유지 */ }
    }

    public static void Save(string lang)
    {
        Lang = lang;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { language = lang }));
        }
        catch { }
    }

    public static string T(string key) =>
        (Lang == "ko" ? Ko : En).TryGetValue(key, out var v) ? v
        : En.TryGetValue(key, out var e) ? e : key;

    public static string F(string key, params object[] args) => string.Format(T(key), args);

    private static readonly Dictionary<string, string> En = new()
    {
        ["title"] = "Mail Viewer",
        ["fld_all"] = "All", ["fld_subject"] = "Subject", ["fld_from"] = "From",
        ["fld_to"] = "To", ["fld_body"] = "Body", ["fld_cc"] = "CC",
        ["kw_placeholder"] = "Search (space = AND)",
        ["btn_search"] = "Search",
        ["chk_thread"] = "Group by thread",
        ["chk_attached"] = "Has attachment",
        ["chk_date"] = "Date",
        ["chk_trash"] = "Trash",
        ["menu_delete"] = "Delete (viewer only)",
        ["menu_restore"] = "Restore",
        ["confirm_delete"] = "Move {0} mail(s) to the viewer's trash?\nOriginals in the mbox file and Gmail are NOT affected.",
        ["status_trash"] = "Trash: {0:N0} mails",
        ["status_deleted"] = "Moved {0:N0} mail(s) to trash (viewer only)",
        ["status_restored"] = "Restored {0:N0} mail(s)",
        ["btn_stats"] = "Sender stats",
        ["btn_export"] = "Export to Excel",
        ["btn_import"] = "Import mail",
        ["btn_sync"] = "Gmail sync",
        ["col_no"] = "#", ["col_date"] = "Date", ["col_from"] = "From",
        ["col_subject"] = "Subject", ["col_att"] = "📎",
        ["col_last"] = "Latest", ["col_thread_subject"] = "Thread subject",
        ["col_count"] = "Mails", ["col_senders"] = "People",
        ["tab_body"] = "Body", ["tab_html"] = "HTML",
        ["status_total"] = "Total {0:N0} mails  ({1} ~ {2})",
        ["status_fts"] = "  [FTS5 full-text search]",
        ["status_results"] = "{0:N0} results",
        ["status_threads"] = "{0:N0} threads",
        ["status_saved"] = "Saved: {0}",
        ["hdr_subject"] = "Subject", ["hdr_date"] = "Date", ["hdr_from"] = "From",
        ["hdr_to"] = "To", ["hdr_cc"] = "CC", ["hdr_labels"] = "Labels",
        ["hdr_att"] = "Attachments", ["none"] = "(none)",
        ["hdr_thread"] = "Thread", ["hdr_mails"] = "{0:N0} mails",
        ["hdr_period"] = "Period", ["hdr_participants"] = "Participants",
        ["more_n"] = " and {0} more", ["no_subject"] = "(no subject)",
        ["info"] = "Info", ["err"] = "Error", ["confirm"] = "Confirm",
        ["msg_csv_no_mbox"] = "This mail was imported from CSV; the original (mbox) is not available.",
        ["msg_mbox_missing"] = "mbox file not found:\n{0}\n\nIf you moved it, re-import via [Import mail].",
        ["err_parse"] = "Failed to parse message:\n",
        ["att_mismatch"] = "Attachment list mismatch.",
        ["save_att_title"] = "Save attachment",
        ["save_att_fail"] = "Failed to save:\n",
        ["html_select"] = "Select a mail.",
        ["html_none"] = "This mail has no HTML body. See the [Body] tab.",
        ["html_load_fail"] = "Could not load the HTML body.",
        ["err_webview"] = "HTML viewer initialization failed (WebView2 runtime required):\n",
        ["stats_title"] = "Sender statistics — {0:N0} senders",
        ["stats_cond"] = "Filter: {0}    (click a header to sort, double-click a row to search that sender)",
        ["stats_col_sender"] = "Sender", ["stats_col_email"] = "Email",
        ["stats_col_count"] = "Count", ["stats_col_first"] = "First", ["stats_col_last"] = "Last",
        ["filter_all_mail"] = "all mail", ["filter_attached"] = "has attachment",
        ["export_title"] = "Export to Excel",
        ["export_file"] = "mail_search_{0}.xlsx",
        ["export_done"] = "Saved {0:N0} mails.\n{1}\n\nOpen it now?",
        ["export_done_title"] = "Export complete",
        ["export_fail"] = "Export failed:\n",
        ["xls_sheet"] = "Mail",
        ["xls_date"] = "Date", ["xls_from"] = "From", ["xls_to"] = "To",
        ["xls_subject"] = "Subject", ["xls_cc"] = "CC", ["xls_att"] = "Attachments", ["xls_body"] = "Body",
        ["import_title"] = "Select a mail file to import (Gmail backup mbox or CSV)",
        ["import_filter"] = "Mail files (*.mbox;*.csv)|*.mbox;*.csv|All files (*.*)|*.*",
        ["import_done"] = "Imported {0:N0} mails.",
        ["import_done_path"] = "Imported {0:N0} mails.\n{1}",
        ["import_done_title"] = "Import complete",
        ["import_fail"] = "Import failed:\n",
        ["prog_importing"] = "Importing... {0:N0} mails",
        ["prog_start"] = "Importing...",
        ["prog_title"] = "Importing: {0}",
        ["sync_conn"] = "Connecting to Gmail...",
        ["sync_check"] = "Checking for new mail...",
        ["sync_fetch"] = "Downloading... {0} mails",
        ["sync_dup"] = "Checking duplicates... {0} skipped",
        ["sync_prefix"] = "[Sync] ",
        ["sync_done_new"] = "Sync complete: {0:N0} new mails",
        ["sync_done_dup"] = " ({0:N0} duplicates skipped)",
        ["sync_done_none"] = "Sync complete: no new mail",
        ["sync_fail"] = "Sync failed:\n",
        ["sync_fail_hint"] = "\n\nCheck the app password via [Test connection] in ⚙ Settings.",
        ["sync_no_account"] = "Gmail account is not configured.",
        ["set_title"] = "Settings",
        ["set_lang"] = "Language",
        ["set_gmail_group"] = "Gmail sync account",
        ["set_guide"] = "Sync requires a Google \"App Password\" (not your account password).\n" +
                        "1. Enable 2-Step Verification on your Google account\n" +
                        "2. Go to myaccount.google.com/apppasswords\n" +
                        "3. Create an app password and enter the 16-character code below\n" +
                        "The password is encrypted with Windows DPAPI and stored locally only.",
        ["set_user"] = "Gmail address",
        ["set_pass"] = "App password",
        ["set_test"] = "Test connection",
        ["set_save"] = "Save", ["set_cancel"] = "Cancel",
        ["set_test_ok"] = "Connection successful!",
        ["set_test_fail"] = "Connection failed:\n",
        ["set_test_title"] = "Test",
        ["set_need_user"] = "Enter your Gmail address.",
        ["set_need_pass"] = "Enter the app password.",
        ["db_old"] = "The existing DB ({0}) uses an outdated format.\nPlease re-import from your mail file.",
        ["db_old_title"] = "DB format changed",
    };

    private static readonly Dictionary<string, string> Ko = new()
    {
        ["title"] = "메일 뷰어",
        ["fld_all"] = "전체", ["fld_subject"] = "제목", ["fld_from"] = "보낸사람",
        ["fld_to"] = "받는사람", ["fld_body"] = "본문", ["fld_cc"] = "CC",
        ["kw_placeholder"] = "검색어 (공백 = AND)",
        ["btn_search"] = "검색",
        ["chk_thread"] = "스레드로 묶기",
        ["chk_attached"] = "첨부만",
        ["chk_date"] = "기간",
        ["chk_trash"] = "휴지통",
        ["menu_delete"] = "삭제 (뷰어에서만)",
        ["menu_restore"] = "복원",
        ["confirm_delete"] = "선택한 {0}건을 뷰어의 휴지통으로 보낼까요?\nmbox 파일과 Gmail 원본에는 영향이 없습니다.",
        ["status_trash"] = "휴지통 {0:N0}건",
        ["status_deleted"] = "{0:N0}건을 휴지통으로 보냈습니다 (뷰어에서만)",
        ["status_restored"] = "{0:N0}건을 복원했습니다",
        ["btn_stats"] = "발신자 통계",
        ["btn_export"] = "엑셀 내보내기",
        ["btn_import"] = "메일 가져오기",
        ["btn_sync"] = "Gmail 동기화",
        ["col_no"] = "#", ["col_date"] = "날짜", ["col_from"] = "보낸사람",
        ["col_subject"] = "제목", ["col_att"] = "📎",
        ["col_last"] = "최근", ["col_thread_subject"] = "스레드 제목",
        ["col_count"] = "메일수", ["col_senders"] = "참여자",
        ["tab_body"] = "본문", ["tab_html"] = "HTML 원본",
        ["status_total"] = "전체 {0:N0}건  ({1} ~ {2})",
        ["status_fts"] = "  [FTS5 전문검색]",
        ["status_results"] = "검색 결과 {0:N0}건",
        ["status_threads"] = "스레드 {0:N0}개",
        ["status_saved"] = "저장됨: {0}",
        ["hdr_subject"] = "제목", ["hdr_date"] = "날짜", ["hdr_from"] = "보낸사람",
        ["hdr_to"] = "받는사람", ["hdr_cc"] = "CC", ["hdr_labels"] = "라벨",
        ["hdr_att"] = "첨부", ["none"] = "(없음)",
        ["hdr_thread"] = "스레드", ["hdr_mails"] = "메일 {0:N0}건",
        ["hdr_period"] = "기간", ["hdr_participants"] = "참여자",
        ["more_n"] = " 외 {0}명", ["no_subject"] = "(제목 없음)",
        ["info"] = "안내", ["err"] = "오류", ["confirm"] = "확인",
        ["msg_csv_no_mbox"] = "이 메일은 CSV에서 가져와 원본(mbox)이 없습니다.",
        ["msg_mbox_missing"] = "mbox 파일을 찾을 수 없습니다:\n{0}\n\n파일을 옮겼다면 [메일 가져오기]로 다시 가져와 주세요.",
        ["err_parse"] = "메시지 파싱 실패:\n",
        ["att_mismatch"] = "첨부 목록이 일치하지 않습니다.",
        ["save_att_title"] = "첨부파일 저장",
        ["save_att_fail"] = "저장 실패:\n",
        ["html_select"] = "메일을 선택하세요.",
        ["html_none"] = "이 메일에는 HTML 본문이 없습니다. [본문] 탭을 확인하세요.",
        ["html_load_fail"] = "HTML 본문을 불러올 수 없습니다.",
        ["err_webview"] = "HTML 뷰어 초기화 실패 (WebView2 런타임 필요):\n",
        ["stats_title"] = "발신자별 통계 — {0:N0}명",
        ["stats_cond"] = "조건: {0}    (열 머리글 클릭 = 정렬, 행 더블클릭 = 해당 발신자로 검색)",
        ["stats_col_sender"] = "발신자", ["stats_col_email"] = "이메일",
        ["stats_col_count"] = "건수", ["stats_col_first"] = "최초", ["stats_col_last"] = "최근",
        ["filter_all_mail"] = "전체 메일", ["filter_attached"] = "첨부만",
        ["export_title"] = "엑셀로 내보내기",
        ["export_file"] = "메일검색결과_{0}.xlsx",
        ["export_done"] = "{0:N0}건을 저장했습니다.\n{1}\n\n지금 열어볼까요?",
        ["export_done_title"] = "내보내기 완료",
        ["export_fail"] = "내보내기 실패:\n",
        ["xls_sheet"] = "메일",
        ["xls_date"] = "날짜", ["xls_from"] = "보낸사람", ["xls_to"] = "받는사람",
        ["xls_subject"] = "제목", ["xls_cc"] = "CC", ["xls_att"] = "첨부", ["xls_body"] = "본문",
        ["import_title"] = "가져올 메일 파일 선택 (Gmail 백업 mbox 또는 CSV)",
        ["import_filter"] = "메일 파일 (*.mbox;*.csv)|*.mbox;*.csv|모든 파일 (*.*)|*.*",
        ["import_done"] = "메일 {0:N0}건을 가져왔습니다.",
        ["import_done_path"] = "메일 {0:N0}건을 가져왔습니다.\n{1}",
        ["import_done_title"] = "가져오기 완료",
        ["import_fail"] = "가져오기 실패:\n",
        ["prog_importing"] = "가져오는 중... {0:N0}건",
        ["prog_start"] = "가져오는 중...",
        ["prog_title"] = "가져오는 중: {0}",
        ["sync_conn"] = "Gmail 접속 중...",
        ["sync_check"] = "새 메일 확인 중...",
        ["sync_fetch"] = "가져오는 중... {0}건",
        ["sync_dup"] = "중복 확인 중... {0}건 건너뜀",
        ["sync_prefix"] = "[동기화] ",
        ["sync_done_new"] = "동기화 완료: 새 메일 {0:N0}건 추가",
        ["sync_done_dup"] = " (중복 {0:N0}건 건너뜀)",
        ["sync_done_none"] = "동기화 완료: 새 메일 없음",
        ["sync_fail"] = "동기화 실패:\n",
        ["sync_fail_hint"] = "\n\n앱 비밀번호가 맞는지 ⚙ 설정에서 [연결 테스트]로 확인해 보세요.",
        ["sync_no_account"] = "Gmail 계정이 설정되지 않았습니다.",
        ["set_title"] = "환경설정",
        ["set_lang"] = "언어 (Language)",
        ["set_gmail_group"] = "Gmail 동기화 계정",
        ["set_guide"] = "동기화에는 Google \"앱 비밀번호\"가 필요합니다 (계정 비밀번호 아님).\n" +
                        "1. Google 계정에서 2단계 인증을 켭니다\n" +
                        "2. myaccount.google.com/apppasswords 접속\n" +
                        "3. 앱 비밀번호 생성 → 16자리 코드를 아래에 입력\n" +
                        "비밀번호는 Windows DPAPI로 암호화되어 이 PC에만 저장됩니다.",
        ["set_user"] = "Gmail 주소",
        ["set_pass"] = "앱 비밀번호",
        ["set_test"] = "연결 테스트",
        ["set_save"] = "저장", ["set_cancel"] = "취소",
        ["set_test_ok"] = "연결 성공!",
        ["set_test_fail"] = "연결 실패:\n",
        ["set_test_title"] = "테스트",
        ["set_need_user"] = "Gmail 주소를 입력하세요.",
        ["set_need_pass"] = "앱 비밀번호를 입력하세요.",
        ["db_old"] = "기존 DB({0})가 구버전 형식입니다.\n메일 파일에서 다시 가져와 주세요.",
        ["db_old_title"] = "DB 형식 변경",
    };
}
