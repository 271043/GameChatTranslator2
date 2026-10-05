using System.Drawing;

namespace GameChatTranslator.Core.Capture;

/// <summary>
/// แหล่งภาพ แยกเป็น interface เพื่อสลับระหว่าง GDI กับ Windows Graphics Capture ได้
/// </summary>
public interface ICaptureSource : IDisposable
{
    string Name { get; }
    bool Start(Rectangle region);
    /// <summary>คืนเฟรมล่าสุด ผู้เรียก Dispose เอง คืน null ถ้ายังไม่มีเฟรมใหม่</summary>
    Bitmap? Grab();
}

/// <summary>
/// จับภาพด้วย GDI ใช้ได้กับเกมแบบ Borderless Windowed
/// เบา เชื่อถือได้ ไม่ต้องพึ่ง D3D
/// </summary>
public sealed class GdiCaptureSource : ICaptureSource
{
    private Rectangle _region;

    public string Name => "GDI";

    public bool Start(Rectangle region)
    {
        _region = region;
        return region.Width > 0 && region.Height > 0;
    }

    public Bitmap? Grab()
    {
        if (_region.Width <= 0 || _region.Height <= 0) return null;

        try
        {
            var bmp = new Bitmap(_region.Width, _region.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(_region.Left, _region.Top, 0, 0,
                new Size(_region.Width, _region.Height), CopyPixelOperation.SourceCopy);

            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() { }
}
