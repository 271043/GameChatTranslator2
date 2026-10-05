using System.IO;
using System.Text.Json;

namespace GameChatTranslator.Core;

public enum CaptureBackend
{
    /// <summary>เลือกให้เองตามที่เครื่องรองรับ</summary>
    Auto,
    /// <summary>Windows Graphics Capture ทำงานบน GPU จับ Fullscreen Exclusive ได้</summary>
    WindowsGraphicsCapture,
    /// <summary>GDI ธรรมดา ใช้ได้กับ Borderless Windowed</summary>
    Gdi
}

public class AppConfig
{
    // ---- กรอบช่องแชต เป็นพิกเซลจริง ----
    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionWidth { get; set; }
    public int RegionHeight { get; set; }
    public bool HasRegion => RegionWidth > 0 && RegionHeight > 0;

    // ---- การจับภาพ ----
    public CaptureBackend Backend { get; set; } = CaptureBackend.Auto;

    /// <summary>เปิดแอปมาให้มีแค่หน้าต่างคำแปล ไม่มีหน้าควบคุมหลัก (Ctrl+1 สแกน, Ctrl+2 บางส่วน, Ctrl+3 เลือกกรอบ)</summary>
    public bool HeadlessUi { get; set; } = true;

    /// <summary>ปิดแอปแล้วฆ่าโปรเซส Ollama ในเครื่องนี้ทิ้งด้วย (คืน VRAM การ์ดจอทันที) — เฉพาะตอนใช้ Ollama localhost</summary>
    public bool KillLocalOllamaOnExit { get; set; } = true;

    // ---- OCR ----
    /// <summary>true = อ่านด้วยโมเดล vision บน Ollama (OcrModel), false = PaddleOCR บนเครื่องนี้</summary>
    public bool UseVlOcr { get; set; } = true;
    /// <summary>ชื่อโมเดล vision ที่ดึงไว้แล้ว ใช้เมื่อ UseVlOcr = true</summary>
    public string OcrModel { get; set; } = "qwen3-vl:8b";
    /// <summary>URL Ollama สำหรับ OCR แยกจากตัวแปล — ว่างไว้ = ใช้ OllamaUrl ตัวเดียวกัน
    /// (เช่นให้ OCR รันโมเดลเล็กในเครื่อง ส่วนแปลยิงไปเครื่องแรง)</summary>
    public string OcrOllamaUrl { get; set; } = "";
    /// <summary>ขยายภาพก่อนอ่าน 1.0 คือไม่ขยาย ฟอนต์แชตเกมเล็ก แนะนำ 2.0–3.0 (เฉพาะ PaddleOCR)</summary>
    public double OcrUpscale { get; set; } = 2.0;
    /// <summary>เพิ่มคอนทราสต์ (CLAHE) ก่อนอ่าน ช่วยตัวอักษรบางบนพื้นหลังเกม (เฉพาะ PaddleOCR)</summary>
    public bool OcrPreprocess { get; set; } = true;
    /// <summary>ความมั่นใจขั้นต่ำ (0-1) บรรทัดที่ต่ำกว่านี้จะถูกทิ้ง (เฉพาะ PaddleOCR)</summary>
    public float MinConfidence { get; set; } = 0.6f;

    /// <summary>เซฟภาพที่แคปทุกครั้ง + ข้อความที่อ่านได้ ไว้ทำชุดข้อมูลเทรน OCR ในอนาคต</summary>
    public bool SaveCaptures { get; set; } = false;
    /// <summary>โฟลเดอร์เก็บภาพแคป — ว่างไว้ = %AppData%\GameChatTranslator\captures</summary>
    public string CaptureDir { get; set; } = "";

    // ---- การแปล ----
    /// <summary>เปิด/ปิดการแปล (ปิดแล้วจะโชว์แต่ต้นฉบับเกาหลี)</summary>
    public bool UseTranslator { get; set; } = true;

    /// <summary>true = แปลด้วย Google Translate (เร็ว ต้องต่อเน็ต), false = โมเดลผ่าน Ollama</summary>
    public bool UseGoogleTranslate { get; set; } = true;

