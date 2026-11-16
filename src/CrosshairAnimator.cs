using System;
using System.Collections.Generic;

namespace CrosshairOverlay;

public sealed class CrosshairAnimator
{
    readonly Func<Preset> _preset;
    CrosshairAnimation? _anim;
    readonly Dictionary<(int, bool), CrosshairBitmap> _dynamic = new();
    double _spread, _lastT = -1, _frameT;
    int _frame;
    bool _wasFiring, _playingOnce;

    public CrosshairAnimator(Func<Preset> preset) { _preset = preset; }

    public void Invalidate()
    {
        _anim = null;
        _dynamic.Clear();
        _frame = 0;
        _frameT = 0;
        _playingOnce = false;
    }

    CrosshairAnimation Anim => _anim ??= CrosshairRenderer.RenderAnimation(_preset());

    public bool IsAnimated => Anim.IsAnimated || _preset().IsDynamic;

    public CrosshairBitmap Static => Anim.Frames[0];

    public CrosshairBitmap Update(bool firing, double nowMs)
    {
        var p = _preset();
        double dt = _lastT < 0 ? 0 : Math.Clamp(nowMs - _lastT, 0, 100);
        _lastT = nowMs;
        bool pressed = firing && !_wasFiring;
        _wasFiring = firing;

        var anim = Anim;
        if (anim.IsAnimated)
        {
            switch (p.GifPlayback)
            {
                case GifPlayback.Always:
                    Advance(anim, dt, loop: true);
                    break;
                case GifPlayback.WhileFiring:
                    if (firing) Advance(anim, dt, loop: true);
                    else { _frame = 0; _frameT = 0; }
                    break;
                case GifPlayback.OnClick:
                    if (pressed) { _playingOnce = true; _frame = 0; _frameT = 0; }
                    if (_playingOnce && !Advance(anim, dt, loop: false)) { _playingOnce = false; _frame = 0; _frameT = 0; }
                    break;
            }
            return anim.Frames[Math.Clamp(_frame, 0, anim.Frames.Count - 1)];
        }

        if (p.IsDynamic)
        {
            if (p.SpreadEnabled && p.SpreadAmount > 0)
            {
                double rate = firing ? dt / Math.Max(10, p.SpreadRiseMs) : -dt / Math.Max(10, p.SpreadRecoverMs);
                _spread = Math.Clamp(_spread + rate, 0, 1);
            }
            else _spread = 0;
            int extra = (int)Math.Round(_spread * Math.Max(0, p.SpreadAmount));
            if (p.DynamicStyle == DynamicStyle.Contract) extra = -extra;
            bool fireColor = p.FireColorEnabled && firing;
            var key = (extra, fireColor);
            if (!_dynamic.TryGetValue(key, out var bmp))
                _dynamic[key] = bmp = CrosshairRenderer.Render(p, extra, fireColor ? p.FireColor : null);
            return bmp;
        }

        return anim.Frames[0];
    }

    bool Advance(CrosshairAnimation anim, double dt, bool loop)
    {
        _frameT += dt;
        while (_frameT >= anim.DelaysMs[_frame])
        {
            _frameT -= anim.DelaysMs[_frame];
            _frame++;
            if (_frame >= anim.Frames.Count)
            {
                if (!loop) { _frame = anim.Frames.Count - 1; return false; }
                _frame = 0;
            }
        }
        return true;
    }
}
