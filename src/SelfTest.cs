using System.Linq;
using System.Text;

namespace CrosshairOverlay;

internal static class SelfTest
{
    public static string Run()
    {
        var sb = new StringBuilder();
        int fails = 0, total = 0;
        foreach (var t in new[] { 1, 2, 3, 4 })
        foreach (var dot in new[] { 1, 2, 3, 4, 5 })
        foreach (var round in new[] { false, true })
        foreach (var gap in new[] { 0, 3, 4 })
        foreach (var circle in new[] { false, true })
        foreach (var outline in new[] { false, true })
        {
            var p = new Preset
            {
                CrossThickness = t, DotSize = dot, DotRound = round, CrossGap = gap, CrossLength = 6,
                CircleEnabled = circle, CircleRadius = 9, OutlineEnabled = outline,
            };
            total++;
            var b = CrosshairRenderer.Render(p);
            string? err = Check(b);
            if (err != null) { fails++; sb.AppendLine($"FAIL t={t} dot={dot} round={round} gap={gap} circle={circle} outline={outline}: {err}"); }
        }
        foreach (var p in Preset.Defaults())
        {
            total++;
            var b = CrosshairRenderer.Render(p);

            string? err = Check(b, vertical: !p.TStyle);
            if (err != null) { fails++; sb.AppendLine($"FAIL preset «{p.Name}»: {err}"); }
        }
        foreach (var (name, ok, detail) in AnimationChecks())
        {
            total++;
            if (!ok) { fails++; sb.AppendLine($"FAIL {name}: {detail}"); }
            else sb.AppendLine($"ok   {name}: {detail}");
        }
        sb.Insert(0, $"{total - fails}/{total} OK\n");
        return sb.ToString();
    }

