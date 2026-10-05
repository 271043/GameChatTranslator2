using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using GameChatTranslator.Core;
using GameChatTranslator.Core.Capture;
using GameChatTranslator.Core.Ocr;
using GameChatTranslator.Core.Pipeline;
using GameChatTranslator.Core.Translate;

namespace GameChatTranslator.UI;

public partial class MainWindow : Window
{
    // ไอคอน Segoe MDL2 Assets — เขียนเป็น \u หนีไว้กันปัญหาอักขระ PUA ไม่ผ่านตอน build/edit
    private const string IconPlay = "\uE768";
    private const string IconStop = "\uE71A";

    private readonly AppConfig _config;
    private readonly TranslationCache _cache;
    private readonly IOcrEngine _ocr;
    private readonly TranslationPipeline _pipeline;
    private readonly DispatcherTimer _statsTimer;

    private OverlayWindow? _overlay;
    private PartialSelectorWindow? _partialSelector;
    private int _count;
    private int _pendingTranslations;

    public MainWindow()
    {
        InitializeComponent();

        _config = AppConfig.Load();
        _cache = new TranslationCache();

        var ocrUrl = string.IsNullOrWhiteSpace(_config.OcrOllamaUrl) ? _config.OllamaUrl : _config.OcrOllamaUrl;
        _ocr = _config.UseVlOcr
            ? new OllamaVlOcrEngine(ocrUrl, _config.OcrModel, _config.OllamaKeepAlive)
            : new PaddleOcrEngine(_config.OcrUpscale, _config.MinConfidence, _config.OcrPreprocess);

        _pipeline = new TranslationPipeline(_config, _ocr, _cache);
        _pipeline.EntryAdded += OnEntryAdded;
        _pipeline.EntryUpdated += OnEntryUpdated;
        _pipeline.Status += OnStatus;
        _pipeline.ScanResult += OnScanResult;

        ShowSystemStatus();
        WarmUpModel();

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => ShowStats();
        _statsTimer.Start();

        SourceInitialized += (_, _) => { TryRoundCorners(); RegisterScanHotkey(); };
        Closed += OnClosed;
    }

    // ---------- ปุ่มลัดสแกน (Ctrl+1) ----------
    // จับภาพ+OCR+แปลไม่ทำงานเป็นลูปอัตโนมัติแล้ว ต้องกดปุ่มลัดนี้ทุกครั้งที่ต้องการสแกนแชต

    private bool _hotkeysRegistered;

    private void RegisterScanHotkey()
    {
        if (_hotkeysRegistered) return;
        _hotkeysRegistered = true;

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(hwnd)?.AddHook(ScanHotkeyHook);

        RegisterHotKey(hwnd, ScanHotkeyId, MOD_CONTROL, VK_1);
        RegisterHotKey(hwnd, PartialHotkeyId, MOD_CONTROL, VK_2);
        RegisterHotKey(hwnd, RegionHotkeyId, MOD_CONTROL, VK_3);
    }

    /// <summary>
    /// จุดเริ่มของแอป — เรียกจาก App.OnStartup
    /// โหมด headless (ค่าเริ่มต้น): ไม่โชว์หน้าควบคุม เปิดแค่หน้าต่างคำแปล เริ่มสายพานเลยถ้ามีกรอบแล้ว
    /// ยังไม่มีกรอบ / ปิด headless: โชว์หน้าควบคุมตามปกติ
    /// </summary>
    public void Boot()
    {
        if (_config.HeadlessUi && _config.HasRegion)
        {
            RegisterScanHotkey();          // EnsureHandle ข้างในสร้าง hwnd โดยไม่ต้องโชว์หน้าต่าง
            EnsureOverlay();
            _overlay!.Closed += OverlayClosedQuit;   // ปิดหน้าต่างคำแปล = ปิดแอป
            StartPipeline();
        }
        else
        {
            Show();                        // SourceInitialized → RegisterScanHotkey ตามปกติ
        }
    }

    private IntPtr ScanHotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;

        int id = wParam.ToInt32();