    /// <summary>ภาษาปลายทางของการแปล (เฉพาะโหมด Ollama) เช่น "English", "Thai"</summary>
    public string TargetLanguage { get; set; } = "English";

    /// <summary>โชว์คำแปลด่วนจาก Google ทันทีระหว่างรอ LLM (เฉพาะโหมด Ollama)</summary>
    public bool FastDraft { get; set; } = true;

    /// <summary>แปลสองต่อ: LLM ทำ KO→English แล้ว Google ทำ English→ไทย (ได้ไทยเป็นธรรมชาติกว่า)</summary>
    public bool PivotThroughEnglish { get; set; } = false;
    /// <summary>รหัสภาษาปลายทางฝั่ง Google ตอนแปลสองต่อ (th, en, ja, ...)</summary>
    public string PivotTargetCode { get; set; } = "th";

    /// <summary>รวมกี่บรรทัดต่อการยิงหนึ่งครั้ง</summary>
    public int BatchSize { get; set; } = 5;
    /// <summary>รอสะสมบรรทัดนานสุดกี่ ms ก่อนยิง ยิ่งน้อยยิ่งเห็นคำแปลไวแต่แบตช์ได้น้อยลง</summary>
    public int BatchWaitMs { get; set; } = 150;

    /// <summary>ที่อยู่เซิร์ฟเวอร์ Ollama (ค่าเริ่มต้นคือที่รันบนเครื่องตัวเอง)</summary>
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    /// <summary>ชื่อโมเดลที่ดึงไว้แล้วผ่าน `ollama pull`</summary>
    public string OllamaModel { get; set; } = "qwen3:14b";
    /// <summary>ให้โมเดลค้างอยู่ใน RAM/VRAM นานแค่ไหนหลังไม่ได้ใช้ ก่อนถูก unload อัตโนมัติ</summary>
    public string OllamaKeepAlive { get; set; } = "30m";
    /// <summary>เพดานโทเคนคำตอบต่อบรรทัด — โมเดล reasoning (gpt-oss) ต้อง 600+, qwen3.x ใช้ 200 ก็พอ</summary>
    public int OllamaNumPredict { get; set; } = 768;

    // ---- Ollama เครื่องรีโมต (เมื่อ OllamaUrl ไม่ใช่ localhost) ----
    /// <summary>ชื่อผู้ใช้ SSH ของเครื่องรีโมต ใช้ตอนแอปสั่งเปิด Ollama ให้เองถ้าต่อไม่ติด</summary>
    public string RemoteSshUser { get; set; } = "Sharp";
    /// <summary>พาธ private key สำหรับ SSH — ว่างไว้ = ใช้ ~/.ssh/gct_mac</summary>
    public string RemoteSshKeyPath { get; set; } = "";
    /// <summary>คำสั่งที่รันบนเครื่องรีโมตเพื่อเปิด Ollama ให้ฟังพอร์ต Tailscale</summary>
    public string RemoteOllamaStartCommand { get; set; } =
        "launchctl setenv OLLAMA_HOST 0.0.0.0 && /opt/homebrew/bin/brew services restart ollama";

    // ---- หน้าต่างคำแปล ----
    public double OverlayX { get; set; } = 60;
    public double OverlayY { get; set; } = 60;
    public double OverlayWidth { get; set; } = 460;
    public double OverlayHeight { get; set; } = 320;
    public double OverlayOpacity { get; set; } = 1.0;
    public double OverlayFontSize { get; set; } = 26;
    public bool ShowKorean { get; set; } = true;
    public int MaxOverlayLines { get; set; } = 12;

    // ---- ศัพท์เฉพาะเกมและแสลงที่ผู้ใช้เพิ่มเอง ----
    public Dictionary<string, string> CustomTerms { get; set; } = new();

    private static string Dir
    {
        get
        {
            var d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GameChatTranslator");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string ConfigPath => Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
        }
        catch { }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(ConfigPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
