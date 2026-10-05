namespace GameChatTranslator.Core.Ocr;

/// <summary>
/// แชตเลื่อนขึ้นตลอด บรรทัดเดิมจะถูกอ่านซ้ำหลายรอบ
/// ตัวนี้คัดเฉพาะบรรทัดที่ยังไม่เคยเห็น และทนต่อการที่ OCR อ่านบรรทัดเดิมเพี้ยนไปเล็กน้อยได้
/// </summary>
public class LineTracker
{
    private readonly int _capacity;
    private readonly Queue<string> _order = new();
    private readonly HashSet<string> _exact = new();
    private readonly List<string> _recent = new();

    /// <summary>ต่างกันไม่เกินกี่ส่วนถึงยังถือว่าเป็นบรรทัดเดียวกัน</summary>
    private const double SimilarityThreshold = 0.90;

    /// <summary>
    /// อนุญาตให้เพี้ยนได้อย่างน้อยกี่ตัวอักษรเสมอ ไม่ว่าบรรทัดจะสั้นแค่ไหน
    /// แชตเกมมีข้อความสั้นเยอะ (เช่น "ㅋㅋㅋ", "네", "gg") ถ้าใช้แค่เกณฑ์สัดส่วน 90%
    /// OCR อ่านเพี้ยนตัวเดียวก็ทำให้บรรทัด 4-5 ตัวอักษรตกเกณฑ์ทันที กลายเป็น "ใหม่" ทั้งที่
    /// จริงคือบรรทัดเดิมที่เลื่อนขึ้นมาซ้ำ ทำให้แชตเก่าโดนแปลซ้ำทั้งที่มีข้อความใหม่แค่บรรทัดเดียว
    /// </summary>
    private const int MinAllowedEdits = 1;

    public LineTracker(int capacity = 300) => _capacity = capacity;

    public void Reset()
    {
        _order.Clear();
        _exact.Clear();
        _recent.Clear();
    }

    /// <summary>
    /// เรียกตอนแปลบรรทัดนี้ไม่สำเร็จ ลบออกจากรายการ "เคยเห็นแล้ว" เพื่อให้สแกนรอบถัดไป
    /// (ถ้าบรรทัดนั้นยังอยู่ในแชต) ลองแปลใหม่ได้ ไม่งั้นบรรทัดที่แปลพลาดจะค้าง "แปลไม่สำเร็จ"
    /// ถาวรเพราะระบบคัดบรรทัดซ้ำมองว่าเคยเจอแล้วไม่ว่าแปลจะสำเร็จหรือไม่
    /// </summary>
    public void Forget(string raw)
    {
        var key = Normalize(raw);
        if (key.Length == 0) return;

        _exact.Remove(key);
        _recent.Remove(key);
        // ปล่อย key ที่อาจค้างอยู่ใน _order ไว้เฉย ๆ ไม่กระทบอะไร เพราะ Remove ตอน
        // Dequeue ทีหลังทำงานปลอดภัยอยู่แล้วแม้ key จะถูกลบออกจาก _exact/_recent ไปก่อนหน้านี้
    }

    public List<string> FilterNew(IEnumerable<string> lines)
    {
        var fresh = new List<string>();

        foreach (var raw in lines)
        {
            var key = Normalize(raw);
            if (key.Length == 0) continue;
            if (_exact.Contains(key)) continue;
            if (IsNearDuplicate(key)) continue;

            _exact.Add(key);
            _order.Enqueue(key);
            _recent.Add(key);

            if (_order.Count > _capacity)
            {
                var old = _order.Dequeue();
                _exact.Remove(old);
                _recent.Remove(old);
            }

            fresh.Add(raw.Trim());
        }

        return fresh;
    }

    /// <summary>
    /// เทียบกับ 25 บรรทัดล่าสุดเท่านั้น เพราะบรรทัดที่โผล่ซ้ำจากการเลื่อนจะอยู่ใกล้กันเสมอ
    /// ทำให้ต้นทุนคงที่ ไม่โตตามจำนวนบรรทัดที่เคยเห็น
    /// </summary>
    private bool IsNearDuplicate(string key)
    {
        int start = Math.Max(0, _recent.Count - 25);

        for (int i = start; i < _recent.Count; i++)
            if (IsSameLine(key, _recent[i]))
                return true;

        return false;
    }

    /// <summary>
    /// ถือว่าเป็นบรรทัดเดียวกันถ้าความคล้ายผ่านเกณฑ์สัดส่วน 90% หรือระยะแก้ไขไม่เกิน
    /// โควตาขั้นต่ำที่อนุญาตเสมอ (MinAllowedEdits) เกณฑ์สัดส่วนอย่างเดียวใช้ไม่ได้กับบรรทัดสั้น
    /// เช่น "네" (1 ตัวอักษร) ที่ OCR อ่านผิดตัวเดียวก็กลายเป็นคล้ายกัน 0% ทั้งที่มันคือบรรทัดเดิม
    /// </summary>
    private static bool IsSameLine(string a, string b)
    {
        if (a == b) return true;
        if (a.Length == 0 || b.Length == 0) return false;

        int maxLen = Math.Max(a.Length, b.Length);
        int allowed = Math.Max(MinAllowedEdits, (int)Math.Round(maxLen * (1 - SimilarityThreshold)));

        // ยาวต่างกันเกินโควตาที่จะเป็นไปได้ก็ไม่ต้องคำนวณระยะแก้ไขให้เปลือง
        if (Math.Abs(a.Length - b.Length) > allowed) return false;

        return Levenshtein(a, b) <= allowed;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    private static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;

        var sb = new System.Text.StringBuilder(s.Length);
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
