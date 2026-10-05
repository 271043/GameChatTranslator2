using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace GameChatTranslator.UI;

public partial class RegionSelectorWindow : Window
{
    private Point _start;
    private bool _dragging;
    private double _dpi = 1.0;
    private int _vLeft, _vTop;

    public System.Drawing.Rectangle? SelectedRegion { get; private set; }

    public RegionSelectorWindow()
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

        SetWindowPos(src.Handle, IntPtr.Zero, _vLeft, _vTop, w, h,
            SWP_NOZORDER | SWP_NOACTIVATE);

        Hint.Loaded += (_, _) =>
        {
            Canvas.SetLeft(Hint, (w / _dpi - Hint.ActualWidth) / 2);
            Canvas.SetTop(Hint, h / _dpi * 0.34);
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

        if (w < 20 || h < 10)
        {
            Cancel();
            return;
        }

        SelectedRegion = new System.Drawing.Rectangle(
            _vLeft + (int)Math.Round(l * _dpi),
            _vTop + (int)Math.Round(t * _dpi),
            (int)Math.Round(w * _dpi),
            (int)Math.Round(h * _dpi));

        DialogResult = true;
        Close();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Cancel();
    }

    private void Cancel()
    {
        SelectedRegion = null;
        DialogResult = false;
        Close();
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

    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after,
        int x, int y, int cx, int cy, uint flags);
}
