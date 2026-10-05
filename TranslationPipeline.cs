using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading.Channels;
using GameChatTranslator.Core.Capture;
using GameChatTranslator.Core.Ocr;
using GameChatTranslator.Core.Translate;

namespace GameChatTranslator.Core.Pipeline;

/// <summary>
/// สายพานหลัก จับภาพไม่ได้ทำงานเป็นลูปอัตโนมัติแล้ว แต่รอสัญญาณจากปุ่มลัด "สแกนตอนนี้" เท่านั้น
///
///   [ปุ่มลัด] ──▶ จับภาพ 1 เฟรม ──▶ [ช่อง] ──▶ OCR ──▶ [ช่อง] ──▶ แปลด้วย Ollama ──▶ แสดงผล
///
/// OCR กับขั้นแปลยังทำงานเป็นสายพานแยกกันเหมือนเดิม เผื่อกดสแกนรัว ๆ ติดกัน
/// จะได้ไม่ต้องรอให้เฟรมก่อนหน้าแปลเสร็จก่อนเริ่ม OCR เฟรมถัดไป
/// </summary>
public sealed class TranslationPipeline : IDisposable
{
    private readonly AppConfig _config;
    private readonly IOcrEngine _ocr;
    private readonly TranslationCache _cache;
    private readonly ITranslator _translator;
    private readonly GoogleFastTranslator? _draft;
    private readonly LineTracker _tracker = new();

    // แชตดิบไม่กี่บรรทัดล่าสุด ไว้ส่งเป็นบริบทให้โมเดลตอนแปลบรรทัดถัดไปที่อาจกำกวม
    private readonly Queue<string> _recentRaw = new();
    private const int ContextWindow = 5;

    private readonly SemaphoreSlim _trigger = new(0, 1);

    private readonly Channel<Bitmap> _frames =
        Channel.CreateBounded<Bitmap>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly Channel<ChatEntry> _toTranslate =
        Channel.CreateUnbounded<ChatEntry>();

    private ICaptureSource? _source;
    private CancellationTokenSource? _cts;
    private readonly List<Task> _stages = new();

    public event Action<ChatEntry>? EntryAdded;
    public event Action<ChatEntry>? EntryUpdated;
    public event Action<string>? Status;

    /// <summary>ยิงทุกครั้งที่ OCR จบรอบ พร้อมจำนวนบรรทัดใหม่ที่เจอ ไว้ให้ UI โชว์ "กำลังแปล..." ได้ถูกจังหวะ</summary>
    public event Action<int>? ScanResult;

    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public string CaptureBackendName => _source?.Name ?? "-";

    // ตัวเลขไว้แสดงบนหน้าจอ
    public long LastOcrMs { get; private set; }
    public long LastTranslateMs { get; private set; }
    public int CacheHits { get; private set; }
    public int CacheSize => _cache.Count;

    public TranslationPipeline(AppConfig config, IOcrEngine ocr, TranslationCache cache)
    {
        _config = config;
        _ocr = ocr;
        _cache = cache;

        _translator = config.PivotThroughEnglish
            ? new PivotTranslator(
                new OllamaTranslator(config.OllamaUrl, config.OllamaModel, config.OllamaKeepAlive, config.OllamaNumPredict, "English"),
                new GoogleFastTranslator(350, "en", config.PivotTargetCode))
            : config.UseGoogleTranslate
                ? new GoogleFastTranslator()
                : new OllamaTranslator(config.OllamaUrl, config.OllamaModel, config.OllamaKeepAlive, config.OllamaNumPredict, config.TargetLanguage);
        _translator.Warning += m => Status?.Invoke(m);

        // โหมดสองชั้น: Google โชว์คำแปลด่วนทันที ระหว่างที่ LLM แปลตัวจริงอยู่
        // เปิดเฉพาะตอนตัวแปลหลักเป็น LLM (ถ้าใช้ Google อยู่แล้ว/pivot ไม่ต้อง)
        if (config.FastDraft && !config.UseGoogleTranslate && !config.PivotThroughEnglish)
        {
            var draftCode = config.TargetLanguage.Equals("English", StringComparison.OrdinalIgnoreCase) ? "en" : "th";
            _draft = new GoogleFastTranslator(350, "ko", draftCode);
            _draft.Warning += m => Status?.Invoke(m);
        }
    }

    /// <summary>เรียกตอนเปิดโปรแกรมเพื่อสั่งโหลดโมเดล Ollama ล่วงหน้า จะได้ไม่ต้องรอตอนสแกนครั้งแรก</summary>
    public Task<bool> WarmUpTranslatorAsync(CancellationToken token) => _translator.WarmUpAsync(token);

