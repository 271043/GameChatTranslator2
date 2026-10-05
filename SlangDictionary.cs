using System.Text;
using System.Text.RegularExpressions;

namespace GameChatTranslator.Core.Translate;

/// <summary>
/// แชตเกมเกาหลีเต็มไปด้วยตัวย่อพยัญชนะเดี่ยว ซึ่งเครื่องแปลทั่วไปแปลไม่ออกเลย
/// แทนคำพวกนี้ด้วยประโยคเกาหลีเต็มก่อนส่งไปแปล จะได้คำแปลไทยที่ตรงความหมาย
/// </summary>
public static class SlangDictionary
{
    private static readonly Dictionary<string, string> Map = new()
    {
        // หัวเราะ
        ["ㅋㅋ"] = "ㅎㅎ 웃음",
        ["ㅎㅎ"] = "웃음",
        // ตอบรับ ปฏิเสธ
        ["ㅇㅇ"] = "응",
        ["ㄴㄴ"] = "아니",
        ["ㅇㅈ"] = "인정",
        ["ㅁㅈ"] = "맞아",
        // มารยาท
        ["ㄱㅅ"] = "감사합니다",
        ["ㅅㄱ"] = "수고하셨습니다",
        ["ㅈㅅ"] = "죄송합니다",
        ["ㅊㅋ"] = "축하합니다",
        ["ㅎㅇ"] = "안녕하세요",
        ["ㅂㅂ"] = "안녕히 가세요",
        // ชวนเล่น
        ["ㄱㄱ"] = "가자",
        ["ㄱㄷ"] = "기다려",
        ["ㅅㅂ"] = "젠장",
        ["ㅃㅃ"] = "빨리빨리",
        ["ㅈㄴ"] = "정말",
        ["ㄷㄷ"] = "대단하다",
        ["ㅜㅜ"] = "슬퍼요",
        ["ㅠㅠ"] = "슬퍼요",
        // ศัพท์เกม
        ["ㅍㅌ"] = "파티",
        ["ㅈㄱ"] = "잠깐",
        ["ㅂㅅ"] = "바보",
        ["ㅇㅋ"] = "오케이",
        ["ㄹㅇ"] = "진짜",
        ["ㄱㅊ"] = "괜찮아",
        ["ㅁㄹ"] = "몰라",
        ["ㅅㅅ"] = "수고",
        ["ㅈㅈ"] = "졌다",
        ["ㄷㅊ"] = "닥쳐"
    };

    // ตัวย่อยาวต้องถูกแทนก่อนตัวสั้น ไม่งั้น ㅋㅋㅋ จะโดนตัดผิด
    private static readonly Regex RepeatedLaugh = new("[ㅋㅎ]{2,}", RegexOptions.Compiled);
    private static readonly Regex RepeatedCry = new("[ㅜㅠ]{2,}", RegexOptions.Compiled);

    /// <summary>
    /// แทนแสลงด้วยคำเต็ม พร้อมรับศัพท์เฉพาะเกมที่ผู้ใช้เพิ่มเอง
    /// </summary>
    public static string Expand(string text, IReadOnlyDictionary<string, string>? custom = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var result = text;

        // ศัพท์ที่ผู้ใช้กำหนดเองมาก่อนเสมอ
        if (custom != null)
            foreach (var (key, value) in custom)
                if (!string.IsNullOrWhiteSpace(key))
                    result = result.Replace(key, value);

        result = RepeatedLaugh.Replace(result, " ㅎㅎ ");
        result = RepeatedCry.Replace(result, " ㅠㅠ ");

        foreach (var (key, value) in Map.OrderByDescending(k => k.Key.Length))
            result = result.Replace(key, value);

        return Collapse(result);
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool lastSpace = false;

        foreach (var c in s.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
            }
            else
            {
                sb.Append(c);
                lastSpace = false;
            }
        }

        return sb.ToString();
    }
}