        if (id == ScanHotkeyId)
        {
            // ล้างหน้าต่างคำแปลก่อนสแกนใหม่ทุกครั้ง เหลือแค่ผลจากรอบนี้ ไม่พอกกับของเก่า
            if (_pipeline.IsRunning)
            {
                _overlay?.ClearLines();
                _pendingTranslations = 0;
                _overlay?.SetMode("กำลังสแกน…");
                _overlay?.SetLoading(true, "กำลังสแกน…");
            }

            _pipeline.TriggerScan();
            handled = true;
        }
        else if (id == PartialHotkeyId)
        {
            StartPartialTranslate();
            handled = true;
        }
        else if (id == RegionHotkeyId)
        {
            DoSelectRegion();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private const int WM_HOTKEY = 0x0312;
    private const int ScanHotkeyId = 0xA732;
    private const int PartialHotkeyId = 0xA733;
    private const int RegionHotkeyId = 0xA734;
    private const uint MOD_CONTROL = 0x0002;
    private const uint VK_1 = 0x31;
    private const uint VK_2 = 0x32;
    private const uint VK_3 = 0x33;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>
    /// ขอให้ Windows 11 มนขอบหน้าต่างให้ (DWMWA_WINDOW_CORNER_PREFERENCE)
    /// เครื่องที่เป็น Windows 10 จะไม่รู้จัก attribute นี้ เรียกแล้วแค่เฉยๆ ไม่พัง
    /// </summary>
    private void TryRoundCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch { }
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // ---------- สถานะระบบ ----------
    // ไม่มีกล่องสถานะแยกแล้ว — ถ้า OCR ยังไม่พร้อม บอกผ่านแถบสถานะบรรทัดเดียวด้านล่างแทน

    private void ShowSystemStatus()
    {
        bool ok = _ocr.IsReady;

        ToggleButton.IsEnabled = ok;

        if (!ok)
            StatusText.Text = "ยังใช้งานไม่ได้ — " + _ocr.Problem;
    }

    private void ShowStats()
    {
        if (!_pipeline.IsRunning) return;
        StatusText.Text = $"พร้อมสแกน · กด Ctrl+1 — แปลไปแล้ว {_count} บรรทัด";
    }

    /// <summary>
    /// เปิดโปรแกรมปุ๊บสั่งโหลดโมเดล Ollama ขึ้น RAM/VRAM ทันที
    /// จะได้ไม่ต้องรอโหลดตอนกด Ctrl+1 สแกนครั้งแรก (โมเดลใหญ่โหลดครั้งแรกช้า)
    /// </summary>
    private void SetConn(OverlayWindow.ConnState s, string text) =>
        Dispatcher.Invoke(() => _overlay?.SetConnection(s, text));

    private async void WarmUpModel()
    {
        SetConn(OverlayWindow.ConnState.Connecting, "กำลังเชื่อมต่อ AI…");

        // อุ่นเครื่อง OCR ก่อน — โมเดล VL (localhost/รีโมต) โหลดครั้งแรกช้ามาก (~54 วิ) ต้องปลุกล่วงหน้า
        // Paddle เป็น no-op ไม่มีผล
        if (_config.UseVlOcr)
        {
            StatusText.Text = "กำลังโหลดโมเดลอ่านข้อความ…";
            SetConn(OverlayWindow.ConnState.Connecting, "กำลังโหลดโมเดลอ่านข้อความ…");
            await _ocr.WarmUpAsync(CancellationToken.None);

            // เชื่อม Ollama ไม่ได้รอบแรก — อาจยังไม่ได้รัน ลองสั่งเปิดเองแล้วลองใหม่เป็นช่วง ๆ
            if (_ocr.Problem != null)
            {
                var ocrUrl = string.IsNullOrWhiteSpace(_config.OcrOllamaUrl) ? _config.OllamaUrl : _config.OcrOllamaUrl;
                TryLaunchOllamaServe(ocrUrl);
                for (int i = 0; i < 5 && _ocr.Problem != null; i++)
                {
                    await Task.Delay(2000);
                    await _ocr.WarmUpAsync(CancellationToken.None);
                }
            }

            if (_ocr.Problem is { } ocrProblem)
            {
                StatusText.Text = ocrProblem;
                SetConn(OverlayWindow.ConnState.Warn, "อ่านภาพ: ต่อไม่ได้");
            }
            else if (!_pipeline.IsRunning)
                StatusText.Text = "โมเดลอ่านข้อความพร้อมใช้งาน";
        }

        // ใช้ Google Translate ล้วน ๆ ไม่มีโมเดลแปลต้องอุ่นเครื่อง (แต่โหมด pivot มี LLM ขั้นแรก ต้องอุ่น)
        if (_config.UseGoogleTranslate && !_config.PivotThroughEnglish)
        {
            if (_ocr.Problem == null) SetConn(OverlayWindow.ConnState.Ok, "Google Translate · พร้อม");
            return;
        }

        // OCR ไม่พร้อมสำคัญกว่า อย่าเอาข้อความโหลดโมเดลไปทับข้อความเตือนของ OCR
        bool showStatus = _ocr.IsReady;
        if (showStatus) StatusText.Text = "กำลังโหลดโมเดล Ollama…";

        bool ok = await _pipeline.WarmUpTranslatorAsync(CancellationToken.None);

        // เชื่อมต่อไม่ได้ครั้งแรก แปลว่า Ollama อาจยังไม่ได้รันอยู่เบื้องหลัง
        // ลองสั่งเปิดเองเงียบ ๆ แล้วลองใหม่เป็นช่วง ๆ — วัดจริงพบว่า `ollama serve` บางเครื่อง
        // ใช้เวลาเกิน 2 วิ กว่าจะเปิดพอร์ตสำเร็จ (โดยเฉพาะรอบแรกหลังเพิ่งบูตเครื่อง) รอครั้งเดียวไม่พอ
        if (!ok)
        {
            TryLaunchOllamaServe(_config.OllamaUrl);
            for (int i = 0; i < 5 && !ok; i++)
            {
                await Task.Delay(2000);
                ok = await _pipeline.WarmUpTranslatorAsync(CancellationToken.None);
            }
        }

        if (ok && showStatus && !_pipeline.IsRunning)
            StatusText.Text = "โมเดล Ollama พร้อมใช้งาน";

        if (ok && _ocr.Problem == null)
            SetConn(OverlayWindow.ConnState.Ok, "เชื่อมต่อ AI แล้ว");
        else if (!ok)
            SetConn(OverlayWindow.ConnState.Warn, "แปล: ต่อ Ollama ไม่ได้");
    }

    private void TryLaunchOllamaServe(string url)
    {
        // Ollama อยู่เครื่องอื่น (เช่น Mac ผ่าน Tailscale) — SSH ไปสั่งเปิดด้วย key ที่ตั้งไว้
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !IsLocalHost(uri.Host))
        {
            TryRemoteStartOllama(uri.Host);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ollama",
                Arguments = "serve",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { /* ไม่เจอ ollama.exe ใน PATH หรือรันอยู่แล้ว — ปล่อยให้ข้อความเตือนปกติแจ้งแทน */ }
    }

    private static bool IsLocalHost(string host) =>
        string.IsNullOrEmpty(host) ||
        host is "localhost" or "127.0.0.1" or "::1" or "[::1]";

    /// <summary>
    /// SSH ไปเครื่องรีโมตสั่งเปิด Ollama (ใช้ตอนเปิดแอปแล้วต่อ Ollama ไม่ติด)
    /// ใช้ SSH key ที่ ~/.ssh/gct_mac (ตั้งครั้งเดียว) — ไม่มีรหัสผ่านในโปรแกรม
    /// </summary>
    private void TryRemoteStartOllama(string host)
    {
        try
        {
            var key = string.IsNullOrWhiteSpace(_config.RemoteSshKeyPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "gct_mac")
                : _config.RemoteSshKeyPath;

            var target = $"{_config.RemoteSshUser}@{host}";
            var args =
                $"-i \"{key}\" -o IdentitiesOnly=yes -o BatchMode=yes " +
                $"-o StrictHostKeyChecking=accept-new -o ConnectTimeout=10 " +
                $"{target} \"{_config.RemoteOllamaStartCommand}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { /* ไม่มี ssh.exe / key หาย — ปล่อยให้ข้อความเตือนปกติแจ้งแทน */ }
    }

    // ---------- ไตเติลบาร์ ----------

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- ปุ่ม ----------

    private void SelectRegion_Click(object sender, RoutedEventArgs e) => DoSelectRegion();

    private void DoSelectRegion()
    {
        bool wasRunning = _pipeline.IsRunning;
        _pipeline.Stop();

        bool visible = IsVisible;
        var prev = WindowState;
        if (visible) WindowState = WindowState.Minimized;

        Dispatcher.InvokeAsync(() =>
        {
            var selector = new RegionSelectorWindow();
            bool? ok = selector.ShowDialog();

            if (visible) { WindowState = prev; Activate(); }

            if (ok == true && selector.SelectedRegion is { } r)
            {
                _config.RegionX = r.X;
                _config.RegionY = r.Y;
                _config.RegionWidth = r.Width;
                _config.RegionHeight = r.Height;
                _config.Save();

                StatusText.Text = $"ตั้งกรอบแชตใหม่ {r.Width} × {r.Height} px";
            }

            // headless: เพิ่งตั้งกรอบครั้งแรก → เริ่มสายพาน + เปิดหน้าต่างคำแปล + ซ่อนหน้าควบคุม
            if (wasRunning || (_config.HeadlessUi && _config.HasRegion))
            {
                EnsureOverlay();
                if (_overlay != null)
                {
                    _overlay.Closed -= OverlayClosedQuit;
                    if (_config.HeadlessUi) _overlay.Closed += OverlayClosedQuit;
                }
                StartPipeline();
                if (_config.HeadlessUi && IsVisible) Hide();
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private void OverlayClosedQuit(object? sender, EventArgs e) => Close();

    /// <summary>ปุ่มเดียวสลับ เริ่ม/หยุด ตามสถานะปัจจุบัน มือใหม่ไม่ต้องเดาว่าปุ่มไหนคู่กับปุ่มไหน</summary>
    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_pipeline.IsRunning)
            StopPipeline();
        else
            Start_Click();
    }

    private void Start_Click()
    {
        if (!_config.HasRegion)
        {
            MessageBox.Show(this, "ยังไม่ได้เลือกกรอบแชต กด “เลือกกรอบแชต” ก่อน",
                "ยังเริ่มไม่ได้", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StartPipeline();
    }

    private void StartPipeline()
    {
        var region = new System.Drawing.Rectangle(_config.RegionX, _config.RegionY,
            _config.RegionWidth, _config.RegionHeight);

        if (!_pipeline.Start(region))
        {
            StatusText.Text = "เริ่มไม่สำเร็จ";
            return;
        }

        // เปิดหน้าต่างคำแปลให้อัตโนมัติ มือใหม่จะได้ไม่ต้องรู้ว่ามีปุ่มแยกต่างหาก
        EnsureOverlay();

        // สลับปุ่มเป็นสไตล์ "อันตราย/หยุด" สีแดง ให้เห็นชัดว่ากำลังทำงานอยู่ กดซ้ำเพื่อหยุด
        ToggleButton.Style = (Style)FindResource("DangerButton");
        ToggleIcon.Text = IconStop;
        ToggleTitle.Text = "หยุดแปล";
        ToggleSubtitle.Text = "กด Ctrl+1 เพื่อสแกนแชต · กดปุ่มนี้เพื่อหยุด";
        SelectRegionButton.IsEnabled = false;
    }

    private void StopPipeline()
    {
        _pipeline.Stop();

        // กลับไปเป็นปุ่ม Accent สีเน้น พร้อมให้กดเริ่มใหม่
        ToggleButton.Style = (Style)FindResource("AccentButton");
        ToggleIcon.Text = IconPlay;
        ToggleTitle.Text = "เริ่มแปล";
        ToggleSubtitle.Text = "กดแล้วใช้ Ctrl+1 เพื่อสแกนแชตทีละครั้ง";
        SelectRegionButton.IsEnabled = true;

        StatusText.Text = "หยุดแล้ว";
    }

    private void Overlay_Click(object sender, MouseButtonEventArgs e)
    {
        EnsureOverlay();
        _overlay!.Show();
        _overlay.Activate();
    }

    /// <summary>
    /// ทางออกสำรองเวลาล็อกหน้าต่างคำแปลแล้วปุ่มลัด Ctrl+Shift+L ใช้ไม่ได้
    /// (เช่นชนกับโปรแกรมอื่นที่จองปุ่มลัดนี้ไปก่อน) — ปุ่มนี้อยู่ในหน้าต่างหลัก
    /// ที่ไม่โดนคลิกทะลุ เลยกดได้เสมอไม่ว่าหน้าต่างคำแปลจะล็อกอยู่หรือไม่
    /// </summary>
    private void Unlock_Click(object sender, MouseButtonEventArgs e)
    {
        if (_overlay is not { IsLoaded: true })
        {
            MessageBox.Show(this, "ยังไม่ได้เปิดหน้าต่างคำแปล",
                "ไม่มีอะไรให้ปลดล็อก", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _overlay.Unlock();
        _overlay.Activate();
    }

    // ---------- แปลบางส่วนทันที (Ctrl+2) ----------
    // กด Ctrl+2: จับภาพจอค้างไว้ → ลากกรอบคลุมบรรทัด → ปล่อยเมาส์ = ตัดกรอบนั้นจากภาพนิ่ง → OCR → แปล
    // ทำงานได้แม้ยังไม่ได้กด "เริ่มแปล" เพราะไม่พึ่งสายพานหลัก

    private void Partial_Click(object sender, RoutedEventArgs e) => StartPartialTranslate();

    private void StartPartialTranslate()
    {
        // มีตัวเลือกเปิดค้างอยู่แล้ว — ไม่เปิดซ้อน
        if (_partialSelector is { IsLoaded: true }) return;

        _partialSelector = new PartialSelectorWindow();

        _partialSelector.Cancelled += () =>
        {
            _partialSelector?.Close();
            _partialSelector = null;
            StatusText.Text = "ยกเลิกแปลบางส่วน";
        };

        _partialSelector.Completed += () =>
        {
            var selector = _partialSelector;
            if (selector == null) return;

            var box = selector.SelectionInSnapshot;
            var snapshot = selector.TakeSnapshot();
            selector.Close();
            _partialSelector = null;

            if (box is not { } b || snapshot == null)
            {
                snapshot?.Dispose();
                StatusText.Text = "ลากกรอบไม่สำเร็จ — กด Ctrl+2 ลองใหม่";
                return;
            }

            RunPartialTranslate(snapshot, b);
        };

        _partialSelector.Show();
        _partialSelector.Activate();
        StatusText.Text = "จอค้างอยู่ · ลากกรอบคลุมบรรทัดแล้วปล่อยเมาส์เพื่อแปล";
    }

    private async void RunPartialTranslate(System.Drawing.Bitmap snapshot, System.Drawing.Rectangle box)
    {
        EnsureOverlay();
        _overlay!.Show();
        _overlay.ClearLines();
        _pendingTranslations = 0;
        _overlay.SetMode("กำลังสแกน…");
        _overlay.SetLoading(true, "กำลังสแกน…");
        StatusText.Text = "กำลังแปลบางส่วน…";

        try
        {
            await _pipeline.TranslateHighlightAsync(snapshot, box, CancellationToken.None);
            StatusText.Text = "แปลบางส่วนเสร็จ · กด Ctrl+2 เพื่อลากกรอบใหม่";
        }
        catch (Exception ex)
        {
            StatusText.Text = "แปลบางส่วนผิดพลาด: " + ex.Message;
        }
        finally
        {
            _overlay?.SetLoading(false);
        }
    }

    private void EnsureOverlay()
    {
        if (_overlay is { IsLoaded: true }) return;

        _overlay = new OverlayWindow(_config);
        _overlay.Show();
    }

    // ---------- รับผลจากสายพาน ----------
    // ไม่มีช่องบันทึกในหน้าต่างหลักแล้ว ข้อความ/คำแปลไปโผล่ที่หน้าต่างคำแปลลอยแทนทั้งหมด

    private void OnEntryAdded(ChatEntry entry)
    {
        Dispatcher.Invoke(() =>
        {
            _count++;
            _overlay?.Add(entry);
        });
    }

    private void OnEntryUpdated(ChatEntry entry)
    {
        Dispatcher.Invoke(() =>
        {
            _overlay?.Update(entry);

            // แปลสำเร็จจาก LLM = ต่อ AI ได้ → ไฟเขียว
            if (entry.State == TranslationState.Fast)
                _overlay?.SetConnection(OverlayWindow.ConnState.Ok, "เชื่อมต่อ AI แล้ว");

            _pendingTranslations = Math.Max(0, _pendingTranslations - 1);
            _overlay?.SetMode(_pendingTranslations > 0 ? $"กำลังแปล… ({_pendingTranslations})" : "");
        });
    }

    /// <summary>OCR จบรอบแล้ว รู้จำนวนบรรทัดใหม่ที่ต้องแปลต่อ ใช้ตั้งค่า "กำลังแปล..." ให้ตรงจังหวะ</summary>
    private void OnScanResult(int freshCount)
    {
        Dispatcher.Invoke(() =>
        {
            // ปิดอนิเมชันโหลดกลางช่อง เพราะจากนี้แต่ละแถวที่เพิ่มเข้ามาจะมีตัวบอก "กำลังแปล…"
            // ของตัวเองอยู่แล้วระหว่างรอผลแปล (ดู OverlayWindow.BuildContent)
            _overlay?.SetLoading(false);

            _pendingTranslations = freshCount;
            _overlay?.SetMode(freshCount > 0 ? $"กำลังแปล… ({freshCount})" : "");
        });
    }

    private void OnStatus(string message)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = message;

            // ข้อความที่บ่งบอกว่าต่อ AI ไม่ได้ → เปลี่ยนไฟสถานะเป็นส้ม
            if (message.Contains("ไม่ได้") || message.Contains("ไม่สำเร็จ") || message.Contains("ปฏิเสธ"))
                _overlay?.SetConnection(OverlayWindow.ConnState.Warn, message);

            _overlay?.SetLoading(false);
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(h, ScanHotkeyId);
            UnregisterHotKey(h, PartialHotkeyId);
            UnregisterHotKey(h, RegionHotkeyId);
        }
        catch { }

        _partialSelector?.Close();

        _statsTimer.Stop();
        _pipeline.Dispose();
        _cache.Flush();
        _config.Save();
        _overlay?.Close();

        if (_config.KillLocalOllamaOnExit && UsesLocalOllama())
            KillLocalOllama();

        Application.Current.Shutdown();
    }

    private bool UsesLocalOllama()
    {
        bool IsLocal(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) && IsLocalHost(u.Host);

        return IsLocal(_config.OllamaUrl) ||
               (_config.UseVlOcr && IsLocal(string.IsNullOrWhiteSpace(_config.OcrOllamaUrl) ? _config.OllamaUrl : _config.OcrOllamaUrl));
    }

    /// <summary>
    /// ฆ่าโปรเซส Ollama ในเครื่องทิ้งเด็ดขาดตอนปิดแอป — คืน VRAM ทันที ไม่ต้องรอ keep_alive unload เอง
    /// ปิด `ollama.exe` (ตัว server) และ `ollama app.exe` (ตัว tray) ทั้งคู่
    /// </summary>
    private static void KillLocalOllama()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.ProcessName.Contains("ollama", StringComparison.OrdinalIgnoreCase))
                        p.Kill(entireProcessTree: true);
                }
                catch { /* บางโปรเซสอาจปิดไปแล้ว/ไม่มีสิทธิ์ ข้ามไปตัวถัดไป */ }
            }
        }
        catch { }
    }
}
