using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace GameChatTranslator.Core.Ocr;

/// <summary>
/// อ่านแชตเกาหลีด้วยโมเดล vision บน Ollama เครื่องรีโมต (เช่น qwen3-vl:8b) แทน PaddleOCR
///
/// ต่างจาก PaddleOCR ตรงที่โมเดล vision อ่านทั้งภาพเป็นก้อนเดียวแล้วคืนข้อความทีละบรรทัด
/// ไม่มีกล่องพิกัด (Box) และไม่มีสีตัวอักษรกลับมา — ฟีเจอร์ที่พึ่งพิกัด (เช่นเลือกกรอบบางส่วน)
/// จะใช้ทุกบรรทัดที่อ่านได้จากภาพที่ครอปมาแล้วแทนการคัดตามตำแหน่ง
///
/// ยิงแบบ synchronous (บล็อกเธรด) เพราะ IOcrEngine.Read เป็น sync และสายพานเรียกมันบนเธรดพื้นหลังอยู่แล้ว
/// </summary>
public sealed class OllamaVlOcrEngine : IOcrEngine
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _keepAlive;
    private readonly object _gate = new();

    public string Name => "Ollama-VL";
    public bool IsReady => true;
    public bool ProvidesBoxes => false;
    public bool StrictLines { get; set; }
    public string? Problem { get; private set; }

    // คำสั่งเน้นย้ำหลายจุดว่า "ถอดความ ไม่ใช่แปล" เพราะโมเดล vision มักจะแปลหรือ "แก้" ฮันกึลให้เอง
    private const string Prompt =
        "You are an OCR engine. Transcribe every chat line visible in this game screenshot " +
        "EXACTLY as written, keeping the original Korean. One chat line per output line, " +
        "top to bottom. Start from the VERY FIRST line at the top of the image — never skip it. " +
        "Copy the speaker name and any channel tag ONLY if they are actually visible in the image — " +
        "never invent a tag like [Global] that is not there. " +
        "Do NOT translate. Do NOT fix spelling or slang. Do NOT add quotes, numbering, or commentary. " +
        "If there is no readable text, output nothing.";

    // โมเดล vision มักมองข้ามข้อความที่ชิดขอบภาพพอดี — เติมกรอบว่างรอบภาพก่อนส่ง
    private const int PadPx = 14;

    // ต่อท้าย Prompt ตอน StrictLines (ลากกรอบแปลบางส่วน): เอาเฉพาะบรรทัดที่เห็นเกินครึ่ง
    private const string StrictSuffix =
        " IMPORTANT: the image is a small region the user selected. Transcribe ONLY lines that are " +
        "at least half visible. Skip any line where less than half of its height shows.";

    public OllamaVlOcrEngine(string baseUrl, string model, string keepAlive = "30m")
    {
        _model = string.IsNullOrWhiteSpace(model) ? "qwen3-vl:8b" : model;
        _keepAlive = keepAlive;
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            // โหลดโมเดล vision ครั้งแรกช้ามาก (วัดจริง ~54 วิ) หลังจากนั้นอุ่นแล้วเหลือ ~1 วิ
            Timeout = TimeSpan.FromSeconds(180)
        };
    }

    /// <summary>สั่ง Ollama โหลดโมเดลขึ้นหน่วยความจำล่วงหน้า จะได้ไม่เจอ 54 วิ ตอนสแกนครั้งแรก</summary>
    public async Task WarmUpAsync(CancellationToken token)
    {
        try
        {
            var payload = new { model = _model, prompt = "", keep_alive = _keepAlive };
            using var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync("/api/generate", content, token);

            Problem = response.IsSuccessStatusCode
                ? null
                : $"โหลดโมเดล VL ไม่สำเร็จ ({(int)response.StatusCode})";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Problem = "อุ่นเครื่องโมเดล VL ไม่สำเร็จ (ตรวจว่าเปิด Ollama อยู่): " + ex.Message;
        }
    }

    public List<OcrLine> Read(Bitmap frame)
    {
        lock (_gate)
        {
            try
            {
                using var padded = Pad(frame, PadPx);
                var b64 = ToPngBase64(padded);
                var prompt = StrictLines ? Prompt + StrictSuffix : Prompt;

                var payload = new
                {
                    model = _model,
                    stream = false,
                    keep_alive = _keepAlive,
                    // โมเดลตอบเป็นข้อความล้วน ไม่มีเหตุผลต้องเปิด thinking (ช้าเปล่า ๆ)
                    think = false,
                    options = new { temperature = 0 },
                    messages = new[]
                    {
                        new { role = "user", content = prompt, images = new[] { b64 } }
                    }
                };

                using var content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var response = _http.PostAsync("/api/chat", content).GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    Problem = $"โมเดล VL ปฏิเสธคำขอ ({(int)response.StatusCode})";
                    return new List<OcrLine>();
                }

                var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Problem = null;

                var result = ParseLines(json);
                if (DebugDump) WriteDebug(padded, json, result);
                return result;
            }
            catch (Exception ex)
            {
                Problem = "อ่านด้วยโมเดล VL ไม่สำเร็จ: " + ex.Message;
                return new List<OcrLine>();
            }
        }
    }

    private static string ToPngBase64(Bitmap frame)
    {
        using var ms = new MemoryStream();
        frame.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>เติมกรอบว่างสีเข้มรอบภาพ กันโมเดล vision มองข้ามบรรทัดบนสุด/ล่างสุดที่ชิดขอบ</summary>
    private static Bitmap Pad(Bitmap src, int pad)
    {
        var dst = new Bitmap(src.Width + pad * 2, src.Height + pad * 2, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.Clear(Color.FromArgb(18, 18, 22));
        g.DrawImageUnscaled(src, pad, pad);
        return dst;
    }

    /// <summary>ตอบกลับจาก /api/chat คือ {"message":{"content":"บรรทัด1\nบรรทัด2..."}}</summary>
    private static List<OcrLine> ParseLines(string json)
    {
        var lines = new List<OcrLine>();

        string? text;
        try
        {
            using var doc = JsonDocument.Parse(json);
            text = doc.RootElement.GetProperty("message").GetProperty("content").GetString();
        }
        catch
        {
            return lines;
        }

        if (string.IsNullOrWhiteSpace(text)) return lines;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().Trim('"', '\'', '`', '-', '*').Trim();
            if (line.Length == 0) continue;

            // กันโมเดลแอบเติมประโยคเกริ่น/ปิดท้ายภาษาอังกฤษ ("Here are the chat lines:")
            if (LooksLikeCommentary(line)) continue;

            lines.Add(new OcrLine(line, 100f));
        }

        return lines;
    }

    private static bool LooksLikeCommentary(string line)
    {
        // บรรทัดแชตจริงมีฮันกึลเกือบทุกครั้ง ถ้าไม่มีฮันกึลเลยและเป็นอังกฤษล้วน น่าจะเป็นคำเกริ่นของโมเดล
        bool hasHangul = false;
        foreach (var c in line)
        {
            if (c is >= '가' and <= '힣' or >= '㄰' and <= '㆏')
            {
                hasHangul = true;
                break;
            }
        }
        if (hasHangul) return false;

        var lower = line.ToLowerInvariant();
        return lower.Contains("here are") || lower.Contains("chat line") ||
               lower.Contains("no readable") || lower.Contains("screenshot") ||
               lower.EndsWith(":");
    }

    // ---------- ดีบัก: env GCT_OCR_DEBUG=1 → ดัมป์ภาพที่ส่งให้ VL + คำตอบดิบ ไปที่ %AppData%\GameChatTranslator\ocr-debug ----------

    // เปิดดีบักด้วยการสร้างไฟล์เปล่าชื่อ DEBUG ไว้ใน %AppData%\GameChatTranslator (เช็คทุกครั้ง ไม่ต้อง restart)
    private static string DebugDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameChatTranslator", "ocr-debug");

    private static bool DebugDump => File.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameChatTranslator", "DEBUG"));

    private static void WriteDebug(Bitmap sent, string rawJson, List<OcrLine> parsed)
    {
        try
        {
            var dir = DebugDir;
            Directory.CreateDirectory(dir);

            var stamp = DateTime.Now.ToString("HHmmss-fff");
            sent.Save(Path.Combine(dir, $"{stamp}-vl-input.png"), ImageFormat.Png);

            string content = "";
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                content = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
            }
            catch { }

            var sb = new StringBuilder();
            sb.AppendLine($"image: {sent.Width}x{sent.Height}");
            sb.AppendLine().AppendLine("VL RAW content:").AppendLine(content);
            sb.AppendLine().AppendLine("PARSED LINES:");
            foreach (var l in parsed) sb.AppendLine("  " + l.Text);

            File.WriteAllText(Path.Combine(dir, $"{stamp}-vl-lines.txt"), sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // ดีบักพลาดไม่กระทบการอ่านจริง
        }
    }

    public void Dispose() => _http.Dispose();
}
