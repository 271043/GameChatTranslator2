using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameChatTranslator.Core.Pipeline;

namespace GameChatTranslator.Core.Translate;

public interface ITranslator
{
    string Name { get; }
    event Action<string>? Warning;
    Task<bool> WarmUpAsync(CancellationToken token);
    Task<List<string>> TranslateAsync(List<ChatEntry> entries, CancellationToken token);
}

/// <summary>
/// แปลด้วยโมเดลที่รันอยู่บนเครื่องนี้เองผ่าน Ollama (http://localhost:11434)
/// ไม่ต้องต่อเน็ต ไม่มีโควตา ไม่มีเรื่องโดนบล็อก IP แลกกับความเร็วที่ขึ้นกับสเปกเครื่อง
///
/// ยิงทีละบรรทัดเสมอ ไม่รวมหลายบรรทัดเป็นคำขอเดียว — วัดจริงพบว่าถ้ารวมบรรทัดแล้วมีบรรทัดที่
/// โมเดลไม่เข้าใจปนอยู่ (เช่นไม่มีข้อความหลังชื่อผู้เล่น) โมเดลมักจะเอ็คโค่ทั้งก้อนกลับมาเฉย ๆ
/// โดยที่จำนวนส่วนยังตรงตามที่ขอ ทำให้ตรวจจับความผิดพลาดไม่ได้ ยิงทีละบรรทัดแยกกันแก้ปัญหานี้ได้เด็ดขาด
/// และเพราะเป็นโมเดลบนเครื่องเอง (ไม่ใช่เครือข่ายภายนอก) การยิงถี่ขึ้นไม่มีต้นทุนเรื่องโควตา/บล็อก
/// </summary>
public sealed class OllamaTranslator : ITranslator
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _keepAlive;
    private readonly int _numPredict;
    private readonly string _targetLang;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Name => "Ollama";

    /// <summary>ยิงเมื่อเชื่อมต่อไม่ได้หรือโมเดลตอบผิดปกติ เพื่อให้ UI แจ้งผู้ใช้ได้</summary>
    public event Action<string>? Warning;

    public OllamaTranslator(string baseUrl, string model, string keepAlive = "30m", int numPredict = 768, string targetLang = "English")
    {
        _model = model;
        _keepAlive = keepAlive;
        _targetLang = string.IsNullOrWhiteSpace(targetLang) ? "English" : targetLang.Trim();
        // เพดานโทเคนคำตอบ — โมเดล reasoning (gpt-oss) ต้องเผื่อ token ให้ reasoning ก่อนถึงคำแปลจริง
        // ไม่งั้นชนเพดานก่อนได้คำตอบแล้วตอบว่าง วัดจริง gpt-oss:120b ต้อง ~600, qwen3.x หยุดเองที่ ~15
        _numPredict = numPredict < 64 ? 64 : numPredict;
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            // โมเดลใหญ่ (เช่น gpt-oss:120b บนเครื่องรีโมต) โหลดครั้งแรกนานหลายนาที
            Timeout = TimeSpan.FromSeconds(300)
        };
    }

    /// <summary>
    /// เรียกตอนเปิดโปรแกรม เพื่อสั่งให้ Ollama โหลดโมเดลขึ้น RAM/VRAM ทันที
    /// จะได้ไม่ต้องรอโหลดตอนกดสแกนครั้งแรก — ส่ง prompt ว่างไปที่ /api/generate
    /// ซึ่ง Ollama จะแค่โหลดโมเดลค้างไว้โดยไม่สร้างข้อความตอบกลับ
    /// </summary>
    public async Task<bool> WarmUpAsync(CancellationToken token)
    {
        try
        {
            var payload = new { model = _model, prompt = "", keep_alive = _keepAlive };

            using var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await _http.PostAsync("/api/generate", content, token);

            if (!response.IsSuccessStatusCode)
            {
                Warning?.Invoke($"โหลดโมเดล Ollama ไม่สำเร็จ ({(int)response.StatusCode})");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (HttpRequestException ex)
        {
            Warning?.Invoke("เชื่อมต่อ Ollama ไม่ได้ — ตรวจว่ารัน `ollama serve` อยู่หรือยัง: " + ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Warning?.Invoke("โหลดโมเดล Ollama ไม่สำเร็จ: " + ex.Message);
            return false;
        }
    }

    // จับ "[แชนแนล] ชื่อผู้เล่น:" หรือแค่ "ชื่อผู้เล่น:" นำหน้าข้อความ เผื่อมีสัญลักษณ์ ' หรือ | ปนมาจาก OCR
    private static readonly Regex PrefixPattern =
        new(@"^\s*(?:['|]\s*)?(?:\[[^\]]+\]\s*)?[^:：\n]{1,20}[:：]\s*", RegexOptions.Compiled);

    /// <summary>
    /// ต่อ "ชื่อ:" กับคำแปลกลับเป็นบรรทัดเดียว บาง OCR อ่านมาแบบไม่มีช่องว่างหลัง ":" เลย
    /// (เช่น "이름:안녕") ทำให้คำแปลไปติดกับ ":" พอดี อ่านยาก จึงเติมช่องว่างให้เองเสมอถ้ายังไม่มี
    /// </summary>
    private static string JoinPrefix(string prefix, string message)
    {
        if (prefix.Length == 0) return message;
        var trimmed = prefix.TrimEnd();
        return (trimmed.EndsWith(':') || trimmed.EndsWith('：')) ? trimmed + " " + message : prefix + message;
    }

    public async Task<List<string>> TranslateAsync(List<ChatEntry> entries, CancellationToken token)
    {
        var results = new List<string>(new string[entries.Count]);
        if (entries.Count == 0) return results;

        // กันสแกนหลายรอบยิงทับกันจนโมเดลรับไม่ไหว — ให้แต่ละแบตช์เข้าคิวทีละก้อน
        await _gate.WaitAsync(token);
        try
        {
            // ยิงหลายบรรทัดในแบตช์พร้อมกัน (ไม่เกิน MaxParallel) เครื่องรีโมตตั้ง OLLAMA_NUM_PARALLEL=3
            // การยิงทีละบรรทัดแบบเดิมทำให้แบตช์ 5 บรรทัดช้าเป็น 5 เท่าโดยไม่จำเป็น
            const int MaxParallel = 3;
            using var slots = new SemaphoreSlim(MaxParallel, MaxParallel);

            var tasks = new Task[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                int idx = i;
                tasks[i] = Task.Run(async () =>
                {
                    await slots.WaitAsync(token);
                    try { results[idx] = await TranslateOneAsync(entries[idx], token); }
                    finally { slots.Release(); }
                }, token);
            }

            await Task.WhenAll(tasks);
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> TranslateOneAsync(ChatEntry entry, CancellationToken token)
    {
        // แยกส่วน "[แชนแนล] ชื่อ:" ออกจากข้อความจริงเอง ไม่ให้โมเดลยุ่งกับส่วนนี้เลย
        // วัดจริงพบว่าถ้าปล่อยให้โมเดลจัดการเอง บางทีมันแปล/แปลงชื่อผู้เล่นเป็นอังกฤษ
        // ทั้งที่สั่งห้ามแล้ว (เช่น "최수빈" กลายเป็น "Choi Soo-bin") วิธีนี้การันตีว่าชื่อไม่โดนแตะแน่นอน
        var line = entry.Expanded;
        var prefixMatch = PrefixPattern.Match(line);
        var prefix = prefixMatch.Success ? prefixMatch.Value : string.Empty;
        var message = line[prefix.Length..];

        if (string.IsNullOrWhiteSpace(message)) return string.Empty;

        // ความผิดพลาด (เอ็คโค่/หลุดภาษาอื่น) เป็นแบบสุ่ม ไม่ใช่พังทุกครั้งกับบรรทัดเดิม
        // วัดจริงพบว่าลองซ้ำแล้วมักผ่านในไม่กี่ครั้ง คุ้มกว่าปล่อยให้ขึ้น "แปลไม่สำเร็จ" ทันที
        const int maxAttempts = 2;
        var cleaned = string.Empty;

        for (int attempt = 0; attempt < maxAttempts && cleaned.Length == 0; attempt++)
        {
            var reply = await RequestAsync(BuildPrompt(message, entry.Context), token);
            cleaned = reply == null ? string.Empty : CleanOrEmpty(message, reply);
        }

        return cleaned.Length == 0 ? string.Empty : JoinPrefix(prefix, cleaned);
    }

    /// <summary>
    /// สามอาการที่วัดจริงเจอจากโมเดลเล็ก (7-9B): (1) เอ็คโค่ข้อความเกาหลีต้นฉบับกลับมาเฉย ๆ
    /// (2) แปลค้างฮันกึลบางส่วน (3) หลุดตัวอักษรภาษาอื่นปนมา (เจอจริงว่าซีริลลิก/จีนหลุดมาแทรกกลางคำ)
    /// ทั้งสามแบบต้องกันไว้ ไม่งั้นจะโชว์ข้อความผิดภาษาทับที่ที่ควรเป็นคำแปล
    /// </summary>
    private static string CleanOrEmpty(string original, string translated)
    {
        var trimmed = StripEchoedPrefix(original, translated.Trim());
        if (trimmed.Length == 0) return string.Empty;

        var isEcho = string.Equals(trimmed, original.Trim(), StringComparison.Ordinal);
        if (isEcho) return string.Empty;

        return ContainsForeignLeak(trimmed) ? string.Empty : trimmed;
    }

    /// <summary>
    /// บางครั้งโมเดลใส่ต้นฉบับเกาหลีนำหน้าคำแปลแล้วคั่นด้วย "-&gt;" ทั้งที่สั่งให้ตอบแค่คำแปล
    /// (เช่น "같이 사냥 가자 -&gt; ไปล่าสัตว์ด้วยกันเถอะ") ตัดส่วนต้นฉบับ+ตัวคั่นทิ้ง เหลือแต่คำแปลจริง
    /// </summary>
    private static string StripEchoedPrefix(string original, string translated)
    {
        var trimmedOriginal = original.Trim();
        if (trimmedOriginal.Length == 0 || !translated.StartsWith(trimmedOriginal, StringComparison.Ordinal))
            return translated;

        var rest = translated[trimmedOriginal.Length..].TrimStart().TrimStart('-', '>', '→', ':', '-').TrimStart();
        return rest.Length > 0 ? rest : translated;
    }

    /// <summary>ฮันกึล/ซีริลลิก/CJK ปนมา — ไม่มีทางเป็นส่วนหนึ่งของคำแปลไทย/อังกฤษที่ถูกต้อง</summary>
    private static bool ContainsForeignLeak(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '가' and <= '힣' or >= '㄰' and <= '㆏')   // ฮันกึล
                return true;
            if (c is >= 'Ѐ' and <= 'ӿ')                  // ซีริลลิก
                return true;
            if (c is >= '一' and <= '鿿')                  // CJK (จีน/คันจิ)
                return true;
        }
        return false;
    }

    private string BuildPrompt(string message, IReadOnlyList<string> context)
    {
        var sb = new StringBuilder();

        // บริบทช่วยข้อความสั้นกำกวม เช่น "길!" เดี่ยว ๆ แปลไม่ได้ แต่ถ้ารู้ว่าแชตก่อนหน้า
        // กำลังคุยเรื่องอะไรอยู่ (เช่นชวนไปดันเจี้ยน) ก็เดาความหมายที่ตรงบริบทได้แม่นขึ้นมาก
        if (context.Count > 0)
        {
            sb.Append("For context, here are the last few chat lines (do NOT translate these, ")
              .Append("they're only so you understand what's being discussed):\n");
            foreach (var c in context) sb.Append("- ").Append(c).Append('\n');
            sb.Append('\n');
        }

        sb.Append("This is live chat from a Korean MMORPG (guild/party chat). Translate the message below into ")
          .Append("natural, casual ").Append(_targetLang).Append(" the way gamers actually talk")
          .Append(context.Count > 0 ? ", using the context above to resolve ambiguous or very short words." : ".")
          .Append("\n\nRules:\n")
          .Append("- Expand Korean gamer slang to its real meaning, don't translate it letter by letter ")
          .Append("(ㄱㄱ=let's go, ㅈㅅ=sorry, ㄴㄴ=no, ㅇㅋ=ok, ㅇㅈ=agreed, ㅃㅃ=bye, 팟=party, 딜=DPS/damage, ")
          .Append("힐=heal, 탱=tank, 각=opening/timing, 렉=lag, 버스=carry, 뻥=bluff/lie, ㅊㅋ=congrats, ")
          .Append("레이드=raid, 몸입금=pay the entry fee/deposit to join).\n")
          .Append("- Keep player names, guild names, numbers, item names and English as-is.\n")
          .Append("- Even if the text looks garbled (OCR misread), still output your best-guess translation.\n")
          .Append("- Reply with ONLY the ").Append(_targetLang)
          .Append(" translation. Never mix in Korean, Chinese, or Cyrillic characters — ")
          .Append("only ").Append(_targetLang).Append(" script plus preserved English/numbers. No quotes, no notes.\n\n");

        // ตัวอย่างช่วยล็อกโทน — ให้เฉพาะภาษาที่เตรียมไว้ ภาษาอื่นปล่อยให้โมเดลจัดการเอง
        if (_targetLang.Equals("English", StringComparison.OrdinalIgnoreCase))
            sb.Append("Examples:\n")
              .Append("ㄱㄱ 렉 그만 → let's go, stop lagging\n")
              .Append("힐러 한 명만 더 팟 구함 → need one more healer for the party\n")
              .Append("각 나오면 콜 해줘 → call it when there's an opening\n")
              .Append("ㅃㅃ → bye\n")
              .Append("버스 태워줄 사람? → anyone giving a carry?\n")
              .Append("오늘 레이드 몇시임? → what time's the raid today?\n")
              .Append("몸입금? → paid the entry fee yet?\n\n");
        else if (_targetLang.Equals("Thai", StringComparison.OrdinalIgnoreCase))
            sb.Append("Examples:\n")
              .Append("ㄱㄱ 렉 그만 → ไปกันเลย เลิกแลคซะที\n")
              .Append("힐러 한 명만 더 팟 구함 → หาฮีลอีกคนเข้าปาร์ตี้\n")
              .Append("각 나오면 콜 해줘 → มีจังหวะแล้วบอกด้วย\n")
              .Append("ㅃㅃ → บาย\n")
              .Append("버스 태워줄 사람? → มีใครพาไปบ้างมั้ย?\n")
              .Append("오늘 레이드 몇시임? → วันนี้เรดกี่โมง?\n")
              .Append("몸입금? → จ่ายค่าเข้าร่วมหรือยัง?\n\n");

        sb.Append("Message:\n").Append(message);

        return sb.ToString();
    }

    private async Task<string?> RequestAsync(string prompt, CancellationToken token)
    {
        try
        {
            var payload = new
            {
                model = _model,
                stream = false,
                keep_alive = _keepAlive,
                // qwen3.x มีโหมด "คิดก่อนตอบ" ที่ปล่อย reasoning token ยาวก่อนถึงคำตอบจริง
                // วัดจริง: qwen3.6:27b ถ้าเปิด thinking จะชน num_predict ก่อนได้คำแปลด้วยซ้ำ (ตอบว่าง)
                // ปิดด้วย think=false ตอบสั้นตรงประเด็นทันที เหมาะกับงานแปลแชตสั้น ๆ
                // (ค่า boolean เท่านั้น — qwen3 ไม่รับ string level แบบ gpt-oss)
                think = false,
                // ที่ค่าอุณหภูมิปกติ (~0.8) โมเดลมักจะ "แก้" หรือเอ็คโค่ข้อความเกาหลีที่ป้อนเข้าไปกลับมาเฉย ๆ
                // แทนที่จะแปล วัดจริงพบว่าพลาดแบบนี้ราวครึ่งหนึ่งของครั้ง ลดอุณหภูมิเหลือ 0.1 แล้วพลาด 0/24 ครั้ง
                // โมเดลเล็ก (7-9B) หลุดภาษา/หลอนง่ายกว่าโมเดลใหญ่ที่อุณหภูมิสูง กด temperature เหลือ 0 ให้นิ่งสุด
                options = new { temperature = 0.0, num_predict = _numPredict },
                messages = new[]
                {
                    new { role = "system", content = $"You are a Korean-to-{_targetLang} game-chat localizer specializing in MMORPG chat and gamer slang. Output only the natural, casual {_targetLang} translation — nothing else, and never leave Korean text in the output." },
                    new { role = "user", content = prompt }
                }
            };

            using var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await _http.PostAsync("/api/chat", content, token);

            if (!response.IsSuccessStatusCode)
            {
                Warning?.Invoke($"Ollama ปฏิเสธคำขอ ({(int)response.StatusCode})");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(token);
            return Parse(json);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException ex)
        {
            Warning?.Invoke("เชื่อมต่อ Ollama ไม่ได้ — ตรวจว่ารัน `ollama serve` อยู่หรือยัง: " + ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            Warning?.Invoke("เรียก Ollama ไม่สำเร็จ: " + ex.Message);
            return null;
        }
    }

    /// <summary>ตอบกลับจาก /api/chat มีรูปแบบ {"message":{"role":"assistant","content":"..."}}</summary>
    private static string? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("message").GetProperty("content").GetString();
        }
        catch
        {
            return null;
        }
    }
}
