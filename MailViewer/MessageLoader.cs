using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Tnef;

namespace MailViewer;

public record AttachmentInfo(string Name, string MimeType, long Size, MimeEntity Entity);

public sealed class LoadedMessage
{
    public required MimeMessage Message { get; init; }
    public string? Html { get; init; }               // 인라인 이미지가 data: URI로 치환된 HTML
    public required List<AttachmentInfo> Attachments { get; init; }
}

/// <summary>DB에 기록된 바이트 위치로 mbox에서 메시지 하나를 즉석 파싱.</summary>
public static class MessageLoader
{
    public static LoadedMessage Load(string mboxPath, long offset, long length)
    {
        // 동기화가 같은 연도 파일에 쓰는 중에도 열 수 있게 ReadWrite 공유
        using var fs = new FileStream(mboxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[length];
        fs.ReadExactly(buf, 0, (int)length);

        var parser = new MimeParser(new MemoryStream(buf), MimeFormat.Mbox);
        var msg = parser.ParseMessage();

        var attachments = new List<AttachmentInfo>();
        foreach (var e in ExpandAttachments(msg))
        {
            long size = e is MimePart mp ? GetDecodedSize(mp) : 0;
            attachments.Add(new AttachmentInfo(AttachmentName(e), e.ContentType.MimeType, size, e));
        }

        return new LoadedMessage
        {
            Message = msg,
            Html = msg.HtmlBody is { } html ? InlineCidImages(html, msg) : null,
            Attachments = attachments,
        };
    }

    /// <summary>Outlook의 winmail.dat(TNEF) 포장을 풀어 실제 첨부파일 목록으로 펼친다.</summary>
    public static List<MimeEntity> ExpandAttachments(MimeMessage msg)
    {
        var list = new List<MimeEntity>();
        foreach (var e in msg.Attachments)
        {
            if (e is TnefPart tnef)
            {
                List<MimeEntity> inner;
                try { inner = tnef.ExtractAttachments().ToList(); }
                catch { inner = new(); }
                if (inner.Count > 0) { list.AddRange(inner); continue; }
                // 풀지 못하면 winmail.dat 그대로 노출
            }
            list.Add(e);
        }
        return list;
    }

    public static string AttachmentName(MimeEntity e) =>
        e is MimePart mp ? (mp.FileName ?? "(이름 없음)") :
        e is MessagePart ? (e.ContentDisposition?.FileName ?? "(첨부 메일)") : "(기타)";

    private static long GetDecodedSize(MimePart part)
    {
        try
        {
            using var counter = new CountingStream();
            part.Content.DecodeTo(counter);
            return counter.Length;
        }
        catch { return 0; }
    }

    /// <summary>cid: 참조 인라인 이미지를 data: URI로 치환.</summary>
    private static string InlineCidImages(string html, MimeMessage msg)
    {
        foreach (var entity in msg.BodyParts)
        {
            if (entity is not MimePart part || string.IsNullOrEmpty(part.ContentId)) continue;
            if (!html.Contains("cid:" + part.ContentId, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var ms = new MemoryStream();
                part.Content.DecodeTo(ms);
                string dataUri = $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(ms.ToArray())}";
                html = Regex.Replace(html, Regex.Escape("cid:" + part.ContentId), dataUri, RegexOptions.IgnoreCase);
            }
            catch { /* 개별 이미지 실패는 무시 */ }
        }
        return html;
    }

    /// <summary>렌더링용 완성 HTML: 스크립트 제거 + CSP로 외부 리소스 차단.</summary>
    public static string BuildSafeHtml(string bodyHtml)
    {
        string cleaned = Regex.Replace(bodyHtml, @"<script[^>]*>.*?</script>", "",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\son\w+\s*=\s*(""[^""]*""|'[^']*'|\S+)", "",
            RegexOptions.IgnoreCase);

        return """
            <!DOCTYPE html><html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy"
                  content="default-src 'none'; img-src data:; style-src 'unsafe-inline';">
            <style>body { font-family: 'Malgun Gothic', sans-serif; margin: 12px; }</style>
            </head><body>
            """ + cleaned + "</body></html>";
    }

    public static void SaveAttachment(AttachmentInfo att, string path)
    {
        using var fs = File.Create(path);
        if (att.Entity is MessagePart mp) mp.Message.WriteTo(fs);
        else if (att.Entity is MimePart part) part.Content.DecodeTo(fs);
    }

    private sealed class CountingStream : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
    }
}
