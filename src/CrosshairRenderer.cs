using System.Collections.Generic;
using System.Linq;
using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CrosshairOverlay;

public sealed class CrosshairAnimation
{
    public List<CrosshairBitmap> Frames = new();
    public List<int> DelaysMs = new();
    public bool IsAnimated => Frames.Count > 1;
}

public sealed class CrosshairBitmap
{
    public int Width, Height, CenterX, CenterY;
    public int[] Pixels = Array.Empty<int>();
}

public static class CrosshairRenderer
{
    sealed class Layer
    {
        public readonly int W, H;
        public readonly float[] A, R, G, B;
        public Layer(int w, int h) { W = w; H = h; A = new float[w * h]; R = new float[w * h]; G = new float[w * h]; B = new float[w * h]; }

        public void Paint(int x, int y, float cov, Color c)
        {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H || cov <= 0) return;
            int i = y * W + x;
            float a = Math.Min(1f, cov) * (c.A / 255f);
            if (a <= A[i]) return;
            A[i] = a; R[i] = c.R / 255f; G[i] = c.G / 255f; B[i] = c.B / 255f;
        }

        public void Rect(int x0, int y0, int x1, int y1, Color c)
        {
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    Paint(x, y, 1f, c);
        }
    }

    public static Color ParseColor(string? s, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(s))
                return (Color)ColorConverter.ConvertFromString(s.Trim());
        }
        catch { }
        return fallback;
    }

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    public static CrosshairBitmap Render(Preset p, int extraGap = 0, string? colorOverride = null)
    {
        int outline = p.OutlineEnabled ? Math.Clamp(p.OutlineThickness, 1, 4) : 0;
        int pad = outline + 1;
        Layer fill;
        int cx, cy;

        switch (p.Mode)
        {
            case CrosshairMode.Pixels:
                (fill, cx, cy) = RenderPixels(p, pad);
                break;
            case CrosshairMode.Image:
                (fill, cx, cy) = RenderImage(p, pad);
                break;
            default:
                (fill, cx, cy) = RenderShapes(p, pad, extraGap, colorOverride);
                break;
        }

        return Compose(fill, cx, cy, outline, ParseColor(p.OutlineColor, Colors.Black), p.Opacity);
    }

    static (Layer, int, int) RenderShapes(Preset p, int pad, int extraGap = 0, string? colorOverride = null)
    {
        var color = ParseColor(colorOverride ?? p.Color, Colors.Lime);
        int len = Math.Max(0, p.CrossLength), t = Math.Max(1, p.CrossThickness), gap = Math.Max(0, p.CrossGap + extraGap);
        int circleRadius = Math.Max(1, p.CircleRadius + extraGap);
        int dot = Math.Max(1, p.DotSize);

        bool even = p.CrossEnabled ? t % 2 == 0 : p.DotEnabled ? dot % 2 == 0 : false;

        int ext = 2;
        if (p.CrossEnabled) ext = Math.Max(ext, t + gap + len + 1);
        if (p.CircleEnabled) ext = Math.Max(ext, circleRadius + p.CircleThickness + 2);
        if (p.DotEnabled) ext = Math.Max(ext, dot + 2);
        int size = 2 * (ext + pad) + (even ? 2 : 1);
        int c = even ? size / 2 - 1 : size / 2;
        float m = even ? c + 1 : c + 0.5f;
        var L = new Layer(size, size);

        (int, int) Band(int w) { int a = (int)MathF.Round(m - w / 2f); return (a, a + w - 1); }

        if (p.CrossEnabled && len > 0)
        {
            var (b0, b1) = Band(t);
            L.Rect(b1 + 1 + gap, b0, b1 + gap + len, b1, color);
            L.Rect(b0 - gap - len, b0, b0 - 1 - gap, b1, color);
            L.Rect(b0, b1 + 1 + gap, b1, b1 + gap + len, color);
            if (!p.TStyle) L.Rect(b0, b0 - gap - len, b1, b0 - 1 - gap, color);
            if (gap == 0) L.Rect(b0, b0, b1, b1, color);
        }

        if (p.CircleEnabled && circleRadius > 0)
        {
            float r = circleRadius, half = Math.Max(1, p.CircleThickness) / 2f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - m, dy = y + 0.5f - m;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    L.Paint(x, y, Math.Clamp(half + 0.5f - Math.Abs(d - r), 0f, 1f), color);
                }
        }

        if (p.DotEnabled)
        {
            if (p.DotRound && dot > 2)
            {
                float rad = dot / 2f;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float dx = x + 0.5f - m, dy = y + 0.5f - m;
                        L.Paint(x, y, Math.Clamp(rad + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f), color);
                    }
            }
            else
            {
                int s = (dot % 2 == 0) == even ? dot : dot + 1;
                var (d0, d1) = Band(s);
                L.Rect(d0, d0, d1, d1, color);
            }
        }

        int cc = even ? c + 1 : c;
        return (L, cc, cc);
    }

    static (Layer, int, int) RenderPixels(Preset p, int pad)
    {
        int g = Math.Clamp(p.GridSize, 5, 63), k = Math.Clamp(p.PixelScale, 1, 8);
        int size = g * k + 2 * pad;
        var L = new Layer(size, size);
        foreach (var kv in p.Pixels)
        {
            int x = kv.Key % g, y = kv.Key / g;
            if (y >= g) continue;
            var col = ParseColor(kv.Value, Colors.White);
            L.Rect(pad + x * k, pad + y * k, pad + x * k + k - 1, pad + y * k + k - 1, col);
        }
        int c = pad + (g / 2) * k + k / 2;
        return (L, c, c);
    }

    static (Layer, int, int) RenderImage(Preset p, int pad)
    {
        BitmapSource? src = null;
        int target = Math.Clamp(p.ImageSize, 4, 512);
        try
        {
            if (!string.IsNullOrEmpty(p.ImagePath) && File.Exists(p.ImagePath))
                src = LoadImage(p.ImagePath!, target);
        }
        catch { src = null; }

        return RenderBitmap(src, pad);
    }

    static (Layer, int, int) RenderBitmap(BitmapSource? src, int pad)
    {
        if (src == null)
        {
            var e = new Layer(3 + 2 * pad, 3 + 2 * pad);
            e.Rect(pad, pad, pad + 2, pad + 2, Colors.Magenta);
            return (e, pad + 1, pad + 1);
        }

        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var px = new int[w * h];
        conv.CopyPixels(px, w * 4, 0);
        var L = new Layer(w + 2 * pad, h + 2 * pad);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint v = (uint)px[y * w + x];
                byte a = (byte)(v >> 24);
                if (a == 0) continue;
                L.Paint(x + pad, y + pad, 1f, Color.FromArgb(a, (byte)(v >> 16), (byte)(v >> 8), (byte)v));
            }
        return (L, pad + w / 2, pad + h / 2);
    }

        static BitmapSource LoadImage(string path, int target)
    {
        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            var settings = new SharpVectors.Renderers.Wpf.WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true };
            using var reader = new SharpVectors.Converters.FileSvgReader(settings);
            DrawingGroup drawing = reader.Read(path);
            Rect bounds = drawing.Bounds;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidDataException();
            double scale = target / Math.Max(bounds.Width, bounds.Height);
            int w = Math.Max(1, (int)Math.Round(bounds.Width * scale)), h = Math.Max(1, (int)Math.Round(bounds.Height * scale));
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
                dc.DrawDrawing(drawing);
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            return rtb;
        }

        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.UriSource = new Uri(path);
        bi.EndInit();
        bi.Freeze();
        if (bi.PixelWidth <= target && bi.PixelHeight <= target) return bi;
        double sc = (double)target / Math.Max(bi.PixelWidth, bi.PixelHeight);
        var tb = new TransformedBitmap(bi, new ScaleTransform(sc, sc));
        tb.Freeze();
        return tb;
    }

    public static bool IsGif(string? path) => path != null && path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

    public static CrosshairAnimation RenderAnimation(Preset p)
    {
        var anim = new CrosshairAnimation();
        if (p.Mode == CrosshairMode.Image && IsGif(p.ImagePath) && File.Exists(p.ImagePath))
        {
            try
            {
                int outline = p.OutlineEnabled ? Math.Clamp(p.OutlineThickness, 1, 4) : 0;
                int pad = outline + 1;
                var oc = ParseColor(p.OutlineColor, Colors.Black);
                foreach (var (frame, delay) in LoadGifFrames(p.ImagePath!, Math.Clamp(p.ImageSize, 4, 512)))
                {
                    var (fill, cx, cy) = RenderBitmap(frame, pad);
                    anim.Frames.Add(Compose(fill, cx, cy, outline, oc, p.Opacity));
                    anim.DelaysMs.Add(delay);
                }
            }
            catch { anim.Frames.Clear(); anim.DelaysMs.Clear(); }
        }
        if (anim.Frames.Count == 0)
        {
            anim.Frames.Add(Render(p));
            anim.DelaysMs.Add(100);
        }
        return anim;
    }

    static object? Query(BitmapMetadata? m, string q)
    {
        try { return m?.ContainsQuery(q) == true ? m.GetQuery(q) : null; } catch { return null; }
    }

    static List<(BitmapSource, int)> LoadGifFrames(string path, int target)
    {
        var dec = new GifBitmapDecoder(new Uri(path), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var result = new List<(BitmapSource, int)>();
        if (dec.Frames.Count == 0) return result;
        int W = Convert.ToInt32(Query(dec.Metadata as BitmapMetadata, "/logscrdesc/Width") ?? dec.Frames[0].PixelWidth);
        int H = Convert.ToInt32(Query(dec.Metadata as BitmapMetadata, "/logscrdesc/Height") ?? dec.Frames[0].PixelHeight);
        W = Math.Clamp(W, 1, 2048); H = Math.Clamp(H, 1, 2048);
        var canvas = new int[W * H];
        foreach (var frame in dec.Frames.Take(300))
        {
            var fm = frame.Metadata as BitmapMetadata;
            int left = Convert.ToInt32(Query(fm, "/imgdesc/Left") ?? 0);
            int top = Convert.ToInt32(Query(fm, "/imgdesc/Top") ?? 0);
            int delay = Convert.ToInt32(Query(fm, "/grctlext/Delay") ?? 10) * 10;
            if (delay < 20) delay = 100;
            int disposal = Convert.ToInt32(Query(fm, "/grctlext/Disposal") ?? 0);
            int[]? saved = disposal == 3 ? (int[])canvas.Clone() : null;

            var conv = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int fw = conv.PixelWidth, fh = conv.PixelHeight;
            var px = new int[fw * fh];
            conv.CopyPixels(px, fw * 4, 0);
            for (int y = 0; y < fh; y++)
            {
                int cy = top + y;
                if ((uint)cy >= (uint)H) continue;
                for (int x = 0; x < fw; x++)
                {
                    int cx = left + x;
                    if ((uint)cx >= (uint)W) continue;
                    int v = px[y * fw + x];
                    if (((uint)v >> 24) != 0) canvas[cy * W + cx] = v;
                }
            }

            BitmapSource snap = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, (int[])canvas.Clone(), W * 4);
            if (W > target || H > target)
            {
                double sc = (double)target / Math.Max(W, H);
                snap = new TransformedBitmap(snap, new ScaleTransform(sc, sc));
            }
            snap.Freeze();
            result.Add((snap, delay));

            if (disposal == 2)
                for (int y = 0; y < fh; y++)
                    for (int x = 0; x < fw; x++)
                    {
                        int cx = left + x, cy = top + y;
                        if ((uint)cx < (uint)W && (uint)cy < (uint)H) canvas[cy * W + cx] = 0;
                    }
            else if (saved != null) canvas = saved;
        }
        return result;
    }

    static CrosshairBitmap Compose(Layer f, int cx, int cy, int outline, Color oc, double opacity)
    {
        int w = f.W, h = f.H;
        var outA = new float[w * h];
        if (outline > 0)
        {
            float rr = (outline + 0.5f) * (outline + 0.5f);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float m = 0;
                    for (int dy = -outline; dy <= outline; dy++)
                    {
                        int yy = y + dy;
                        if ((uint)yy >= (uint)h) continue;
                        for (int dx = -outline; dx <= outline; dx++)
                        {
                            int xx = x + dx;
                            if ((uint)xx >= (uint)w || dx * dx + dy * dy > rr) continue;
                            float a = f.A[yy * w + xx];
                            if (a > m) m = a;
                        }
                    }
                    outA[y * w + x] = Math.Min(1f, m) * (oc.A / 255f);
                }
        }

        float op = (float)Math.Clamp(opacity, 0, 1);
        float oR = oc.R / 255f, oG = oc.G / 255f, oB = oc.B / 255f;
        var res = new CrosshairBitmap { Width = w, Height = h, CenterX = cx, CenterY = cy, Pixels = new int[w * h] };
        for (int i = 0; i < w * h; i++)
        {
            float fa = f.A[i], oa = outA[i] * (1 - fa);
            float a = fa + oa;
            if (a <= 0.001f) continue;

            float r = f.R[i] * fa + oR * oa, g = f.G[i] * fa + oG * oa, b = f.B[i] * fa + oB * oa;
            a *= op; r *= op; g *= op; b *= op;
            int A = (int)(a * 255 + 0.5f), R = (int)(r * 255 + 0.5f), G = (int)(g * 255 + 0.5f), B = (int)(b * 255 + 0.5f);
            res.Pixels[i] = (A << 24) | (R << 16) | (G << 8) | B;
        }
        return res;
    }

    public static WriteableBitmap ToWriteable(CrosshairBitmap bmp)
    {
        var wb = new WriteableBitmap(bmp.Width, bmp.Height, 96, 96, PixelFormats.Pbgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, bmp.Width, bmp.Height), bmp.Pixels, bmp.Width * 4, 0);
        wb.Freeze();
        return wb;
    }

    internal static string Hex(byte a, byte r, byte g, byte b) => string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", a, r, g, b);
}
