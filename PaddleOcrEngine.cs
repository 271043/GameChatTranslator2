using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local;

namespace GameChatTranslator.Core.Ocr;

/// <param name="Box">กรอบข้อความในพิกัดของภาพที่ส่งเข้า Read() (ก่อนขยายภาพ) — Empty ถ้าหาไม่ได้</param>
public record OcrLine(string Text, float Confidence, (byte R, byte G, byte B)? Color = null, Rectangle Box = default);

public interface IOcrEngine : IDisposable
{
    string Name { get; }
    bool IsReady { get; }
    string? Problem { get; }
    List<OcrLine> Read(Bitmap frame);

    /// <summary>คืนกล่องพิกัด (Box) ของแต่ละบรรทัดไหม — โมเดล VL ไม่คืน ผู้เรียกต้องเปลี่ยนวิธีคัดบรรทัด</summary>
    bool ProvidesBoxes => true;

    /// <summary>
    /// โหมด "เฉพาะบรรทัดที่เห็นครบ" — ผู้เรียกเปิดตอนแปลบางส่วน (ลากกรอบ) เพื่อให้ตัด
    /// บรรทัดข้าง ๆ ที่ถูกขอบครอปหั่นครึ่งทิ้ง เอนจินที่คัดตามพิกัดได้เองปล่อยเป็น no-op
    /// </summary>
    bool StrictLines { get => false; set { } }

    /// <summary>อุ่นเครื่องล่วงหน้า (เช่นโหลดโมเดลรีโมต) — เอนจินที่พร้อมใช้ทันทีปล่อยเป็น no-op</summary>
    Task WarmUpAsync(CancellationToken token) => Task.CompletedTask;
}

/// <summary>
/// อ่านฮันกึลด้วย PaddleOCR (PP-OCR โมเดลภาษาเกาหลี)
/// รันบน CPU (Mkldnn) เสมอ ไม่ใช้ GPU เพราะ VRAM ถูกแชร์กับ Ollama และเกม 3 หน้าต่างพร้อมกันอยู่แล้ว
/// โมเดลฝังมากับแพ็กเกจ NuGet โดยตรง ไม่ต้องให้ผู้ใช้ปลายทางโหลดไฟล์โมเดลแยกเองเหมือน Tesseract
/// </summary>
public sealed class PaddleOcrEngine : IOcrEngine
{
    private readonly PaddleOcrAll? _engine;
    private readonly double _upscale;
    private readonly float _minConfidence;
    private readonly bool _preprocess;
    private readonly object _gate = new();

    public string Name => "PaddleOCR";
    public bool IsReady => _engine != null;
    public string? Problem { get; }

    public PaddleOcrEngine(double upscale, float minConfidence, bool preprocess = true)
    {
        _preprocess = preprocess;
        _upscale = upscale <= 0 ? 1.0 : upscale;
        // Score ของ PaddleOCR เป็นสเกล 0-1 ส่วนค่าตั้งเดิมมาจากยุค Tesseract ที่เป็น 0-100
        _minConfidence = minConfidence > 1f ? minConfidence / 100f : minConfidence;

        try
        {
            _engine = new PaddleOcrAll(LocalFullModels.KoreanV5, PaddleDevice.Mkldnn())
            {
                // แชตเกมเป็นข้อความแนวนอนเสมอ ไม่ต้องเดาการหมุน — วัดจริงพบว่าเปิดไว้ทำให้
                // กล่องตรวจจับหมุนข้อความแนวนอนไปเกือบ 90° ก่อนส่งเข้าโมเดลอ่านค่า จนอ่านไม่ออกเลย (0 บรรทัด)
                AllowRotateDetection = false,
                Enable180Classification = false
            };
        }
        catch (Exception ex)
        {
            Problem = "เปิด PaddleOCR ไม่สำเร็จ: " + ex.Message;
            _engine = null;
        }
    }

