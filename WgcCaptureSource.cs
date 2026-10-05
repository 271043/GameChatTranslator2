using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GameChatTranslator.Core.Capture;

/// <summary>
/// จับภาพด้วย Windows Graphics Capture
/// ทำงานฝั่ง GPU จับได้แม้เกมเป็น Fullscreen Exclusive และแทบไม่กิน CPU
/// วิธีทำงาน: รับเฟรมทั้งจอจาก GPU แล้วคัดลอกเฉพาะกรอบแชตลง staging texture ก่อนอ่านลง RAM
/// การครอปเกิดบน GPU จึงถูกกว่าการดึงทั้งจอลงมาแล้วค่อยครอปมาก
/// </summary>
public sealed class WgcCaptureSource : ICaptureSource
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _staging;
    private IDirect3DDevice? _rtDevice;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;

    private Rectangle _region;
    private Rectangle _monitorBounds;
    private readonly object _gate = new();

    public string Name => "Windows Graphics Capture";

    public static bool IsSupported()
    {
        try { return GraphicsCaptureSession.IsSupported(); }
        catch { return false; }
    }

    public bool Start(Rectangle region)
    {
        try
        {
            _region = region;

            // หาจอที่กรอบแชตอยู่ แล้วจับทั้งจอนั้น
            var hmon = MonitorFromPoint(
                new POINT { X = region.X + region.Width / 2, Y = region.Y + region.Height / 2 },
                MONITOR_DEFAULTTONEAREST);

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hmon, ref info)) return false;

            _monitorBounds = Rectangle.FromLTRB(
                info.rcMonitor.left, info.rcMonitor.top,
                info.rcMonitor.right, info.rcMonitor.bottom);

            CreateDevice();

            _item = CreateItemForMonitor(hmon);
            if (_item == null) return false;

            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _rtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _item.Size);

            _session = _pool.CreateCaptureSession(_item);

            // ปิดเคอร์เซอร์และกรอบสีเหลือง ถ้า Windows รุ่นนั้นรองรับ
            try { _session.IsCursorCaptureEnabled = false; } catch { }

            // IsBorderRequired มีเฉพาะ Windows SDK contract ใหม่กว่า TFM ของโปรเจกต์นี้
            // ใช้ reflection กันไว้เผื่อรันบน Windows ที่รองรับ ไม่ทำให้คอมไพล์พังบนรุ่นที่ไม่มี
            try { _session.GetType().GetProperty("IsBorderRequired")?.SetValue(_session, false); } catch { }

            CreateStaging(region.Width, region.Height);

            _session.StartCapture();
            return true;
        }
        catch
        {
            Dispose();
            return false;
        }
    }

    public Bitmap? Grab()
    {
        lock (_gate)
        {
            if (_pool == null || _device == null || _context == null || _staging == null)
                return null;

            using var frame = _pool.TryGetNextFrame();
            if (frame == null) return null;

            using var surface = frame.Surface;
            using var source = GetTexture(surface);
            if (source == null) return null;

            // แปลงพิกัดจากทั้งเดสก์ท็อปเป็นพิกัดภายในจอนั้น
            int left = _region.X - _monitorBounds.X;
            int top = _region.Y - _monitorBounds.Y;

            var desc = source.Description;
            if (left < 0 || top < 0 ||
                left + _region.Width > desc.Width ||
                top + _region.Height > desc.Height)
                return null;

            var box = new Box(left, top, 0, left + _region.Width, top + _region.Height, 1);
            _context.CopySubresourceRegion(_staging, 0, 0, 0, 0, source, 0, box);

            var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            try
            {
                var bmp = new Bitmap(_region.Width, _region.Height, PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(
                    new Rectangle(0, 0, _region.Width, _region.Height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                try
                {
                    unsafe
                    {
                        byte* src = (byte*)map.DataPointer;
                        byte* dst = (byte*)data.Scan0;
                        int rowBytes = _region.Width * 4;

                        for (int y = 0; y < _region.Height; y++)
                            Buffer.MemoryCopy(
                                src + (long)y * map.RowPitch,
                                dst + (long)y * data.Stride,
                                rowBytes, rowBytes);
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }

                return bmp;
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
        }
    }

    private void CreateDevice()
    {
        // BgraSupport จำเป็นสำหรับ WinRT interop
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
            out var device).CheckError();

        _device = device;
        _context = device.ImmediateContext;

        using var dxgi = device.QueryInterface<IDXGIDevice>();
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        try
        {
            _rtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    private void CreateStaging(int width, int height)
    {
        _staging?.Dispose();
        _staging = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });
    }

    private static GraphicsCaptureItem? CreateItemForMonitor(IntPtr hmon)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = typeof(GraphicsCaptureItem).GUID;

        int hr = interop.CreateForMonitor(hmon, ref iid, out var raw);
        if (hr != 0 || raw == IntPtr.Zero) return null;

        try
        {
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(raw);
        }
        finally
        {
            Marshal.Release(raw);
        }
    }

    private static ID3D11Texture2D? GetTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = typeof(ID3D11Texture2D).GUID;

        int hr = access.GetInterface(ref iid, out var raw);
        if (hr != 0 || raw == IntPtr.Zero) return null;

        return new ID3D11Texture2D(raw);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _session?.Dispose(); } catch { }
            try { _pool?.Dispose(); } catch { }
            _session = null;
            _pool = null;
            _item = null;

            _staging?.Dispose(); _staging = null;
            _context?.Dispose(); _context = null;
            _device?.Dispose(); _device = null;
            _rtDevice = null;
        }
    }

    // ---------- interop ----------

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
        int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        int GetInterface(ref Guid iid, out IntPtr obj);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
