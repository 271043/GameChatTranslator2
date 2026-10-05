using System.Text.RegularExpressions;
using GameChatTranslator.Core.Pipeline;

namespace GameChatTranslator.Core.Translate;

/// <summary>
/// แปลสองต่อผ่านภาษาอังกฤษ: เกาหลี → อังกฤษ (LLM ผ่าน Ollama) → ไทย (Google)
///
/// เหตุผล: KO→EN เป็นคู่ภาษาที่โมเดลเก่งสุด (จับแสลงเกม/บริบทได้ดี) ส่วน EN→TH ก็เป็นคู่ที่
/// Google แปลได้ดีและเร็ว รวมกันได้ไทยที่เป็นธรรมชาติกว่าแปล KO→TH ตรง ๆ แลกกับดีเลย์เพิ่ม ~150 ms
/// </summary>
public sealed class PivotTranslator : ITranslator
{
    private readonly OllamaTranslator _toEnglish;
    private readonly GoogleFastTranslator _toFinal;

    // แยก "[แชนแนล] ชื่อ:" ที่ยังติดมากับผล English ออก ก่อนส่งเนื้อความให้ Google
    private static readonly Regex PrefixPattern =
        new(@"^\s*(?:['|]\s*)?(?:\[[^\]]+\]\s*)?[^:：\n]{1,20}[:：]\s*", RegexOptions.Compiled);

    public string Name => "KO→EN→TH";

    public event Action<string>? Warning;

    public PivotTranslator(OllamaTranslator toEnglish, GoogleFastTranslator toFinal)
    {
        _toEnglish = toEnglish;
        _toFinal = toFinal;
        _toEnglish.Warning += m => Warning?.Invoke(m);
        _toFinal.Warning += m => Warning?.Invoke(m);
    }

    public Task<bool> WarmUpAsync(CancellationToken token) => _toEnglish.WarmUpAsync(token);

    public async Task<List<string>> TranslateAsync(List<ChatEntry> entries, CancellationToken token)
    {
        var results = new List<string>(new string[entries.Count]);
        if (entries.Count == 0) return results;

        // ขั้น 1: KO → English (OllamaTranslator แยก prefix + ต่อกลับเองแล้ว ผลจะเป็น "ชื่อ: english text")
        var english = await _toEnglish.TranslateAsync(entries, token);

        // ขั้น 2: แยก prefix เกาหลีออก ส่งเฉพาะเนื้อ English ให้ Google → ไทย แล้วต่อ prefix เดิมกลับ
        var prefixes = new string[english.Count];
        var bodies = new List<string>(english.Count);

        for (int i = 0; i < english.Count; i++)
        {
            var line = english[i] ?? string.Empty;
            var m = PrefixPattern.Match(line);
            prefixes[i] = m.Success ? m.Value : string.Empty;
            bodies.Add(line[prefixes[i].Length..]);
        }

        var final = await _toFinal.TranslateLinesAsync(bodies, token);

        for (int i = 0; i < entries.Count; i++)
        {
            var body = i < final.Count ? final[i] : string.Empty;
            if (string.IsNullOrWhiteSpace(body)) { results[i] = string.Empty; continue; }
            results[i] = prefixes[i].Length == 0 ? body : JoinPrefix(prefixes[i], body);
        }

        return results;
    }

    private static string JoinPrefix(string prefix, string message)
    {
        if (prefix.Length == 0) return message;
        var trimmed = prefix.TrimEnd();
        return (trimmed.EndsWith(':') || trimmed.EndsWith('：')) ? trimmed + " " + message : prefix + message;
    }
}