    public bool Start(Rectangle region)
    {
        Stop();

        _source = CreateSource(region);
        if (_source == null)
        {
            Status?.Invoke("เริ่มจับภาพไม่สำเร็จ");
            return false;
        }

        _tracker.Reset();
        _recentRaw.Clear();
        while (_trigger.CurrentCount > 0) _trigger.Wait(0);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _stages.Clear();
        _stages.Add(Task.Run(() => CaptureStage(token), token));
        _stages.Add(Task.Run(() => OcrStage(token), token));
        _stages.Add(Task.Run(() => TranslateStage(token), token));

        Status?.Invoke($"พร้อมสแกน · จับภาพด้วย {_source.Name} · กดปุ่มลัดเพื่อสแกน");
        return true;
    }

    private ICaptureSource? CreateSource(Rectangle region)
    {
        // ลอง WGC ก่อนเพราะเร็วกว่าและจับ Fullscreen Exclusive ได้
        if (_config.Backend is CaptureBackend.Auto or CaptureBackend.WindowsGraphicsCapture
            && WgcCaptureSource.IsSupported())
        {
            var wgc = new WgcCaptureSource();
            if (wgc.Start(region)) return wgc;

            wgc.Dispose();
            Status?.Invoke("Windows Graphics Capture ใช้ไม่ได้ ถอยไปใช้ GDI");

            if (_config.Backend == CaptureBackend.WindowsGraphicsCapture) return null;
        }

        var gdi = new GdiCaptureSource();
        return gdi.Start(region) ? gdi : null;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;

        _source?.Dispose();
        _source = null;

        // ทิ้งเฟรมค้างท่อ
        while (_frames.Reader.TryRead(out var stale)) stale.Dispose();

        _cache.Flush();
    }

    /// <summary>เรียกจากปุ่มลัดสแกน ปลุกขั้นจับภาพให้จับ 1 เฟรมแล้วส่งเข้าสายพาน</summary>
    public void TriggerScan()
    {
        if (!IsRunning) return;

        // ล้างระบบคัดบรรทัดซ้ำทุกครั้งที่สแกนใหม่ เพื่อให้ทุกบรรทัดที่ยังเห็นอยู่บนจอถูกแปลใหม่
        // เสมอ (โผล่ในหน้าต่างคำแปลที่เพิ่งล้างไปหมาด ๆ) ไม่ใช่แค่บรรทัดที่ "ใหม่กว่ารอบก่อน"
        // ไม่กระทบความเร็ว เพราะข้อความซ้ำยังชนแคชแปลอยู่ดี ไม่ต้องยิง Ollama ซ้ำ
        _tracker.Reset();
        _recentRaw.Clear();

        if (_trigger.CurrentCount == 0) _trigger.Release();
        Status?.Invoke("กำลังสแกน…");
    }

    // ---------- ขั้นที่ 1 จับภาพเมื่อได้รับสัญญาณ ----------

    private async Task CaptureStage(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await _trigger.WaitAsync(token); }
            catch (OperationCanceledException) { break; }

