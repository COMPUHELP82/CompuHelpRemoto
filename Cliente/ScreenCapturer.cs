using System.Drawing.Imaging;

namespace CompuHelpRemoto;

/// <summary>
/// Captura la pantalla principal y devuelve solo los bloques (128x128) que cambiaron, en JPEG.
/// </summary>
public sealed class ScreenCapturer : IDisposable
{
    const int T = 128;
    Bitmap _cur, _prev;
    public Rectangle Bounds { get; private set; }
    readonly ImageCodecInfo _jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    readonly EncoderParameters _encParams = new(1);

    public ScreenCapturer(int quality)
    {
        _encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 20, 95));
        Init();
    }

    void Init()
    {
        _cur?.Dispose(); _prev?.Dispose();
        Bounds = Screen.PrimaryScreen.Bounds;
        _cur = new Bitmap(Bounds.Width, Bounds.Height, PixelFormat.Format32bppRgb);
        _prev = new Bitmap(Bounds.Width, Bounds.Height, PixelFormat.Format32bppRgb);
    }

    /// <summary>True si cambió la resolución (se reinicia y hay que mandar todo).</summary>
    public bool CheckResolutionChanged()
    {
        if (Screen.PrimaryScreen.Bounds == Bounds) return false;
        Init();
        return true;
    }

    public unsafe List<(int x, int y, byte[] jpg)> Capture(bool full)
    {
        using (var g = Graphics.FromImage(_cur))
            g.CopyFromScreen(Bounds.X, Bounds.Y, 0, 0, Bounds.Size, CopyPixelOperation.SourceCopy);

        int W = Bounds.Width, H = Bounds.Height;
        var changed = new List<Rectangle>();
        var rect = new Rectangle(0, 0, W, H);
        var dc = _cur.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        var dp = _prev.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            byte* pc = (byte*)dc.Scan0, pp = (byte*)dp.Scan0;
            int stride = dc.Stride;
            for (int ty = 0; ty < H; ty += T)
                for (int tx = 0; tx < W; tx += T)
                {
                    int w = Math.Min(T, W - tx), h = Math.Min(T, H - ty);
                    bool diff = full;
                    for (int y = ty; y < ty + h && !diff; y++)
                    {
                        long off = (long)y * stride + tx * 4;
                        var a = new ReadOnlySpan<byte>(pc + off, w * 4);
                        var b = new ReadOnlySpan<byte>(pp + off, w * 4);
                        if (!a.SequenceEqual(b)) diff = true;
                    }
                    if (diff) changed.Add(new Rectangle(tx, ty, w, h));
                }
        }
        finally
        {
            _cur.UnlockBits(dc);
            _prev.UnlockBits(dp);
        }

        var result = new List<(int, int, byte[])>(changed.Count);
        foreach (var r in changed)
        {
            using var tile = _cur.Clone(r, PixelFormat.Format24bppRgb);
            using var ms = new MemoryStream();
            tile.Save(ms, _jpeg, _encParams);
            result.Add((r.X, r.Y, ms.ToArray()));
        }

        (_cur, _prev) = (_prev, _cur);
        return result;
    }

    public void Dispose()
    {
        _cur?.Dispose();
        _prev?.Dispose();
        _encParams.Dispose();
    }
}
