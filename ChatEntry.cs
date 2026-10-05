namespace GameChatTranslator.Core.Pipeline;

public enum TranslationState
{
    Pending,
    /// <summary>คำแปลชั่วคราวจาก Google (เร็ว) โชว์ระหว่างรอ LLM</summary>
    Draft,
    Fast,
    Failed
}

/// <summary>
/// หนึ่งบรรทัดแชต ตัวเดียวกันนี้จะถูกอัปเดตซ้ำเมื่อคำแปลมาถึง
/// UI ผูกกับ Id เพื่อแทนที่ข้อความเดิมในที่เดิม ไม่ใช่ต่อท้ายใหม่
/// </summary>
public sealed class ChatEntry
{
    private static long _counter;

    public long Id { get; } = Interlocked.Increment(ref _counter);
    public DateTime Time { get; } = DateTime.Now;

    /// <summary>ข้อความเกาหลีที่ OCR อ่านได้</summary>
    public required string Korean { get; init; }

    /// <summary>ความมั่นใจของ OCR ใช้เตือนผู้ใช้เมื่อบรรทัดนั้นน่าสงสัย</summary>
    public float Confidence { get; init; }

    /// <summary>ข้อความหลังแทนแสลงแล้ว ตัวนี้คือสิ่งที่ส่งไปแปลจริง</summary>
    public required string Expanded { get; init; }

    /// <summary>
    /// แชตไม่กี่บรรทัดก่อนหน้า (ดิบ ไม่ผ่านแทนแสลง) ใช้เป็นบริบทให้โมเดลแปล
    /// ข้อความสั้นกำกวม เช่น "길!" เดี่ยว ๆ แปลไม่ได้ แต่ถ้ารู้ว่ากำลังคุยเรื่องอะไรอยู่จะแปลได้แม่นขึ้น
    /// </summary>
    public IReadOnlyList<string> Context { get; init; } = Array.Empty<string>();

    /// <summary>สีตัวอักษรที่ดึงมาจากภาพจริง ใช้ย้อมข้อความในโอเวอร์เลย์ให้ตรงกับสีในเกม</summary>
    public (byte R, byte G, byte B)? Color { get; init; }

    public string? Thai { get; set; }

    /// <summary>คำแปลจาก Google เก็บแยกไว้ ให้กดดูเทียบกับคำแปล LLM ได้</summary>
    public string? GoogleThai { get; set; }
    /// <summary>ผู้ใช้กดไอคอนเปิดดูคำแปล Google ของบรรทัดนี้อยู่</summary>
    public bool ShowGoogle { get; set; }

    public TranslationState State { get; set; } = TranslationState.Pending;

    public bool IsLowConfidence => Confidence > 0 && Confidence < 70f;
}
