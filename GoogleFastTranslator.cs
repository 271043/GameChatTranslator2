using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using GameChatTranslator.Core.Pipeline;

namespace GameChatTranslator.Core.Translate;

/// <summary>
/// แปลด้วย endpoint ฟรีของ Google Translate — เร็ว (80–200 ms) ไม่กินสเปกเครื่อง ไม่ต้องมี Ollama
/// แลกกับ: ต้องต่อเน็ต ไม่เข้าใจบริบท และมีโอกาสโดนบล็อกถ้ายิงรัวเกินไป
///
/// กันโดนบล็อกด้วยการเว้นจังหวะขั้นต่ำระหว่างคำขอ และถอยแบบทวีคูณเมื่อโดนปฏิเสธ
/// รวมหลายบรรทัดในแบตช์เป็นคำขอเดียว แยกกลับด้วยตัวคั่น "@@@" ที่ Google ไม่แตะ
/// </summary>
public sealed class GoogleFastTranslator : ITranslator
{
    private readonly HttpClient _http;
    private readonly int _minGapMs;
    private readonly string _sl;
    private readonly string _tl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _sinceLast = Stopwatch.StartNew();

    private int _consecutiveFailures;

    public string Name => "Google";

    public event Action<string>? Warning;

    // แยกส่วน "[แชนแนล] ชื่อ:" นำหน้าออกมา ไม่ให้ Google แปลง/ทับศัพท์ชื่อผู้เล่น
    private static readonly Regex PrefixPattern =
        new(@"^\s*(?:['|]\s*)?(?:\[[^\]]+\]\s*)?[^:：\n]{1,20}[:：]\s*", RegexOptions.Compiled);

    public GoogleFastTranslator(int minGapMs = 350, string sourceLang = "ko", string targetLang = "th")
    {
        _minGapMs = minGapMs;
        _sl = string.IsNullOrWhiteSpace(sourceLang) ? "auto" : sourceLang.Trim();
        _tl = string.IsNullOrWhiteSpace(targetLang) ? "th" : targetLang.Trim();

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
    }

    /// <summary>ไม่มีอะไรต้องอุ่นเครื่อง — มีไว้ให้ครบ interface เท่านั้น</summary>
    public Task<bool> WarmUpAsync(CancellationToken token) => Task.FromResult(true);

    public async Task<List<string>> TranslateAsync(List<ChatEntry> entries, CancellationToken token)
    {
        var results = new List<string>(new string[entries.Count]);
        if (entries.Count == 0) return results;

        // แยก prefix ("ชื่อ:") ออกจากข้อความจริง แปลเฉพาะเนื้อความ แล้วต่อ prefix กลับ
        var prefixes = new string[entries.Count];
        var bodies = new List<string>(entries.Count);

        for (int i = 0; i < entries.Count; i++)
        {
            var line = entries[i].Expanded;
            var m = PrefixPattern.Match(line);
            prefixes[i] = m.Success ? m.Value : string.Empty;
            bodies.Add(line[prefixes[i].Length..]);
        }

        var tr = await TranslateLinesAsync(bodies, token);

        for (int i = 0; i < entries.Count; i++)
            results[i] = string.IsNullOrEmpty(tr[i]) ? string.Empty : JoinPrefix(prefixes[i], tr[i]);

        return results;
    }

    /// <summary>
    /// แปลข้อความดิบทีละก้อน (ไม่ยุ่งกับ prefix) — รวมทุกบรรทัดเป็นคำขอเดียวคั่นด้วย "@@@"
    /// ใช้ตรง ๆ จาก PivotTranslator (ขั้น English → ไทย)
    /// </summary>
    public async Task<List<string>> TranslateLinesAsync(List<string> lines, CancellationToken token)
    {
        var results = new List<string>(new string[lines.Count]);
        if (lines.Count == 0) return results;

        var idx = new List<int>(lines.Count);
        var msgs = new List<string>(lines.Count);

        for (int i = 0; i < lines.Count; i++)
        {
            var s = (lines[i] ?? string.Empty).Replace("@", "").Trim();
            if (s.Length == 0) { results[i] = string.Empty; continue; }
            idx.Add(i);
            msgs.Add(s);
        }

        if (msgs.Count == 0) return results;

        const string sep = "\n@@@\n";
        var translated = await RequestAsync(string.Join(sep, msgs), token);
        if (translated == null) return results;

        var parts = translated.Split("@@@", StringSplitOptions.None);
        for (int k = 0; k < idx.Count; k++)
            results[idx[k]] = k < parts.Length ? parts[k].Trim() : string.Empty;

        return results;
    }

    private static string JoinPrefix(string prefix, string message)
    {
        if (prefix.Length == 0) return message;
        var trimmed = prefix.TrimEnd();
        return (trimmed.EndsWith(':') || trimmed.EndsWith('：')) ? trimmed + " " + message : prefix + message;
    }

    private async Task<string?> RequestAsync(string text, CancellationToken token)
    {
        await _gate.WaitAsync(token);

        try
        {
            int wait = _minGapMs - (int)_sinceLast.ElapsedMilliseconds;
            if (wait > 0) await Task.Delay(wait, token);

            if (_consecutiveFailures > 0)
            {
                int backoff = Math.Min(30000, 500 * (int)Math.Pow(2, _consecutiveFailures));
                await Task.Delay(backoff, token);
            }

            // client=dict-chrome-ex คือ endpoint ที่ส่วนขยาย Google Dictionary ของ Chrome ใช้
            // โดนบล็อกยากกว่า translate_a/single มาก
            var url =
                "https://clients5.google.com/translate_a/t" +
                $"?client=dict-chrome-ex&sl={_sl}&tl={_tl}&q=" + HttpUtility.UrlEncode(text);

            using var response = await _http.GetAsync(url, token);
            _sinceLast.Restart();

            if (!response.IsSuccessStatusCode)
            {
                _consecutiveFailures++;
                Warning?.Invoke($"Google ปฏิเสธคำขอ ({(int)response.StatusCode}) กำลังถอยแล้วลองใหม่");
                return null;
            }

            _consecutiveFailures = 0;

            var json = await response.Content.ReadAsStringAsync(token);
            return Parse(json);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            Warning?.Invoke("เชื่อมต่อ Google ไม่สำเร็จ: " + ex.Message);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// endpoint dict-chrome-ex ตอบได้ 2 แบบ: อาร์เรย์ที่มีสตริงเดียว ["คำแปล"]
    /// หรือรูป sentences [[["คำแปล","ต้นฉบับ",...]],...] — รองรับทั้งคู่
    /// </summary>
    private static string? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                && root[0].ValueKind == JsonValueKind.String)
                return root[0].GetString();

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("sentences", out var sentences)
                && sentences.ValueKind == JsonValueKind.Array)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var s in sentences.EnumerateArray())
                    if (s.TryGetProperty("trans", out var trans))
                        sb.Append(trans.GetString());
                return sb.Length > 0 ? sb.ToString() : null;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
