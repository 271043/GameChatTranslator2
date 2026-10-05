using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace GameChatTranslator.UI;

/// <summary>
/// ตัวเลือกกรอบสำหรับ "แปลบางส่วนทันที" (Ctrl+2) — ลากกรอบบนภาพหน้าจอที่ค้างไว้
///
/// กด Ctrl+2 ครั้งแรก: จับภาพทั้งจอเก็บไว้ทันที แล้วเอามาคลุมทั้งหน้าจอ → ทุกอย่างในจอดูหยุดนิ่ง
/// ผู้ใช้ลากกรอบคลุมบรรทัดที่อยากแปล (ลากใหม่กี่ครั้งก็ได้) แล้วกด Ctrl+2 อีกครั้ง
/// MainWindow เอา <see cref="TakeSnapshot"/> + <see cref="SelectionInSnapshot"/> ไปแปล (ไม่จับภาพจอใหม่)
/// </summary>
public partial class PartialSelectorWindow : Window
{
    private Point _start;
    private bool _dragging;
    private double _dpi = 1.0;
    private int _vLeft, _vTop;

    // ภาพทั้งเดสก์ท็อป (พิกเซลจริง) จุด (0,0) ของภาพ = จุด (_vLeft,_vTop) บนหน้าจอ
    private System.Drawing.Bitmap? _snapshot;

    /// <summary>กรอบที่ลากไว้ล่าสุด เป็นพิกเซลจริงบนเดสก์ท็อป — null ถ้ายังไม่เคยลาก</summary>
    public System.Drawing.Rectangle? SelectedRegion { get; private set; }

    /// <summary>ยิงเมื่อผู้ใช้กด Esc — MainWindow เป็นคนปิดหน้าต่าง</summary>
    public event Action? Cancelled;

    /// <summary>ยิงเมื่อลากกรอบเสร็จ (ปล่อยเมาส์) และกรอบใหญ่พอ — MainWindow เอา snapshot+กรอบไปแปลแล้วปิดหน้าต่าง</summary>
    public event Action? Completed;

    public PartialSelectorWindow()
    {
        InitializeComponent();

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += OnKey;
        SourceInitialized += OnInit;
    }

    private void OnInit(object? sender, EventArgs e)
    {
        var src = (HwndSource)PresentationSource.FromVisual(this)!;
        _dpi = src.CompositionTarget.TransformToDevice.M11;

        _vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _vTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        // จับภาพก่อนหน้าต่างนี้โผล่ (SourceInitialized ยิงก่อน render) จะได้ภาพจอสะอาดไม่มีตัวเอง
        _snapshot = GrabScreen(_vLeft, _vTop, w, h);

        SetWindowPos(src.Handle, IntPtr.Zero, _vLeft, _vTop, w, h,
            SWP_NOZORDER | SWP_NOACTIVATE);

        double dipW = w / _dpi, dipH = h / _dpi;
        Dim.Width = dipW;
        Dim.Height = dipH;

        if (_snapshot != null)
        {
            Frost.Source = ToBitmapSource(_snapshot);
            Frost.Width = dipW;
            Frost.Height = dipH;
        }

        Hint.Loaded += (_, _) =>
        {
            Canvas.SetLeft(Hint, (dipW - Hint.ActualWidth) / 2);
            Canvas.SetTop(Hint, dipH * 0.34);
        };
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(RootCanvas);
        _dragging = true;

        Hint.Visibility = Visibility.Collapsed;
        SelectionBox.Visibility = Visibility.Visible;
        SizeLabel.Visibility = Visibility.Visible;

        Draw(_start);
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_dragging) Draw(e.GetPosition(RootCanvas));
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();

        var end = e.GetPosition(RootCanvas);

        double l = Math.Min(_start.X, end.X);
        double t = Math.Min(_start.Y, end.Y);
        double w = Math.Abs(end.X - _start.X);
        double h = Math.Abs(end.Y - _start.Y);

        // กรอบเล็กเกินไป (เผลอคลิก) — ทิ้งค่า ปล่อยหน้าต่างค้างไว้ให้ลากใหม่
        if (w < 20 || h < 10)
        {
            SelectionBox.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            return;
        }

        SelectedRegion = new System.Drawing.Rectangle(
            _vLeft + (int)Math.Round(l * _dpi),
            _vTop + (int)Math.Round(t * _dpi),
            (int)Math.Round(w * _dpi),
            (int)Math.Round(h * _dpi));

        Completed?.Invoke();
    }

    private void Draw(Point current)
    {
        double l = Math.Min(_start.X, current.X);
        double t = Math.Min(_start.Y, current.Y);
        double w = Math.Abs(current.X - _start.X);
        double h = Math.Abs(current.Y - _start.Y);

        Canvas.SetLeft(SelectionBox, l);
        Canvas.SetTop(SelectionBox, t);
        SelectionBox.Width = w;
        SelectionBox.Height = h;

        SizeLabel.Text = $"{(int)Math.Round(w * _dpi)} × {(int)Math.Round(h * _dpi)} px";
        Canvas.SetLeft(SizeLabel, l);
        Canvas.SetTop(SizeLabel, Math.Max(0, t - 20));
    }

    /// <summary>กรอบที่ลากไว้ในพิกัดของภาพจอที่ค้างไว้ (จุด 0,0 = มุมซ้ายบนของภาพ) — null ถ้ายังไม่ได้ลาก</summary>
    public System.Drawing.Rectangle? SelectionInSnapshot =>
        SelectedRegion is { } r
            ? new System.Drawing.Rectangle(r.X - _vLeft, r.Y - _vTop, r.Width, r.Height)
            : null;

    /// <summary>ยกภาพจอที่ค้างไว้ให้ผู้เรียกไปจัดการต่อ (ผู้เรียก Dispose เอง) หน้าต่างนี้จะไม่แตะอีก</summary>
    public System.Drawing.Bitmap? TakeSnapshot()
    {
        var s = _snapshot;
        _snapshot = null;
        return s;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Cancelled?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        _snapshot?.Dispose();
        _snapshot = null;
        base.OnClosed(e);
    }

    private static System.Drawing.Bitmap? GrabScreen(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;

        try
        {
            var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h),
                System.Drawing.CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
    {
        var hBitmap = bmp.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
