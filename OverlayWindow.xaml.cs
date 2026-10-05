using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GameChatTranslator.Core;
using GameChatTranslator.Core.Pipeline;

namespace GameChatTranslator.UI;

/// <summary>
/// หน้าต่างคำแปล วางลอยเหนือเกม
///
/// จุดสำคัญคือ "ล็อก" ซึ่งทำให้เมาส์ทะลุผ่านหน้าต่างนี้ไปที่เกมได้
/// ทำผ่าน WS_EX_TRANSPARENT ไม่งั้นหน้าต่างจะไปขวางการคลิกในเกม
///
/// อีกจุดคือคำแปลของแต่ละบรรทัดจะถูกแทนที่ในตำแหน่งเดิมเมื่อชั้นที่ 2 ทำงานเสร็จ
/// ผู้ใช้จึงเห็นคำแปลเร็วก่อน แล้วเห็นมันเปลี่ยนเป็นคำแปลที่ดีกว่าโดยไม่ต้องรอ
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly AppConfig _config;
    private readonly Dictionary<long, Border> _rows = new();
    private bool _locked;

    public OverlayWindow(AppConfig config)
    {
        _config = config;
        InitializeComponent();

        Left = config.OverlayX;
        Top = config.OverlayY;
        Width = config.OverlayWidth;
        Height = config.OverlayHeight;

        // ใส่ความโปร่งแสงที่พื้นหลังกล่องแทนที่จะปรับ Opacity ของทั้งหน้าต่าง
        // เพราะ Window.Opacity ทำให้ตัวหนังสือจางไปด้วย อ่านยาก
        byte alpha = (byte)Math.Clamp(config.OverlayOpacity * 255, 0, 255);
        Shell.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x10, 0x12, 0x16));

        SourceInitialized += (_, _) => RegisterHotkey();
        LocationChanged += SaveBounds;
        SizeChanged += SaveBounds;
    }

    public void SetMode(string text) => ModeLabel.Text = text;

    public enum ConnState { Connecting, Ok, Warn }

    /// <summary>อัปเดตไฟสถานะการเชื่อมต่อ AI บนแถบหัวหน้าต่าง</summary>
    public void SetConnection(ConnState state, string text)
    {
        var color = state switch
        {
            ConnState.Ok   => Color.FromRgb(0x4C, 0xC2, 0xA0),   // เขียว
            ConnState.Warn => Color.FromRgb(0xE0, 0xA4, 0x58),   // ส้ม
            _              => Color.FromRgb(0x9C, 0x9C, 0x9C)     // เทา
        };
        StatusDot.Fill = new SolidColorBrush(color);
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(
            state == ConnState.Ok ? Color.FromRgb(0x9C, 0x9C, 0x9C) : color);
    }

    /// <summary>
    /// อนิเมชันวงกลมหมุนกลางช่องแปล โผล่ตอนกำลังจับภาพ+OCR (ช่วงที่ยังไม่มีบรรทัดให้เห็นเลย)
    /// พอ OCR เจอบรรทัดแล้วให้ปิด เพราะแต่ละแถวมีตัวบอก "กำลังแปล…" ของตัวเองอยู่แล้วระหว่างรอผลแปล
    /// </summary>
    public void SetLoading(bool loading, string? text = null)
    {
        // ปิดอนิเมชันโหลดกลางจอถาวร — แต่ละแถวมีตัวบอก "กำลังแปล…" ของตัวเองอยู่แล้ว
        LoadingPanel.Visibility = Visibility.Collapsed;
        LoadingSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    /// <summary>
    /// ทางออกฉุกเฉิน — ตอนล็อกอยู่ ทั้งไตเติลบาร์และปุ่มล็อกจะหายไปด้วย (คลิกทะลุหมด)
    /// ทางเดียวที่เหลือคือปุ่มลัด Ctrl+Shift+L ถ้าปุ่มลัดชนกับโปรแกรมอื่นจนใช้ไม่ได้
    /// จะไม่มีทางปลดล็อกได้เองเลย เมนหน้าต่างเลยต้องมีทางปลดล็อกสำรองไว้เสมอ
    /// </summary>
    public void Unlock() => SetLocked(false);

    public bool IsLocked => _locked;

    // ---------- แสดงบรรทัด ----------

    public void Add(ChatEntry entry)
    {
        var row = BuildRow(entry);
        _rows[entry.Id] = row;
        Lines.Children.Add(row);

        while (Lines.Children.Count > _config.MaxOverlayLines)
        {
            if (Lines.Children[0] is Border old)
            {
                var stale = _rows.FirstOrDefault(kv => kv.Value == old);
                if (stale.Value != null) _rows.Remove(stale.Key);
            }
            Lines.Children.RemoveAt(0);
        }

        Scroller.ScrollToEnd();
    }

    public void Update(ChatEntry entry)
    {
        if (!_rows.TryGetValue(entry.Id, out var row)) return;

        row.Child = BuildContent(entry);
        Scroller.ScrollToEnd();
    }

    private Border BuildRow(ChatEntry entry) => new()
    {
        Margin = new Thickness(0, 0, 0, 16),
        Child = BuildContent(entry)
    };

    private StackPanel BuildContent(ChatEntry entry)
    {
        var panel = new StackPanel();

        // ต้นฉบับเกาหลี ช่วยให้เห็นเองว่าบรรทัดไหน OCR อ่านเพี้ยน
        if (_config.ShowKorean)
        {
            panel.Children.Add(new TextBlock
            {
                Text = entry.Korean,
                Foreground = new SolidColorBrush(
                    entry.IsLowConfidence
                        ? Color.FromRgb(0xE0, 0xA4, 0x58)
                        : Color.FromRgb(0x6C, 0x76, 0x86)),
                FontSize = _config.OverlayFontSize - 3,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 2)
            });
        }

        var thai = new TextBlock
        {
            FontSize = _config.OverlayFontSize,
            TextWrapping = TextWrapping.Wrap
        };

        switch (entry.State)
        {
            case TranslationState.Pending:
                thai.Text = "กำลังแปล…";
                thai.Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x63, 0x71));
                thai.FontStyle = FontStyles.Italic;
                break;

            case TranslationState.Draft:
                // คำแปลชั่วคราวจาก Google — อ่านออกชัด แต่เอียงไว้ให้รู้ว่ายังไม่ใช่ตัวจริง
                thai.Text = entry.Thai ?? "";
                thai.Foreground = new SolidColorBrush(Color.FromRgb(0xCE, 0xD6, 0xE0));
                thai.FontStyle = FontStyles.Italic;
                break;

            case TranslationState.Fast:
                thai.Text = entry.Thai ?? "";
                thai.Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xF1, 0xF8));
                break;

            case TranslationState.Failed:
                // แปลไม่ผ่าน (มักเป็นข้อความสั้นกำกวมเกินไป เช่น "길!" เดี่ยว ๆ)
                // โชว์ต้นฉบับแทนดีกว่าขึ้น error ผู้ใช้เห็นแล้วเข้าใจเองว่าเป็นเศษข้อความ ไม่ใช่โปรแกรมพัง
                thai.Text = entry.Korean;
                thai.Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x63, 0x71));
                thai.FontStyle = FontStyles.Italic;
                break;
        }

        // มีคำแปล Google เก็บไว้ + LLM แปลจริงแล้ว → เพิ่มไอคอนเล็กให้กดดูคำแปล Google เทียบ
        bool canPeek = entry.State == TranslationState.Fast
                       && !string.IsNullOrWhiteSpace(entry.GoogleThai)
                       && entry.GoogleThai != entry.Thai;

        if (canPeek)
        {
            var dock = new DockPanel { LastChildFill = true };

            var peek = new Button
            {
                Content = "",   // Segoe MDL2 ลูกโลก
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = _config.OverlayFontSize - 6,
                Foreground = new SolidColorBrush(Color.FromRgb(entry.ShowGoogle ? (byte)0x4C : (byte)0x6C,
                                                               entry.ShowGoogle ? (byte)0xC2 : (byte)0x76,
                                                               entry.ShowGoogle ? (byte)0xA0 : (byte)0x86)),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 0, 0, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = "ดูคำแปล Google"
            };
            peek.Click += (_, _) => { entry.ShowGoogle = !entry.ShowGoogle; Update(entry); };

            DockPanel.SetDock(peek, Dock.Right);
            dock.Children.Add(peek);
            dock.Children.Add(thai);
            panel.Children.Add(dock);
        }
        else
        {
            panel.Children.Add(thai);
        }

        if (canPeek && entry.ShowGoogle)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Google · " + entry.GoogleThai,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA1)),
                FontSize = _config.OverlayFontSize - 4,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        return panel;
    }

    public void ClearLines()
    {
        Lines.Children.Clear();
        _rows.Clear();
    }

    // ---------- ล็อกให้เมาส์ทะลุ ----------

    private void Lock_Click(object sender, RoutedEventArgs e) => SetLocked(true);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SetLocked(bool locked)
    {
        _locked = locked;

        var hwnd = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);

        if (locked)
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_LAYERED);
            TitleBar.Visibility = Visibility.Collapsed;
            ResizeMode = ResizeMode.NoResize;
            Shell.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x3A, 0x42, 0x4E));
        }
        else
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, style & ~WS_EX_TRANSPARENT);
            TitleBar.Visibility = Visibility.Visible;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Shell.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x4E));
        }
    }

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void SaveBounds(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Normal) return;

        _config.OverlayX = Left;
        _config.OverlayY = Top;
        _config.OverlayWidth = Width;
        _config.OverlayHeight = Height;
    }

    // ---------- ปุ่มลัด Ctrl+Shift+L ปลดล็อก ----------

    private void RegisterHotkey()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var src = HwndSource.FromHwnd(hwnd);
        src?.AddHook(HotkeyHook);

        RegisterHotKey(hwnd, HotkeyId, MOD_CONTROL | MOD_SHIFT, VK_L);
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            SetLocked(!_locked);
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        try { UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId); } catch { }
        base.OnClosed(e);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0xA731;
    private const uint MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004;
    private const uint VK_L = 0x4C;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
