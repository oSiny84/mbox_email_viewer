using System.Text;
using ClosedXML.Excel;

namespace MailViewer;

/// <summary>검색 결과를 .xlsx로 저장.</summary>
public static class ExcelExporter
{
    // xlsx 셀 최대 32,767자 — 초과 본문은 잘라서 저장
    private const int MaxCell = 32000;

    public static void Export(List<MailDetail> mails, string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("메일");

        string[] headers = { "날짜", "보낸사람", "받는사람", "제목", "CC", "첨부", "본문" };
        for (int c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#D9E1F2");
        }

        int row = 2;
        foreach (var m in mails)
        {
            ws.Cell(row, 1).Value = m.Date;
            ws.Cell(row, 2).Value = Clean(m.FromFull);
            ws.Cell(row, 3).Value = Clean(m.ToFull);
            ws.Cell(row, 4).Value = Clean(m.Subject);
            ws.Cell(row, 5).Value = Clean(m.Cc);
            ws.Cell(row, 6).Value = Clean(m.AttNames);
            ws.Cell(row, 7).Value = Clean(m.Body);
            row++;
        }

        ws.Column(1).Width = 17;
        ws.Column(2).Width = 30;
        ws.Column(3).Width = 30;
        ws.Column(4).Width = 45;
        ws.Column(5).Width = 35;
        ws.Column(6).Width = 25;
        ws.Column(7).Width = 80;
        ws.SheetView.FreezeRows(1);
        ws.Range(1, 1, Math.Max(row - 1, 1), headers.Length).SetAutoFilter();

        wb.SaveAs(path);
    }

    /// <summary>XML에 넣을 수 없는 제어문자 제거 + 셀 길이 제한.</summary>
    private static string Clean(string s)
    {
        var sb = new StringBuilder(Math.Min(s.Length, MaxCell + 8));
        foreach (char ch in s)
        {
            if (ch is '\t' or '\n' or '\r' || ch >= 0x20 && ch != '￾' && ch != '￿')
                sb.Append(ch);
            if (sb.Length >= MaxCell) { sb.Append("…(생략)"); break; }
        }
        return sb.ToString();
    }
}