    static System.Collections.Generic.IEnumerable<(string, bool, string)> AnimationChecks()
    {
        string gif = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"crosshair-selftest-{System.Guid.NewGuid():N}.gif");
        var enc = new System.Windows.Media.Imaging.GifBitmapEncoder();
        foreach (var c in new[] { System.Windows.Media.Colors.Red, System.Windows.Media.Colors.Lime, System.Windows.Media.Colors.Blue })
        {
            var px = new int[16 * 16];
            System.Array.Fill(px, unchecked((int)(0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B)));
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(
                System.Windows.Media.Imaging.BitmapSource.Create(16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, 64)));
        }
        using (var fs = System.IO.File.Create(gif)) enc.Save(fs);

        var p = new Preset { Mode = CrosshairMode.Image, ImagePath = gif, ImageSize = 16, OutlineEnabled = false };
        var anim = CrosshairRenderer.RenderAnimation(p);
        yield return ("gif frames", anim.Frames.Count == 3, $"кадров {anim.Frames.Count}, задержки {string.Join(",", anim.DelaysMs)}");

        int Idx(CrosshairBitmap b) => anim.Frames.FindIndex(f => f.Pixels.SequenceEqual(b.Pixels));
        int d = anim.DelaysMs.Count > 0 ? anim.DelaysMs[0] : 100;

        p.GifPlayback = GifPlayback.Always;
        var a = new CrosshairAnimator(() => p);
        var seq = new[] { 0, 1, 2, 3 }.Select(k => Idx(a.Update(false, k * d + 1))).ToArray();
        yield return ("gif Always", seq.SequenceEqual(new[] { 0, 1, 2, 0 }), string.Join(",", seq));

        p.GifPlayback = GifPlayback.WhileFiring;
        a = new CrosshairAnimator(() => p);
        var idle = new[] { 0, 1, 2 }.Select(k => Idx(a.Update(false, k * d + 1))).ToArray();
        var fire = new[] { 3, 4, 5 }.Select(k => Idx(a.Update(true, k * d + 1))).ToArray();
        var after = Idx(a.Update(false, 6 * d + 1));
        yield return ("gif WhileFiring", idle.All(i => i == 0) && fire.SequenceEqual(new[] { 1, 2, 0 }) && after == 0,
            $"без ЛКМ {string.Join(",", idle)}; с ЛКМ {string.Join(",", fire)}; отпустили {after}");

        p.GifPlayback = GifPlayback.OnClick;
        a = new CrosshairAnimator(() => p);
        a.Update(false, 0);
        var click = new[] { Idx(a.Update(true, 1)), Idx(a.Update(false, d + 1)), Idx(a.Update(false, 2 * d + 1)), Idx(a.Update(false, 3 * d + 1)), Idx(a.Update(false, 5 * d + 1)) };
        yield return ("gif OnClick", click.SequenceEqual(new[] { 0, 1, 2, 0, 0 }), string.Join(",", click));
        try { System.IO.File.Delete(gif); } catch { }

        var s = new Preset { SpreadEnabled = true, SpreadAmount = 6, SpreadRiseMs = 100, SpreadRecoverMs = 200, FireColorEnabled = true, FireColor = "#FFFF0000" };
        var sa = new CrosshairAnimator(() => s);
        int w0 = sa.Update(false, 0).Width;
        CrosshairBitmap Step(bool fire, int from, int to) { CrosshairBitmap b = null!; for (int t = from; t <= to; t += 16) b = sa.Update(fire, t); return b; }
        int wHalf = Step(true, 16, 48).Width;
        int wFull = Step(true, 64, 160).Width;
        var firing = sa.Update(true, 176);
        int wBack = Step(false, 192, 450).Width;
        bool red = firing.Pixels.Any(v => (v & 0x00FFFFFF) == 0x00FF0000);
        yield return ("spread", w0 < wHalf && wHalf < wFull && wBack == w0 && red,
            $"ширина {w0} → {wHalf} → {wFull} → {wBack}, красный при стрельбе: {red}");

        var cp = new Preset { CrossGap = 9, SpreadEnabled = true, DynamicStyle = DynamicStyle.Contract, SpreadAmount = 6, SpreadRiseMs = 100, SpreadRecoverMs = 200, CircleEnabled = true, CircleRadius = 18 };
        var ca = new CrosshairAnimator(() => cp);
        CrosshairBitmap CStep(bool fire, int from, int to) { CrosshairBitmap b = null!; for (int t = from; t <= to; t += 16) b = ca.Update(fire, t); return b; }
        int Arm(CrosshairBitmap b)
        {
            int cy = b.CenterY, x = b.CenterX + 1;
            while (x < b.Width && (b.Pixels[cy * b.Width + x] & 0x00FFFFFF) == 0) x++;
            return x - b.CenterX;
        }
        int g0 = Arm(ca.Update(false, 0));
        int gFire = Arm(CStep(true, 16, 160));
        int gBack = Arm(CStep(false, 176, 450));
        yield return ("contract", gFire < g0 && gBack == g0, $"отступ луча от центра {g0} → {gFire} (при стрельбе) → {gBack}");

        var changed = Preset.CreateDefault("classic")!;
        changed.CrossGap = 20; changed.Color = "#FF0000FF";
        var reset = Preset.CreateDefault(changed.DefaultId)!;
        var orig = Preset.Defaults().First();
        yield return ("reset default", reset.CrossGap == orig.CrossGap && reset.Color == orig.Color && Preset.Defaults().All(x => x.DefaultId != null),
            $"gap {reset.CrossGap}, цвет {reset.Color}, стандартных {Preset.Defaults().Count()}");
    }

        static string? Check(CrosshairBitmap b, bool vertical = true)
    {
        bool even = b.Width % 2 == 0;
        int Mx(int x) => even ? 2 * b.CenterX - 1 - x : 2 * b.CenterX - x;
        int My(int y) => even ? 2 * b.CenterY - 1 - y : 2 * b.CenterY - y;
        if (Mx(0) != b.Width - 1 || My(0) != b.Height - 1) return $"центр не в середине холста ({b.CenterX},{b.CenterY} при {b.Width}×{b.Height})";
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                int v = b.Pixels[y * b.Width + x];
                if (b.Pixels[y * b.Width + Mx(x)] != v) return $"несимметрично по горизонтали в ({x},{y})";
                if (vertical && b.Pixels[My(y) * b.Width + x] != v) return $"несимметрично по вертикали в ({x},{y})";
            }
        return null;
    }
}