    public List<OcrLine> Read(Bitmap frame)
    {
        var lines = new List<OcrLine>();
        if (_engine == null) return lines;

        // PaddleOcrAll ไม่ thread-safe ต้องกันไว้เหมือน Tesseract เดิม
        lock (_gate)
        {
            Bitmap working = frame;
            Bitmap? scaled = null;
            Mat? mat = null;

            try
            {
                if (_upscale > 1.01)
                {
                    scaled = Upscale(frame, _upscale);
                    working = scaled;
                }

                // Bitmap เป็น 32bppArgb (4 ช่อง) แต่ตัวตรวจจับของ PaddleOCR รับแค่ 3 ช่อง (BGR) หรือ 1 ช่อง (เทา)
                // ไม่แปลงก่อนจะโดน NotSupportedException ทันทีที่ Run() — เจอจริงตอนทดสอบ
                using var rawMat = working.ToMat();
                mat = new Mat();
                Cv2.CvtColor(rawMat, mat, ColorConversionCodes.BGRA2BGR);

                // แชตเกมมักเป็นตัวอักษรบาง ๆ ทับพื้นหลังเกมที่มีลาย/โปร่งแสง คอนทราสต์ต่ำ
                // ทำ CLAHE บนภาพเทาก่อนช่วยให้ขอบตัวอักษรชัดขึ้นมาก แล้วดันกลับเป็น 3 ช่องให้ Paddle
                if (_preprocess)
                {
                    try
                    {
                        using var gray = new Mat();
                        Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);
                        using var clahe = Cv2.CreateCLAHE(2.0, new OpenCvSharp.Size(8, 8));
                        clahe.Apply(gray, gray);
                        Cv2.CvtColor(gray, mat, ColorConversionCodes.GRAY2BGR);
                    }
                    catch
                    {
                        // พรีโปรเซสพลาดก็ยังอ่านจากภาพเดิมต่อได้
                    }
                }

                var result = _engine.Run(mat);

                // เก็บทุก region ที่พออ่านออก (เกณฑ์ต่ำ) ไว้ก่อน แล้วค่อยกรองความมั่นใจตอนเป็นบรรทัดเต็ม
                // ถ้ากรองตั้งแต่ตอนนี้ เศษที่มั่นใจน้อย (เช่นครึ่งท้ายข้อความ) จะหายไปทั้งที่ควรต่อกับส่วนหน้า
                var raw = new List<(string Text, float Score, Rectangle Box)>();
                foreach (var region in result.Regions)
                {
                    var t = region.Text?.Trim();
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    if (region.Score < 0.15f) continue;

                    if (ToBoundingRect(region.Rect, working.Width, working.Height) is { } b)
                        raw.Add((t, region.Score, b));
                }

                // PaddleOCR แยกกล่องตรวจจับตามช่องว่าง/สี — "노트입니다:" กับ "감쟈감쟈" มักได้คนละกล่อง
                // ประกอบกลับเป็นบรรทัดเดียวก่อนส่งแปล ไม่งั้นตัวแปลจะแปลครึ่งท่อน
                var rows = MergeIntoLines(raw);

                if (DebugDump) WriteDebug(working, raw, rows);

                // rows เรียงบนลงล่างมาแล้ว — สายพานแปลพึ่งลำดับนี้เป็นลำดับเวลาของแชต
                foreach (var row in rows)
                {
                    if (row.Score < _minConfidence) continue;

                    var text = Regex.Replace(row.BuildText(), @"\s+", " ").Trim();
                    if (text.Length == 0) continue;

                    var wbox = row.Box;
                    if (wbox.Width < 1 || wbox.Height < 1) continue;

                    var color = ExtractColor(working, wbox);

                    // คืนกรอบในพิกัดของภาพต้นทาง (ก่อนขยาย) เผื่อผู้เรียกใช้คัดบรรทัดตามตำแหน่ง
                    var frameBox = _upscale > 1.01
                        ? new Rectangle(
                            (int)Math.Round(wbox.X / _upscale), (int)Math.Round(wbox.Y / _upscale),
                            (int)Math.Round(wbox.Width / _upscale), (int)Math.Round(wbox.Height / _upscale))
                        : wbox;

                    lines.Add(new OcrLine(text, row.Score * 100f, color, frameBox));
                }
            }
            catch
            {
                // เฟรมเดียวพลาดไม่ควรทำให้ทั้งสายพานหยุด
            }
            finally
            {
                mat?.Dispose();
                scaled?.Dispose();
            }
        }