            try
            {
                var frame = _source?.Grab();

                if (frame != null)
                {
                    // ช่องเต็มจะทิ้งเฟรมเก่าทิ้งเอง ไม่ให้สายพานตัน
                    if (!_frames.Writer.TryWrite(frame))
                        frame.Dispose();
                }
                else
                {
                    Status?.Invoke("จับภาพไม่สำเร็จ");
                }
            }
            catch (Exception ex)
            {
                Status?.Invoke("จับภาพผิดพลาด: " + ex.Message);
            }
        }
    }

    // ---------- ขั้นที่ 2 OCR ----------

    private async Task OcrStage(CancellationToken token)
    {
        var sw = new Stopwatch();

        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(token))
            {
                try
                {
                    sw.Restart();
                    var lines = _ocr.Read(frame);
                    sw.Stop();
                    LastOcrMs = sw.ElapsedMilliseconds;

                    SaveCapture(frame, lines, "scan");

                    // ใช้ indexer แทน ToDictionary เพราะ OCR อ่านข้อความซ้ำกันในเฟรมเดียวได้
                    // (เช่นแชตสแปมคำเดิม) ToDictionary จะโยน exception ทันทีถ้าเจอคีย์ซ้ำ
                    var byText = new Dictionary<string, GameChatTranslator.Core.Ocr.OcrLine>(
                        StringComparer.Ordinal);
                    foreach (var l in lines) byText[l.Text.Trim()] = l;

                    var fresh = _tracker.FilterNew(lines.Select(l => l.Text));

                    if (fresh.Count == 0)
                        Status?.Invoke("สแกนแล้ว — ไม่มีข้อความใหม่");

                    ScanResult?.Invoke(fresh.Count);

                    foreach (var korean in fresh)
                    {
                        byText.TryGetValue(korean, out var match);

                        var entry = new ChatEntry
                        {
                            Korean = korean,
                            Confidence = match?.Confidence ?? 0f,
                            Expanded = SlangDictionary.Expand(korean, _config.CustomTerms),
                            // สแนปช็อตบริบทตอนนี้ ก่อนเอาบรรทัดนี้เข้าคิว ไม่งั้นบรรทัดจะเห็นตัวเองเป็นบริบทของตัวเอง
                            Context = _recentRaw.ToArray()
                        };

                        EntryAdded?.Invoke(entry);

                        await _toTranslate.Writer.WriteAsync(entry, token);

                        _recentRaw.Enqueue(korean);
                        if (_recentRaw.Count > ContextWindow) _recentRaw.Dequeue();
                    }
                }
                finally
                {
                    frame.Dispose();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---------- ขั้นที่ 3 แปล ----------

    private async Task TranslateStage(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var batch = await CollectBatchAsync(_toTranslate.Reader, token);
                if (batch.Count == 0) continue;

                await TranslateBatchAsync(batch, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// เช็กแคชก่อน แล้วยิงเฉพาะบรรทัดที่ยังไม่เคยแปลไปที่ Ollama อัปเดตผลกลับผ่าน EntryUpdated
    /// ใช้ร่วมกันทั้งสายพานหลักและปุ่มลัด "แปลบางส่วนทันที" (Ctrl+2)
    /// </summary>
    private async Task TranslateBatchAsync(List<ChatEntry> batch, CancellationToken token)
    {
        // ตัดรายการที่เคยแปลแล้วออกก่อน ไม่ต้องยิงโมเดลซ้ำ
        var needTranslation = new List<ChatEntry>();

        foreach (var entry in batch)
        {
            if (_cache.TryGet(entry.Expanded, out var cached))
            {
                CacheHits++;
                entry.Thai = cached;
                entry.State = TranslationState.Fast;
                EntryUpdated?.Invoke(entry);
            }
            else
            {
                needTranslation.Add(entry);
            }
        }

        if (needTranslation.Count == 0 || !_config.UseTranslator) return;

        var sw = Stopwatch.StartNew();

        // ยิง LLM (ตัวจริง) กับ Google (ด่วน) พร้อมกัน — Google มักเสร็จก่อน โชว์เป็นฉบับร่างไว้ก่อน
        var llmTask = _translator.TranslateAsync(needTranslation, token);

        if (_draft != null)
        {
            try
            {
                var draft = await _draft.TranslateAsync(needTranslation, token);
                for (int i = 0; i < needTranslation.Count && i < draft.Count; i++)
                {
                    var entry = needTranslation[i];
                    if (string.IsNullOrWhiteSpace(draft[i])) continue;

                    // เก็บคำแปล Google ไว้เสมอ ให้กดดูเทียบกับ LLM ทีหลังได้
                    entry.GoogleThai = draft[i];

                    // โชว์เป็นฉบับร่างเฉพาะที่ LLM ยังไม่เสร็จ
                    if (entry.State == TranslationState.Pending)
                    {
                        entry.Thai = draft[i];
                        entry.State = TranslationState.Draft;
                        EntryUpdated?.Invoke(entry);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* Google ล่ม/โดนบล็อก — ข้ามฉบับร่าง รอ LLM อย่างเดียว */ }
        }

        var thai = await llmTask;
        sw.Stop();
        LastTranslateMs = sw.ElapsedMilliseconds;

        for (int i = 0; i < needTranslation.Count; i++)
        {
            var entry = needTranslation[i];
            var result = i < thai.Count ? thai[i] : null;

            if (!string.IsNullOrWhiteSpace(result))
            {
                entry.Thai = result;
                entry.State = TranslationState.Fast;
                _cache.Set(entry.Expanded, result);
            }
            else if (entry.State == TranslationState.Draft)
            {
                // LLM แปลไม่ได้ แต่มีฉบับร่างจาก Google อยู่แล้ว — คงไว้ ไม่ต้องถอยเป็นต้นฉบับ
                _tracker.Forget(entry.Korean);
            }
            else
            {
                entry.State = TranslationState.Failed;

                // ลืมบรรทัดนี้จากระบบคัดซ้ำ ไม่งั้นถ้าบรรทัดยังไม่เลื่อนหายไปจากแชต
                // สแกนรอบถัดไปจะมองว่า "เคยเห็นแล้ว" แล้วไม่ลองแปลซ้ำให้อีกเลย
                _tracker.Forget(entry.Korean);
            }

            EntryUpdated?.Invoke(entry);
        }
    }

    /// <summary>
    /// แปล "บางส่วน" แบบครั้งเดียวจากภาพหน้าจอที่ค้างไว้ตอนลากกรอบ
    ///
    /// OCR กรอบที่ขยายขึ้น-ลงเล็กน้อย (ให้บรรทัดที่ถูกขอบกรอบตัดครึ่งปรากฏเต็มบรรทัด) แล้ว
    /// คัดเก็บเฉพาะบรรทัดที่ "ทั้งกล่อง" อยู่ในกรอบที่ลากจริง ๆ — บรรทัดข้าง ๆ ที่ถูกขอบตัด
    /// (เช่นตอนแชตกำลังไหล) จะล้นออกไปแล้วถูกตัดทิ้ง ไม่ถูกส่งไปแปล
    ///
    /// ยิงผลผ่าน EntryAdded/EntryUpdated เหมือนสายพานปกติ ทำงานได้แม้ยังไม่ได้กด "เริ่มแปล"
    /// รับหน้าที่ Dispose <paramref name="snapshot"/> ให้เอง
    /// </summary>
    public async Task TranslateHighlightAsync(Bitmap? snapshot, Rectangle box, CancellationToken token)
    {
        if (snapshot == null)
        {
            Status?.Invoke("จับภาพไม่สำเร็จ");
            ScanResult?.Invoke(0);
            return;
        }

        using (snapshot)
        {
            box.Intersect(new Rectangle(0, 0, snapshot.Width, snapshot.Height));
            if (box.Width <= 0 || box.Height <= 0)
            {
                ScanResult?.Invoke(0);
                return;
            }

            // Paddle (คืนกล่อง): เผื่อขอบเยอะได้ แล้วคัดตามการซ้อนทับ ≥ครึ่งบรรทัดทีหลัง
            // VL (ไม่คืนกล่อง): ครอปตรงกรอบที่ลากเป๊ะ (margin 0) + prompt StrictLines สั่งเอาเฉพาะบรรทัดที่เห็นเกินครึ่ง
            //   — เผื่อขอบแม้ 12px ก็ดูดบรรทัดข้าง ๆ ที่สูงพอ ๆ กันเข้ามาทั้งเส้น
            bool boxed = _ocr.ProvidesBoxes;
            int margin = boxed ? 12 : 0;
            var ocrRect = Rectangle.Inflate(box, 0, margin);
            ocrRect.Intersect(new Rectangle(0, 0, snapshot.Width, snapshot.Height));

            // OCR หนักพอที่จะทำให้ UI ค้างถ้ารันบนเธรดหลัก — ยกไปเธรดพื้นหลังเหมือนสายพานปกติ
            var lines = await Task.Run(() =>
            {
                using var crop = Crop(snapshot, ocrRect);
                if (!boxed) _ocr.StrictLines = true;   // VL: ทิ้งบรรทัดข้าง ๆ ที่ถูกขอบ crop หั่นครึ่งเอง
                try
                {
                    var r = _ocr.Read(crop);
                    SaveCapture(crop, r, "highlight");
                    return r;
                }
                finally { if (!boxed) _ocr.StrictLines = false; }
            }, token);

            // คัดบรรทัดตามการซ้อนทับแนวตั้งกับกรอบที่ลาก (ไม่บังคับว่าต้องอยู่ครบทั้งบรรทัด)
            // — ผู้ใช้ลากเฉือนหัวบรรทัดแรกนิดหน่อยเป็นเรื่องปกติ ขอแค่เกินครึ่งบรรทัดอยู่ในกรอบก็เอา
            var kept = new List<GameChatTranslator.Core.Ocr.OcrLine>();
            foreach (var l in lines)
            {
                // โมเดล VL ไม่คืนกล่องพิกัด — คัดด้วย prompt StrictLines ไปแล้ว เก็บทั้งหมด
                if (l.Box.IsEmpty) { kept.Add(l); continue; }

                var b = l.Box;
                b.Offset(ocrRect.X, ocrRect.Y);

                int overlap = Math.Min(b.Bottom, box.Bottom) - Math.Max(b.Top, box.Top);
                bool mostlyInside = overlap >= 0.5 * Math.Max(1, b.Height);
                bool horizontalOverlap = b.Right > box.Left && b.Left < box.Right;

                if (mostlyInside && horizontalOverlap) kept.Add(l);
            }

            await EmitLinesAsync(kept, token);
        }
    }

    /// <summary>ตัดบรรทัดที่ OCR อ่านได้ (คัดซ้ำ) → สร้าง ChatEntry → ยิง EntryAdded → แปลทั้งชุด</summary>
    private async Task EmitLinesAsync(
        IReadOnlyList<GameChatTranslator.Core.Ocr.OcrLine> lines, CancellationToken token)
    {
        var byText = new Dictionary<string, GameChatTranslator.Core.Ocr.OcrLine>(StringComparer.Ordinal);
        var fresh = new List<string>();
        foreach (var l in lines)
        {
            var t = l.Text.Trim();
            if (t.Length == 0) continue;
            if (byText.TryAdd(t, l)) fresh.Add(t);
        }

        if (fresh.Count == 0)
            Status?.Invoke("สแกนแล้ว — ไม่มีข้อความในกรอบ");

        ScanResult?.Invoke(fresh.Count);
        if (fresh.Count == 0) return;

        var entries = new List<ChatEntry>();
        foreach (var korean in fresh)
        {
            byText.TryGetValue(korean, out var match);

            var entry = new ChatEntry
            {
                Korean = korean,
                Confidence = match?.Confidence ?? 0f,
                Expanded = SlangDictionary.Expand(korean, _config.CustomTerms),
                Color = match?.Color,
                Context = Array.Empty<string>()
            };

            entries.Add(entry);
            EntryAdded?.Invoke(entry);
        }

        await TranslateBatchAsync(entries, token);
    }

    // ---------- เก็บภาพที่แคป + ผลอ่าน ไว้ทำชุดข้อมูลเทรน OCR ในอนาคต ----------

    private long _captureSeq;

    /// <summary>
    /// เซฟภาพที่ส่งให้ OCR + ข้อความที่อ่านได้ (เป็น label เริ่มต้น แก้เองทีหลังได้)
    /// ไปที่ %AppData%\GameChatTranslator\captures\ เมื่อ AppConfig.SaveCaptures = true
    /// คู่ไฟล์: stamp.png + stamp.txt (บรรทัดละข้อความที่ OCR อ่านได้)
    /// </summary>
    private void SaveCapture(Bitmap image, IReadOnlyList<GameChatTranslator.Core.Ocr.OcrLine> lines, string tag)
    {
        if (!_config.SaveCaptures) return;

        try
        {
            var dir = string.IsNullOrWhiteSpace(_config.CaptureDir)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "GameChatTranslator", "captures")
                : _config.CaptureDir;
            Directory.CreateDirectory(dir);

            var stamp = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Interlocked.Increment(ref _captureSeq):D4}-{tag}";

            // clone กัน bitmap โดน dispose ระหว่างเซฟ (ผู้เรียกถือสิทธิ์ dispose เดิม)
            using (var copy = new Bitmap(image))
                copy.Save(Path.Combine(dir, stamp + ".png"), ImageFormat.Png);

            var sb = new StringBuilder();
            foreach (var l in lines) sb.AppendLine(l.Text);
            File.WriteAllText(Path.Combine(dir, stamp + ".txt"), sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // เซฟไม่ได้ (ดิสก์เต็ม/สิทธิ์) ไม่ควรทำให้การแปลหยุด
        }
    }

    private static Bitmap Crop(Bitmap source, Rectangle rect)
    {
        var crop = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(crop);
        g.DrawImage(source, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
        return crop;
    }

    /// <summary>
    /// รอบรรทัดแรก แล้วสะสมเพิ่มอีกไม่เกิน BatchWaitMs หรือจนครบ BatchSize
    /// ทำให้ผลจากการสแกนหนึ่งครั้งที่ได้หลายบรรทัดถูกรวมยิงครั้งเดียว
    /// </summary>
    private async Task<List<ChatEntry>> CollectBatchAsync(
        ChannelReader<ChatEntry> reader, CancellationToken token)
    {
        var batch = new List<ChatEntry>();

        var first = await reader.ReadAsync(token);
        batch.Add(first);

        var deadline = Stopwatch.StartNew();

        while (batch.Count < _config.BatchSize &&
               deadline.ElapsedMilliseconds < _config.BatchWaitMs)
        {
            if (reader.TryRead(out var next))
            {
                batch.Add(next);
                continue;
            }

            try { await Task.Delay(30, token); }
            catch (OperationCanceledException) { break; }
        }

        return batch;
    }

    public void Dispose()
    {
        Stop();
        _ocr.Dispose();
    }
}
