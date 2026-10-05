using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace GameChatTranslator.Core.Translate;

/// <summary>
/// จำคำแปลไว้ทั้งในหน่วยความจำและบนดิสก์
/// แชตเกมมีประโยคซ้ำเยอะมาก (ชวนปาร์ตี้ ทักทาย ซื้อขาย) ตัวนี้ตัดการยิงเครือข่ายทิ้งได้เป็นส่วนใหญ่
/// และเป็นเกราะสำคัญที่กันไม่ให้โดน Google บล็อก
/// </summary>
public sealed class TranslationCache
{
    private readonly ConcurrentDictionary<string, string> _map = new();
    private readonly string _path;
    private int _dirty;

    public TranslationCache()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameChatTranslator");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "cache.json");

        Load();
    }

    public int Count => _map.Count;

    public bool TryGet(string korean, out string thai) => _map.TryGetValue(Key(korean), out thai!);

    public void Set(string korean, string thai)
    {
        if (string.IsNullOrWhiteSpace(korean) || string.IsNullOrWhiteSpace(thai)) return;

        _map[Key(korean)] = thai;
        Interlocked.Exchange(ref _dirty, 1);
    }

    private static string Key(string s) => s.Trim();

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
            if (loaded == null) return;

            foreach (var (k, v) in loaded) _map[k] = v;
        }
        catch { }
    }

    /// <summary>เขียนลงดิสก์ เรียกตอนปิดโปรแกรมหรือเป็นระยะ</summary>
    public void Flush()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;

        try
        {
            // ไม่ให้ไฟล์โตไม่จำกัด เก็บล่าสุดไว้ 20000 รายการ
            var snapshot = _map.Count > 20000
                ? _map.Skip(_map.Count - 20000).ToDictionary(x => x.Key, x => x.Value)
                : _map.ToDictionary(x => x.Key, x => x.Value);

            File.WriteAllText(_path,
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch { }
    }
}