        return lines;
    }

    private static Rectangle? ToBoundingRect(RotatedRect rect, int frameW, int frameH)
    {
        var r = rect.BoundingRect();
        var box = Rectangle.Intersect(
            new Rectangle(r.X, r.Y, r.Width, r.Height),
            new Rectangle(0, 0, frameW, frameH));
        return box.Width > 0 && box.Height > 0 ? box : null;
    }

    /// <summary>ขยายภาพก่อนอ่าน ช่วยฟอนต์เกมขนาดเล็ก เหมือนที่เคยใช้กับ Tesseract</summary>
    private static Bitmap Upscale(Bitmap source, double factor)
    {
        int w = (int)Math.Round(source.Width * factor);
        int h = (int)Math.Round(source.Height * factor);

        var dest = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dest);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, 0, 0, w, h);
        return dest;
    }

    /// <summary>
    /// สุ่มดูสีตัวอักษรจากกรอบที่ PaddleOCR บอกมา แยก "หมึก" ออกจาก "พื้นหลัง" ด้วยเกณฑ์ Otsu
    /// เหมือนที่เคยใช้กับ Tesseract — ตัวหนังสือกินพื้นที่น้อยกว่าพื้นหลังเสมอ
    /// </summary>
    private static (byte R, byte G, byte B)? ExtractColor(Bitmap bmp, Rectangle box)
    {
        var count = new int[256];
        var sumR = new long[256];
        var sumG = new long[256];
        var sumB = new long[256];
        int total = 0;

        var data = bmp.LockBits(box, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                for (int y = 0; y < box.Height; y++)
                {
                    byte* row = basePtr + y * data.Stride;
                    for (int x = 0; x < box.Width; x++)
                    {
                        byte b = row[x * 4];
                        byte g = row[x * 4 + 1];
                        byte r = row[x * 4 + 2];
                        int lum = (r * 77 + g * 151 + b * 28) >> 8;

                        count[lum]++;
                        sumR[lum] += r;
                        sumG[lum] += g;
                        sumB[lum] += b;
                        total++;
                    }
                }
            }
        }
        finally { bmp.UnlockBits(data); }

        if (total < 8) return null;

        int threshold = OtsuThreshold(count, total);

        long darkCount = 0, darkR = 0, darkG = 0, darkB = 0;
        long lightCount = 0, lightR = 0, lightG = 0, lightB = 0;

        for (int lum = 0; lum <= threshold; lum++)
        {
            darkCount += count[lum];
            darkR += sumR[lum]; darkG += sumG[lum]; darkB += sumB[lum];
        }
        for (int lum = threshold + 1; lum < 256; lum++)
        {
            lightCount += count[lum];
            lightR += sumR[lum]; lightG += sumG[lum]; lightB += sumB[lum];
        }

        var (n, r2, g2, b2) = darkCount > 0 && darkCount <= lightCount
            ? (darkCount, darkR, darkG, darkB)
            : (lightCount, lightR, lightG, lightB);

        if (n == 0) return null;

        return ((byte)(r2 / n), (byte)(g2 / n), (byte)(b2 / n));
    }

    /// <summary>เกณฑ์ Otsu หาความสว่างที่แบ่งสองกลุ่มให้ความแปรปรวนระหว่างกลุ่มมากที่สุด</summary>
    private static int OtsuThreshold(int[] histogram, int total)
    {
        long sumAll = 0;
        for (int i = 0; i < 256; i++) sumAll += (long)i * histogram[i];

        long sumB = 0;
        int wB = 0;
        double maxVariance = -1;
        int best = 127;

        for (int t = 0; t < 256; t++)
        {
            wB += histogram[t];
            if (wB == 0) continue;

            int wF = total - wB;
            if (wF == 0) break;

            sumB += (long)t * histogram[t];

            double mB = (double)sumB / wB;
            double mF = (double)(sumAll - sumB) / wF;
            double variance = (double)wB * wF * (mB - mF) * (mB - mF);

            if (variance > maxVariance)
            {
                maxVariance = variance;
                best = t;
            }
        }

        return best;
    }

    // ---------- ประกอบบรรทัดใหม่จากกล่องที่ PaddleOCR แยกออกมา ----------

    /// <summary>หนึ่งบรรทัดแชตที่ประกอบเสร็จแล้ว (อาจมาจากหลายกล่อง + ส่วนที่ล้นจากการตัดคำ)</summary>
    private sealed class Row
    {
        public int AnchorY;                 // จุดกึ่งกลางแนวตั้งของกล่องแรก ใช้จับคู่แถว
        public int Left, Top, Right, Bottom; // กรอบรวมของทั้งแถว
        public double ScoreSum;
        public int Count;
        public int AbsorbedContinuations;

        // ส่วนหัว: กล่องในแถวแนวนอนเดียวกัน เรียงซ้าย→ขวาตอนต่อข้อความ
        public readonly List<(int X, string Text)> Parts = new();
        // ส่วนต่อ: แถวถัดมาที่เป็นการตัดคำของข้อความเดียวกัน ต่อท้ายตามลำดับบนลงล่าง
        public readonly List<string> Tail = new();

        public float Score => Count == 0 ? 0f : (float)(ScoreSum / Count);
        public Rectangle Box => Rectangle.FromLTRB(Left, Top, Right, Bottom);

        public string HeadText() => string.Join(" ", Parts.OrderBy(p => p.X).Select(p => p.Text));

        public string BuildText()
        {
            var head = HeadText();
            return Tail.Count == 0 ? head : head + " " + string.Join(" ", Tail);
        }
    }

    private static readonly char[] ColonChars = { ':', '：' };

    /// <summary>
    /// บรรทัดที่ขึ้นต้นด้วย "[แท็ก]"/"【แท็ก】" หรือมี "ชื่อ:" ภายใน 20 ตัวแรก ถือเป็น "ข้อความใหม่"
    /// ไม่ใช่ส่วนที่ล้นต่อจากบรรทัดบน (ใช้กันรวมข้อความคนละอันเข้าด้วยกันในขั้นที่ 2)
    /// </summary>
    private static bool StartsNewMessage(string text)
    {
        var t = text.TrimStart();
        if (t.Length == 0) return false;
        if (t[0] is '[' or '【' or '(' or '<' or '〔' or '「') return true;
        int colon = t.IndexOfAny(ColonChars);
        return colon > 0 && colon <= 20;
    }

    private static List<Row> MergeIntoLines(List<(string Text, float Score, Rectangle Box)> items)
    {
        var rows = new List<Row>();
        if (items.Count == 0) return rows;

        // ความสูงตัวแทน (median) เป็นสเกลของทุกเกณฑ์ — ทนกว่าค่าเฉลี่ยเมื่อมีกล่องเพี้ยนปน
        var heights = items.Select(i => i.Box.Height).Where(h => h > 0).OrderBy(h => h).ToList();
        if (heights.Count == 0) return rows;
        double medH = Math.Max(1, heights[heights.Count / 2]);

        // ---- ขั้นที่ 1: รวมกล่องที่จุดกึ่งกลางแนวตั้งใกล้กัน = แถวแนวนอนเดียวกัน ----
        foreach (var it in items.OrderBy(i => i.Box.Y))
        {
            int cy = it.Box.Y + it.Box.Height / 2;

            Row? row = null;
            foreach (var r in rows)
            {
                if (Math.Abs(r.AnchorY - cy) <= 0.6 * medH) { row = r; break; }
            }

            if (row == null)
            {
                row = new Row
                {
                    AnchorY = cy,
                    Left = it.Box.Left, Top = it.Box.Top, Right = it.Box.Right, Bottom = it.Box.Bottom
                };
                rows.Add(row);
            }
            else
            {
                row.Left = Math.Min(row.Left, it.Box.Left);
                row.Top = Math.Min(row.Top, it.Box.Top);
                row.Right = Math.Max(row.Right, it.Box.Right);
                row.Bottom = Math.Max(row.Bottom, it.Box.Bottom);
            }

            row.Parts.Add((it.Box.Left, it.Text));
            row.ScoreSum += it.Score;
            row.Count++;
        }

        rows.Sort((a, b) => a.Top.CompareTo(b.Top));

        // ---- ขั้นที่ 2: แถวที่ไม่มี prefix + ชิดซ้ายใกล้แถวบน + ติดกันแนวตั้ง = ส่วนที่ล้นจากการตัดคำ ----
        var merged = new List<Row>();
        foreach (var r in rows)
        {
            if (merged.Count > 0)
            {
                var prev = merged[^1];

                bool isContinuation =
                    !StartsNewMessage(r.HeadText()) &&
                    Math.Abs(r.Left - prev.Left) <= 2.0 * medH &&
                    (r.Top - prev.Bottom) <= 0.9 * medH &&
                    prev.AbsorbedContinuations < 2;

                if (isContinuation)
                {
                    prev.Tail.Add(r.HeadText());
                    prev.Right = Math.Max(prev.Right, r.Right);
                    prev.Bottom = Math.Max(prev.Bottom, r.Bottom);
                    prev.ScoreSum += r.Score;
                    prev.Count++;
                    prev.AbsorbedContinuations++;
                    continue;
                }
            }

            merged.Add(r);
        }

        return merged;
    }

    // ---------- ดีบัก: ตั้ง env GCT_OCR_DEBUG=1 เพื่อดัมป์ภาพ+ผลอ่านแต่ละครั้งไปที่ %AppData%\GameChatTranslator\ocr-debug ----------

    private static readonly bool DebugDump =
        Environment.GetEnvironmentVariable("GCT_OCR_DEBUG") is "1" or "true";

    private static void WriteDebug(
        Bitmap working,
        List<(string Text, float Score, Rectangle Box)> raw,
        List<Row> rows)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GameChatTranslator", "ocr-debug");
            Directory.CreateDirectory(dir);

            var stamp = DateTime.Now.ToString("HHmmss-fff");
            working.Save(Path.Combine(dir, $"{stamp}-input.png"), ImageFormat.Png);

            var sb = new StringBuilder();
            sb.AppendLine("RAW REGIONS (Paddle แยกออกมา):");
            foreach (var r in raw.OrderBy(r => r.Box.Y).ThenBy(r => r.Box.X))
                sb.AppendLine($"  [{r.Box.X,4},{r.Box.Y,4} {r.Box.Width,4}x{r.Box.Height,3}] {r.Score:F2}  {r.Text}");

            sb.AppendLine().AppendLine("MERGED LINES (หลังประกอบ):");
            foreach (var r in rows)
                sb.AppendLine($"  [{r.Left,4},{r.Top,4} {r.Right - r.Left,4}x{r.Bottom - r.Top,3}] {r.Score:F2}  {r.BuildText()}");

            File.WriteAllText(Path.Combine(dir, $"{stamp}-lines.txt"), sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // ดีบักพลาดไม่กระทบการอ่านจริง
        }
    }

    public void Dispose()
    {
        lock (_gate) { _engine?.Dispose(); }
    }
}
