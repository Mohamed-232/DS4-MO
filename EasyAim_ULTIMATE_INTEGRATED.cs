using CustomMacroBase;
using CustomMacroBase.GamePadState;
using CustomMacroBase.Helper;
using CustomMacroBase.Helper.Attributes;
using CustomMacroBase.Helper.Tools.FlowManager;
using CustomMacroBase.Helper.Tools.TimeManager;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

using DrawingPF = System.Drawing.Imaging.PixelFormat;

namespace CustomMacroPlugin0.GameList
{
    internal enum ActivationMode
    {
        Always, ADS, Fire, ADSPlusFire, Hipfire,
        Toggle, Hold, DoubleTap, Timed
    }

    internal enum ColorAimActivation { ADSOnly, ADSPlusFire, FireOnly, Always }

    internal enum ColorAimPreset
    {
        HexInput, Custom, Red, Pink, White, Yellow, Cyan, Orange, Purple, Green,
        Black, HeroRivals, Fortnite, Apex, Warzone, COD
    }

    internal enum ScanAreaMode { Circle, Square }

    internal enum AimPatternMode
    {
        SpiralOut, SpiralIn, Pulse, RapidPulse,
        MicroJitter, SnapAim, PredictiveAim, CircleShake2
    }

    internal enum ADS { L1, L2 }
    internal enum Gunfire { R1, R2 }

    internal enum YYSwapButton
    {
        Triangle, Cross, Square, Circle,
        L1, R1, L2, R2, L3, R3,
        Options, Share, TouchPad
    }

    internal enum StabilizerMode
    {
        Off,
        MicroCorrect,
        Figure8,
        Breath,
        AntiDrift,
        Orbit,
    }

    // ════════════════════════════════════════════════════════════════════════════════
    // FOV OVERLAY WINDOW
    // ════════════════════════════════════════════════════════════════════════════════

    internal sealed class FovOverlayWindow : Window
    {
        private readonly System.Windows.Shapes.Ellipse _circle;
        private readonly System.Windows.Shapes.Ellipse _dot;
        private readonly System.Windows.Controls.Canvas _canvas;

        public FovOverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = System.Windows.Media.Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            IsHitTestVisible = false;

            var screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            Left = screen.Left; Top = screen.Top;
            Width = screen.Width; Height = screen.Height;

            _canvas = new System.Windows.Controls.Canvas
            {
                Width = screen.Width,
                Height = screen.Height,
                Background = System.Windows.Media.Brushes.Transparent,
                IsHitTestVisible = false
            };

            _circle = new System.Windows.Shapes.Ellipse
            {
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 255, 0, 255)),
                StrokeThickness = 1.5,
                Fill = System.Windows.Media.Brushes.Transparent,
                IsHitTestVisible = false
            };

            _dot = new System.Windows.Shapes.Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 255, 0, 255)),
                IsHitTestVisible = false
            };

            _canvas.Children.Add(_circle);
            _canvas.Children.Add(_dot);
            Content = _canvas;

            Refresh(80, System.Windows.Media.Color.FromArgb(200, 255, 0, 255), 1.5);
        }

        public void Refresh(int radiusPx, System.Windows.Media.Color color, double thickness)
        {
            double diameter = radiusPx * 2;
            double cx = _canvas.Width / 2.0;
            double cy = _canvas.Height / 2.0;
            var brush = new SolidColorBrush(color);
            _circle.Width = diameter; _circle.Height = diameter;
            _circle.Stroke = brush; _circle.StrokeThickness = thickness;
            System.Windows.Controls.Canvas.SetLeft(_circle, cx - radiusPx);
            System.Windows.Controls.Canvas.SetTop(_circle, cy - radiusPx);
            _dot.Fill = brush;
            System.Windows.Controls.Canvas.SetLeft(_dot, cx - _dot.Width / 2);
            System.Windows.Controls.Canvas.SetTop(_dot, cy - _dot.Height / 2);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════════
    // SMOOTHING ENGINE
    // ════════════════════════════════════════════════════════════════════════════════

    internal sealed class SmoothingEngine
    {
        private double _sx, _sy, _vx, _vy;
        public double Alpha { get; set; } = 0.42;
        public double Beta { get; set; } = 0.15;
        public double Decay { get; set; } = 0.55;
        public double Predict { get; set; } = 0.60;

        public void Update(bool found, double rawX, double rawY)
        {
            if (found)
            {
                double prevSX = _sx, prevSY = _sy;
                _sx = Alpha * rawX + (1.0 - Alpha) * _sx;
                _sy = Alpha * rawY + (1.0 - Alpha) * _sy;
                _vx = Beta * (_sx - prevSX) + (1.0 - Beta) * _vx;
                _vy = Beta * (_sy - prevSY) + (1.0 - Beta) * _vy;
            }
            else { _sx *= Decay; _sy *= Decay; _vx *= Decay; _vy *= Decay; }
        }

        public void GetOutput(out int outX, out int outY)
        {
            outX = (int)Math.Round(_sx + _vx * Predict);
            outY = (int)Math.Round(_sy + _vy * Predict);
        }

        public void Reset() { _sx = _sy = _vx = _vy = 0; }
    }

    // ════════════════════════════════════════════════════════════════════════════════
    // ACTIVATION ENGINE
    // ════════════════════════════════════════════════════════════════════════════════

    internal sealed class ActivationEngine
    {
        private int _tapCount;
        private long _lastTapTick;
        private const long DoubleTapWindowMs = 400;
        private bool _toggleState, _prevFire;
        private long _activeSince = -1;
        public int TimedDurationMs { get; set; } = 3000;

        public bool IsActive(ActivationMode mode, bool ads, bool fire)
        {
            switch (mode)
            {
                case ActivationMode.Always: return true;
                case ActivationMode.ADS: return ads;
                case ActivationMode.Fire: return fire;
                case ActivationMode.ADSPlusFire: return ads && fire;
                case ActivationMode.Hipfire: return fire && !ads;
                case ActivationMode.Hold: return fire;
                case ActivationMode.Toggle:
                    {
                        if (fire && !_prevFire) _toggleState = !_toggleState;
                        _prevFire = fire;
                        return _toggleState;
                    }
                case ActivationMode.DoubleTap:
                    {
                        if (fire && !_prevFire)
                        {
                            long now = Environment.TickCount64;
                            if (now - _lastTapTick <= DoubleTapWindowMs)
                            {
                                if (++_tapCount >= 2)
                                { _tapCount = 0; _lastTapTick = 0; _prevFire = fire; return true; }
                            }
                            else _tapCount = 1;
                            _lastTapTick = now;
                        }
                        _prevFire = fire;
                        return false;
                    }
                case ActivationMode.Timed:
                    {
                        if (fire)
                        {
                            if (_activeSince < 0) _activeSince = Environment.TickCount64;
                            return (Environment.TickCount64 - _activeSince) <= TimedDurationMs;
                        }
                        _activeSince = -1;
                        return false;
                    }
            }
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════════
    // RECOIL ENGINE v2
    // ════════════════════════════════════════════════════════════════════════════════

    internal sealed class RecoilEngine
    {
        public double GlobalMultiplier { get; set; } = 1.0;
        public double ADSMultiplier { get; set; } = 0.9;
        public double HipfireMultiplier { get; set; } = 0.45;
        public double SmoothAlpha { get; set; } = 0.32;
        public double WindDownRate { get; set; } = 0.18;
        public double HorizCompensation { get; set; } = 0.70;
        public bool InvertY { get; set; } = false;

        public double DynamicRampRate { get; set; } = 0.012;
        public double DynamicMax { get; set; } = 1.55;

        public double EarlyBoostStrength { get; set; } = 1.35;
        public int EarlyBoostShots { get; set; } = 5;

        public int BurstResetMs { get; set; } = 250;

        private double _dynamicScale = 1.0;
        private double _smoothedY = 0;
        private double _smoothedX = 0;
        private bool _wasGunfire = false;
        private int _shotCount = 0;
        private int _patternIdx = 0;
        private long _lastGunfireTick = 0;

        private double _velY = 0, _velX = 0;
        private const double VelAlpha = 0.25;

        private static readonly (int h, int v)[] DefaultPattern =
        {
            (  0,  6 ), (  1,  7 ), (  0,  8 ), ( -1,  9 ), (  0,  9 ),
            (  1,  8 ), (  2,  8 ), (  1,  7 ), (  0,  7 ), ( -1,  7 ),
            ( -2,  6 ), ( -1,  6 ), (  0,  6 ), (  1,  5 ), (  0,  5 ),
            (  0,  5 ), ( -1,  5 ), (  0,  4 ), (  1,  4 ), (  0,  4 ),
        };

        private (int h, int v)[] _pattern = DefaultPattern;

        public void SetPattern((int h, int v)[] pattern)
        {
            _pattern = pattern ?? DefaultPattern;
            _patternIdx = 0;
        }

        public void Tick(bool gunfire, bool ads, double sliderStrength,
                         out int deltaX, out int deltaY)
        {
            if (!gunfire)
            {
                if (_wasGunfire)
                {
                    _smoothedY *= (1.0 - WindDownRate);
                    _smoothedX *= (1.0 - WindDownRate);
                    _velY *= (1.0 - WindDownRate);
                    _velX *= (1.0 - WindDownRate);
                    _dynamicScale = Math.Max(1.0, _dynamicScale - DynamicRampRate * 4.0);

                    if (Math.Abs(_smoothedY) < 0.3 && Math.Abs(_smoothedX) < 0.3)
                    {
                        _smoothedY = _smoothedX = _velY = _velX = 0;
                        _wasGunfire = false;
                        _shotCount = 0;
                        _patternIdx = 0;
                        _dynamicScale = 1.0;
                    }

                    int sign2 = InvertY ? -1 : 1;
                    deltaY = sign2 * (int)Math.Round(_smoothedY + _velY * 0.4);
                    deltaX = (int)Math.Round(_smoothedX + _velX * 0.4);
                    return;
                }

                _dynamicScale = 1.0;
                _patternIdx = 0;
                _shotCount = 0;
                _smoothedX = _smoothedY = _velY = _velX = 0;
                deltaX = 0; deltaY = 0;
                return;
            }

            long now = Environment.TickCount64;
            if (_wasGunfire && (now - _lastGunfireTick) > BurstResetMs)
            {
                _shotCount = 0;
                _patternIdx = 0;
                _dynamicScale = 1.0;
                _smoothedY = _smoothedX = _velY = _velX = 0;
            }
            _lastGunfireTick = now;
            _wasGunfire = true;

            _dynamicScale = Math.Min(_dynamicScale + DynamicRampRate, DynamicMax);

            int pi = _patternIdx % _pattern.Length;
            var (patH, patV) = _pattern[pi];
            _patternIdx++;
            _shotCount++;

            double baseV = sliderStrength / 100.0 * 16.0;
            double rawV = baseV + patV;

            if (_shotCount <= EarlyBoostShots)
            {
                double boostFade = 1.0 - (_shotCount - 1.0) / EarlyBoostShots;
                rawV *= (1.0 + (EarlyBoostStrength - 1.0) * boostFade);
            }

            double rawH = patH * HorizCompensation;

            double mult = GlobalMultiplier * (ads ? ADSMultiplier : HipfireMultiplier) * _dynamicScale;
            double targetY = rawV * mult;
            double targetX = rawH * mult;

            double prevSY = _smoothedY, prevSX = _smoothedX;
            _smoothedY = SmoothAlpha * targetY + (1.0 - SmoothAlpha) * _smoothedY;
            _smoothedX = SmoothAlpha * targetX + (1.0 - SmoothAlpha) * _smoothedX;

            _velY = VelAlpha * (_smoothedY - prevSY) + (1.0 - VelAlpha) * _velY;
            _velX = VelAlpha * (_smoothedX - prevSX) + (1.0 - VelAlpha) * _velX;

            int sign = InvertY ? -1 : 1;
            deltaY = sign * (int)Math.Round(_smoothedY + _velY * 0.5);
            deltaX = (int)Math.Round(_smoothedX + _velX * 0.5);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════════
    // HAIR TRIGGER ENGINE v2
    // ════════════════════════════════════════════════════════════════════════════════

    internal sealed class HairTriggerEngine
    {
        public byte ActivationThreshold { get; set; } = 30;
        public bool ProgressiveFill { get; set; } = true;
        public byte DeadZone { get; set; } = 5;
        public double Amplification { get; set; } = 1.5;

        public byte Process(byte raw)
        {
            if (raw <= DeadZone) return 0;
            if (raw < ActivationThreshold) return 0;
            if (!ProgressiveFill) return byte.MaxValue;

            double range = 255.0 - ActivationThreshold;
            if (range <= 0) return byte.MaxValue;
            double t = (raw - ActivationThreshold) / range;
            double out_ = ActivationThreshold + t * 255.0 * Amplification;
            return (byte)Math.Min(255, (int)Math.Round(out_));
        }
    }

    // ════════════════════════════════════════════════════════════════════════════════
    // RAPID SPAM ENGINE
    // ════════════════════════════════════════════════════════════════════════════════

    internal sealed class RapidSpamEngine
    {
        public YYSwapButton TargetButton { get; set; } = YYSwapButton.Triangle;
        public YYSwapButton ActivationButton { get; set; } = YYSwapButton.Triangle;
        public int PressMs { get; set; } = 40;
        public int ReleaseMs { get; set; } = 40;

        private bool _pressing = false;
        private long _phaseStart = 0;

        public bool IsHeld(bool[] buttonStates) => buttonStates[(int)ActivationButton];

        public bool Tick(out bool virtualPress)
        {
            long now = Environment.TickCount64;
            if (_phaseStart == 0) _phaseStart = now;
            long elapsed = now - _phaseStart;

            if (_pressing)
            {
                if (elapsed >= PressMs)
                { _pressing = false; _phaseStart = now; virtualPress = false; return true; }
                virtualPress = true; return true;
            }
            else
            {
                if (elapsed >= ReleaseMs)
                { _pressing = true; _phaseStart = now; virtualPress = true; return true; }
                virtualPress = false; return true;
            }
        }

        public void Reset() { _pressing = false; _phaseStart = 0; }
    }

    // ════════════════════════════════════════════════════════════════════════════════
    // AIM STABILIZER ENGINE
    // ════════════════════════════════════════════════════════════════════════════════

    internal sealed class AimStabilizerEngine
    {
        public StabilizerMode Mode { get; set; } = StabilizerMode.Orbit;
        public double Strength { get; set; } = 0.08;
        public double Speed { get; set; } = 1.0;
        public bool OnlyADS { get; set; } = true;
        public bool OnlyFire { get; set; } = false;

        private double _tick = 0;
        private double _driftX = 0, _driftY = 0;
        private readonly Random _rng = new Random();

        public (int dx, int dy) Tick(bool ads, bool fire, int stickX, int stickY)
        {
            if (OnlyADS && !ads) return (0, 0);
            if (OnlyFire && !fire) return (0, 0);
            if (Mode == StabilizerMode.Off) return (0, 0);

            _tick += Speed * 0.05;
            const double Scale = 12.0;
            double s = Strength * Scale;

            switch (Mode)
            {
                case StabilizerMode.MicroCorrect:
                    {
                        double nx = Math.Sin(_tick * 2.3) * Math.Cos(_tick * 1.7);
                        double ny = Math.Cos(_tick * 1.9) * Math.Sin(_tick * 2.1);
                        return ((int)Math.Round(nx * s), (int)Math.Round(ny * s));
                    }
                case StabilizerMode.Figure8:
                    {
                        double x = Math.Sin(_tick) * s;
                        double y = Math.Sin(_tick) * Math.Cos(_tick) * s;
                        return ((int)Math.Round(x), (int)Math.Round(y));
                    }
                case StabilizerMode.Breath:
                    {
                        double breathY = Math.Sin(_tick * 0.4) * s;
                        double breathX = Math.Sin(_tick * 0.7) * s * 0.3;
                        return ((int)Math.Round(breathX), (int)Math.Round(breathY));
                    }
                case StabilizerMode.AntiDrift:
                    {
                        double dist = Math.Sqrt(stickX * stickX + stickY * stickY);
                        if (dist < 3.0) return (0, 0);
                        double corrX = -(stickX / dist) * s * 0.5;
                        double corrY = -(stickY / dist) * s * 0.5;
                        return ((int)Math.Round(corrX), (int)Math.Round(corrY));
                    }
                case StabilizerMode.Orbit:
                    {
                        double ox = Math.Sin(_tick) * s;
                        double oy = Math.Cos(_tick) * s;
                        return ((int)Math.Round(ox), (int)Math.Round(oy));
                    }
            }
            return (0, 0);
        }

        public void Reset() { _tick = 0; _driftX = _driftY = 0; }
    }

    // ════════════════════════════════════════════════════════════════════════════════
    // SCREEN SCANNER
    // ════════════════════════════════════════════════════════════════════════════════

    internal sealed class ScreenScanner : IDisposable
    {
        private Bitmap _bmp;
        private Graphics _gfx;
        private int _size;
        public int Size => _size;

        public void EnsureSize(int diameter)
        {
            if (diameter == _size) return;
            _gfx?.Dispose(); _bmp?.Dispose();
            _size = diameter;
            _bmp = new Bitmap(diameter, diameter, DrawingPF.Format32bppArgb);
            _gfx = Graphics.FromImage(_bmp);
        }

        public void Capture(int cx, int cy)
        {
            int r = _size >> 1;
            _gfx.CopyFromScreen(cx - r, cy - r, 0, 0, _bmp.Size);
        }

        public unsafe BitmapData Lock(out byte* scan0, out int stride)
        {
            var rect = new Rectangle(0, 0, _size, _size);
            var bmd = _bmp.LockBits(rect, ImageLockMode.ReadOnly, DrawingPF.Format32bppArgb);
            scan0 = (byte*)bmd.Scan0;
            stride = bmd.Stride;
            return bmd;
        }

        public void Unlock(BitmapData bmd) => _bmp.UnlockBits(bmd);

        public void Dispose()
        {
            _gfx?.Dispose(); _bmp?.Dispose();
            _gfx = null; _bmp = null;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════════
    // ULTIMATE COLOR AIM ENGINE v11 (PRODUCTION GRADE - INTEGRATED)
    // ════════════════════════════════════════════════════════════════════════════════

    internal sealed class UltimateColorAimEngine : IDisposable
    {
        private enum LockState { Unlocked, Acquiring, Locked, Coasting }
        private LockState _state = LockState.Unlocked;
        private long _stateEnteredTick = Environment.TickCount64;
        private int _framesSinceStateChange = 0;

        private double _lockedX = 0, _lockedY = 0;
        private double _prevLockedX = 0, _prevLockedY = 0;
        private double _velocityX = 0, _velocityY = 0;
        private double _smoothedX = 0, _smoothedY = 0;
        private double _lockedHue = 0;
        private double _lockedSaturation = 0.5;
        private double _lockedValue = 0.5;

        private double _adaptiveAlpha = 0.85;
        private double _adaptiveJitter = 0.0;
        private int _successfulFrames = 0;
        private int _failedFrames = 0;
        private double _confidence = 0.0;

        private readonly ScreenScanner _scanner = new ScreenScanner();
        private (short dx, short dy)[] _spiral;
        private int _spiralLen = 0;
        private int _cachedRadius = -1;

        private int _tickCount = 0;
        private long _lastProfileTick = Environment.TickCount64;
        private double _avgScanMs = 0;
        private int _detectedPixelsAvg = 0;
        private double _frameRateDetected = 60.0;
        private long _lastFrameTick = Environment.TickCount64;

        public ColorAimActivation Activation { get; set; } = ColorAimActivation.ADSOnly;
        public int HexTargetR { get; set; } = 255;
        public int HexTargetG { get; set; } = 0;
        public int HexTargetB { get; set; } = 255;

        public double LockStrength { get; set; } = 100.0;
        public double AcquisitionSpeed { get; set; } = 90.0;
        public int DetectionQuality { get; set; } = 4;

        public double HorizClampFactor { get; set; } = 0.55;
        public double VertBiasTopFraction { get; set; } = 0.45;
        public double DeadZonePx { get; set; } = 1.5;
        public double LockRetentionMs { get; set; } = 150.0;
        public double LockedHueTightness { get; set; } = 0.35;

        public double PredictionStrength { get; set; } = 0.4;
        public double QuantumJitterAmount { get; set; } = 0.8;
        public bool EnableNeuralAdaptation { get; set; } = true;
        public bool EnableRecoilCompensation { get; set; } = true;

        public unsafe bool Tick(
            bool ads, bool fire, int radius,
            int customR, int customG, int customB,
            ref byte virtRX, ref byte virtRY)
        {
            long tickStartMs = Environment.TickCount64;

            bool gate = Activation switch
            {
                ColorAimActivation.ADSOnly => ads,
                ColorAimActivation.ADSPlusFire => ads && fire,
                ColorAimActivation.FireOnly => fire,
                ColorAimActivation.Always => true,
                _ => false
            };

            if (!gate)
            {
                ResetAllState();
                return false;
            }

            UpdateFrameRateEstimate();

            var scr = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            int screenCenterX = scr.Width >> 1;
            int screenCenterY = scr.Height >> 1;
            int diameter = radius * 2;

            _scanner.EnsureSize(diameter);
            _scanner.Capture(screenCenterX, screenCenterY);

            RebuildSpiralIfNeeded(radius);

            int detectedPixels = 0;
            double sumX = 0, sumY = 0;
            double targetHue = GetTargetHue(customR, customG, customB);
            double hueTolerance = GetHueTolerance(targetHue);

            var bmd = _scanner.Lock(out byte* basePtr, out int stride);
            try
            {
                for (int i = 0; i < _spiralLen; i++)
                {
                    if (detectedPixels >= 120) break;

                    short sdx = _spiral[i].dx;
                    short sdy = _spiral[i].dy;
                    int px = radius + sdx;
                    int py = radius + sdy;

                    byte* pixelPtr = basePtr + (py * stride) + (px << 2);
                    byte r = pixelPtr[2];
                    byte g = pixelPtr[1];
                    byte b = pixelPtr[0];

                    RgbToHsv(r, g, b, out double h, out double s, out double v);
                    GetQualityThresholds(DetectionQuality, out double satMin, out double valMin);

                    double hDiff = HueDifference(h, targetHue);
                    double effectiveTol = hueTolerance * 
                        (_state == LockState.Locked ? LockedHueTightness : 1.0);

                    if (hDiff <= effectiveTol && s >= satMin && v >= valMin)
                    {
                        sumX += px;
                        sumY += py;
                        detectedPixels++;
                    }
                }

                _scanner.Unlock(bmd);
            }
            catch { _scanner.Unlock(bmd); throw; }

            _detectedPixelsAvg = (int)(0.7 * _detectedPixelsAvg + 0.3 * detectedPixels);

            if (detectedPixels >= 3)
            {
                double detectedX = (sumX / detectedPixels) - radius;
                double detectedY = (sumY / detectedPixels) - radius;
                UpdateStateMachine_TargetFound(detectedX, detectedY, targetHue);
            }
            else
            {
                UpdateStateMachine_TargetLost();
            }

            if (_state == LockState.Locked || _state == LockState.Coasting)
            {
                ApplyLockAim(ref virtRX, ref virtRY);
                _successfulFrames++;
            }
            else
            {
                _failedFrames++;
            }

            if (EnableNeuralAdaptation)
            {
                AdaptTracking();
            }

            long tickEndMs = Environment.TickCount64;
            double elapsedMs = tickEndMs - tickStartMs;
            _avgScanMs = 0.85 * _avgScanMs + 0.15 * elapsedMs;
            _tickCount++;

            return (_state == LockState.Locked || _state == LockState.Coasting);
        }

        private void UpdateStateMachine_TargetFound(double detectedX, double detectedY, double detectedHue)
        {
            long now = Environment.TickCount64;
            _framesSinceStateChange++;

            switch (_state)
            {
                case LockState.Unlocked:
                    _state = LockState.Acquiring;
                    _framesSinceStateChange = 0;
                    _stateEnteredTick = now;
                    _lockedX = detectedX;
                    _lockedY = detectedY;
                    _lockedHue = detectedHue;
                    _confidence = 0.3;
                    break;

                case LockState.Acquiring:
                    double delta = Math.Sqrt(
                        Math.Pow(detectedX - _lockedX, 2) + 
                        Math.Pow(detectedY - _lockedY, 2));

                    if (delta < 3.0 && _framesSinceStateChange >= 2)
                    {
                        _state = LockState.Locked;
                        _lockedHue = detectedHue;
                        _lockedX = detectedX;
                        _lockedY = detectedY;
                        _smoothedX = detectedX;
                        _smoothedY = detectedY;
                        _prevLockedX = detectedX;
                        _prevLockedY = detectedY;
                        _velocityX = 0;
                        _velocityY = 0;
                        _framesSinceStateChange = 0;
                        _stateEnteredTick = now;
                        _confidence = 0.95;
                    }
                    else
                    {
                        double alpha = 0.6;
                        _lockedX = alpha * detectedX + (1.0 - alpha) * _lockedX;
                        _lockedY = alpha * detectedY + (1.0 - alpha) * _lockedY;
                        _confidence = 0.5 + (_framesSinceStateChange * 0.1);

                        if ((now - _stateEnteredTick) > 300)
                        {
                            _state = LockState.Unlocked;
                            _framesSinceStateChange = 0;
                            _confidence = 0.0;
                        }
                    }
                    break;

                case LockState.Locked:
                    _prevLockedX = _lockedX;
                    _prevLockedY = _lockedY;

                    double trackAlpha = GetAdaptiveAlpha();
                    _lockedX = trackAlpha * detectedX + (1.0 - trackAlpha) * _lockedX;
                    _lockedY = trackAlpha * detectedY + (1.0 - trackAlpha) * _lockedY;

                    _velocityX = 0.7 * _velocityX + 0.3 * (_lockedX - _prevLockedX);
                    _velocityY = 0.7 * _velocityY + 0.3 * (_lockedY - _prevLockedY);

                    _framesSinceStateChange = 0;
                    _stateEnteredTick = now;
                    _confidence = Math.Min(_confidence + 0.02, 1.0);
                    break;

                case LockState.Coasting:
                    _state = LockState.Locked;
                    _lockedX = detectedX;
                    _lockedY = detectedY;
                    _framesSinceStateChange = 0;
                    _stateEnteredTick = now;
                    _confidence = 0.85;
                    break;
            }
        }

        private void UpdateStateMachine_TargetLost()
        {
            long now = Environment.TickCount64;

            switch (_state)
            {
                case LockState.Acquiring:
                    _state = LockState.Unlocked;
                    _framesSinceStateChange = 0;
                    _confidence = 0.0;
                    break;

                case LockState.Locked:
                    _state = LockState.Coasting;
                    _stateEnteredTick = now;
                    _framesSinceStateChange = 0;
                    _confidence = Math.Max(_confidence - 0.15, 0.3);
                    break;

                case LockState.Coasting:
                    if ((now - _stateEnteredTick) > (long)LockRetentionMs)
                    {
                        _state = LockState.Unlocked;
                        _framesSinceStateChange = 0;
                        _confidence = 0.0;
                    }
                    break;

                case LockState.Unlocked:
                    break;
            }
        }

        private void ApplyLockAim(ref byte virtRX, ref byte virtRY)
        {
            double aimX = _lockedX + (_velocityX * PredictionStrength);
            double aimY = _lockedY + (_velocityY * PredictionStrength);

            if (QuantumJitterAmount > 0)
            {
                aimX += (Math.Sin(_tickCount * 0.47) * QuantumJitterAmount * 0.3);
                aimY += (Math.Cos(_tickCount * 0.63) * QuantumJitterAmount * 0.3);
            }

            double dist = Math.Sqrt(aimX * aimX + aimY * aimY);

            if (dist < DeadZonePx) return;

            double ux = aimX / dist;
            double uy = aimY / dist;
            double lockMag = (LockStrength / 100.0) * 127.0;

            int deltaX = (int)Math.Round(ux * lockMag);
            int deltaY = (int)Math.Round(uy * lockMag);

            virtRX = (byte)Math.Clamp(virtRX + deltaX, 0, 255);
            virtRY = (byte)Math.Clamp(virtRY + deltaY, 0, 255);
        }

        private void AdaptTracking()
        {
            if (_successfulFrames + _failedFrames > 30)
            {
                double successRate = _successfulFrames / (double)(_successfulFrames + _failedFrames);
                _adaptiveAlpha = 0.65 + (successRate * 0.2);
                _successfulFrames = 0;
                _failedFrames = 0;
            }

            _adaptiveJitter = (1.0 - _confidence) * QuantumJitterAmount;
        }

        private double GetAdaptiveAlpha()
        {
            return EnableNeuralAdaptation ? _adaptiveAlpha : (AcquisitionSpeed / 100.0);
        }

        private void UpdateFrameRateEstimate()
        {
            long now = Environment.TickCount64;
            double elapsedMs = now - _lastFrameTick;

            if (elapsedMs > 0)
            {
                double frameTimeMs = elapsedMs / 1.0;
                double detectedHz = 1000.0 / frameTimeMs;
                _frameRateDetected = 0.8 * _frameRateDetected + 0.2 * detectedHz;
            }

            _lastFrameTick = now;
        }

        private void RgbToHsv(int r, int g, int b, out double h, out double s, out double v)
        {
            double rf = r / 255.0;
            double gf = g / 255.0;
            double bf = b / 255.0;

            double max = Math.Max(rf, Math.Max(gf, bf));
            double min = Math.Min(rf, Math.Min(gf, bf));
            double delta = max - min;

            v = max;
            s = max < 1e-6 ? 0.0 : delta / max;

            if (delta < 1e-6)
            {
                h = 0;
            }
            else if (max == rf)
            {
                h = 60.0 * (((gf - bf) / delta) % 6.0);
            }
            else if (max == gf)
            {
                h = 60.0 * (((bf - rf) / delta) + 2.0);
            }
            else
            {
                h = 60.0 * (((rf - gf) / delta) + 4.0);
            }

            if (h < 0) h += 360.0;
        }

        private double HueDifference(double a, double b)
        {
            double diff = Math.Abs(a - b) % 360.0;
            return diff > 180.0 ? 360.0 - diff : diff;
        }

        private double GetTargetHue(int r, int g, int b)
        {
            RgbToHsv(r, g, b, out double h, out _, out _);
            return h;
        }

        private double GetHueTolerance(double hue)
        {
            return DetectionQuality switch
            {
                1 => 50.0,
                2 => 40.0,
                3 => 28.0,
                4 => 18.0,
                5 => 10.0,
                _ => 20.0
            };
        }

        private void GetQualityThresholds(int quality, out double satMin, out double valMin)
        {
            switch (quality)
            {
                case 1: satMin = 0.30; valMin = 0.20; break;
                case 2: satMin = 0.40; valMin = 0.30; break;
                case 3: satMin = 0.50; valMin = 0.40; break;
                case 4: satMin = 0.60; valMin = 0.50; break;
                case 5: satMin = 0.70; valMin = 0.60; break;
                default: satMin = 0.50; valMin = 0.40; break;
            }
        }

        private void RebuildSpiralIfNeeded(int radius)
        {
            if (radius == _cachedRadius) return;

            _cachedRadius = radius;
            int r2 = radius * radius;
            int capacity = radius * 2 * radius * 2;
            var temp = new (int dist2, short dx, short dy)[capacity];
            int count = 0;

            for (int dy = -radius + 1; dy < radius; dy++)
            {
                for (int dx = -radius + 1; dx < radius; dx++)
                {
                    int d2 = dx * dx + dy * dy;
                    if (d2 > r2) continue;
                    temp[count++] = (d2, (short)dx, (short)dy);
                }
            }

            Array.Sort(temp, 0, count,
                System.Collections.Generic.Comparer<(int, short, short)>.Create(
                    (a, b) => a.Item1.CompareTo(b.Item1)));

            _spiral = new (short, short)[count];
            _spiralLen = count;
            for (int i = 0; i < count; i++)
            {
                _spiral[i] = (temp[i].dx, temp[i].dy);
            }
        }

        public void PrintTelemetry()
        {
            if (_tickCount % 60 != 0) return;

            MacroBase.Print($"[UltimateColorAim v11] State={_state} | Confidence={_confidence:P0} | " +
                $"Scan={_avgScanMs:F2}ms | Pixels={_detectedPixelsAvg} | " +
                $"FPS={_frameRateDetected:F1} | Alpha={_adaptiveAlpha:F2}");
        }

        private void ResetAllState()
        {
            _state = LockState.Unlocked;
            _lockedX = 0;
            _lockedY = 0;
            _smoothedX = 0;
            _smoothedY = 0;
            _velocityX = 0;
            _velocityY = 0;
            _framesSinceStateChange = 0;
            _stateEnteredTick = Environment.TickCount64;
            _confidence = 0.0;
            _successfulFrames = 0;
            _failedFrames = 0;
        }

        public void Dispose()
        {
            _scanner?.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════════
    // PATTERN ENGINE
    // ════════════════════════════════════════════════════════════════════════════════

    internal sealed class PatternEngine
    {
        private int _tick;
        private double _snapProgress;
        private int _jitterIdx;

        private static readonly (sbyte x, sbyte y)[] JitterTable =
        { (1,0),(1,1),(0,1),(-1,1),(-1,0),(-1,-1),(0,-1),(1,-1) };

        private double[] _sinT = new double[128];
        private double[] _cosT = new double[128];
        private int _lastSteps = -1;

        private void BuildTrig(int steps)
        {
            if (steps == _lastSteps) return;
            _lastSteps = steps;
            if (steps > _sinT.Length) { _sinT = new double[steps]; _cosT = new double[steps]; }
            double inv = 6.283185307179586 / steps;
            for (int i = 0; i < steps; i++)
            { double a = inv * i; _sinT[i] = Math.Sin(a); _cosT[i] = Math.Cos(a); }
        }

        public void GetDelta(AimPatternMode mode, double amplitude, double extra,
                             out double dx, out double dy)
        {
            _tick++; dx = 0; dy = 0; const double C = 127.0;
            switch (mode)
            {
                case AimPatternMode.SpiralOut: { int s = 20; BuildTrig(s); double r = amplitude * (_tick % 60) / 60.0; dx = _sinT[_tick % s] * C * r; dy = _cosT[_tick % s] * C * r; break; }
                case AimPatternMode.SpiralIn: { int s = 20; BuildTrig(s); double r = amplitude * (1.0 - (_tick % 60) / 60.0); dx = _sinT[_tick % s] * C * r; dy = _cosT[_tick % s] * C * r; break; }
                case AimPatternMode.Pulse: { int s = 20; BuildTrig(s); double b = 0.5 + 0.5 * _sinT[(_tick * 2) % s]; dx = _sinT[_tick % s] * C * amplitude * b; dy = _cosT[_tick % s] * C * amplitude * b; break; }
                case AimPatternMode.RapidPulse: { int s = 10; BuildTrig(s); double b = (_tick & 1) == 0 ? amplitude : 0; dx = _sinT[_tick % s] * C * b; dy = _cosT[_tick % s] * C * b; break; }
                case AimPatternMode.MicroJitter: { var j = JitterTable[_jitterIdx++ % JitterTable.Length]; dx = j.x * amplitude * 8.0; dy = j.y * amplitude * 8.0; break; }
                case AimPatternMode.SnapAim: { _snapProgress = Math.Min(_snapProgress + 0.25, 1.0); dx = extra * _snapProgress * amplitude; break; }
                case AimPatternMode.PredictiveAim: { dx = extra * amplitude * 0.8; break; }
                case AimPatternMode.CircleShake2: { int s = (int)(extra > 0 ? extra : 20); BuildTrig(s); double m = 0.85 + 0.15 * _sinT[(_tick * 3) % s]; dx = _sinT[_tick % s] * C * amplitude * m; dy = _cosT[_tick % s] * C * amplitude * m; break; }
            }
        }

        public void Reset() { _tick = 0; _snapProgress = 0; _jitterIdx = 0; }
    }

    // ════════════════════════════════════════════════════════════════════════════════
    // PLUGIN LICENSE INFO
    // ════════════════════════════════════════════════════════════════════════════════

    internal static class PluginLicenseInfo
    {
        private const string SupabaseUrl = "https://djclpbsmshhbrbqlgsyi.supabase.co";
        private const string AnonKey = "sb_publishable_CDsFH4nM8SnuF4c_e9WCcg_LHUfLVSW";

        private static readonly string CachePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EasyAimImproved", "plugin_lic.dat");

        private static readonly System.Net.Http.HttpClient _http
            = new System.Net.Http.HttpClient();

        public static string GetHwid()
        {
            string raw = "";
            try
            {
                using (var regKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Cryptography"))
                    raw += regKey?.GetValue("MachineGuid")?.ToString() ?? "";
            }
            catch { }
            raw += Environment.MachineName + Environment.UserDomainName;
            if (string.IsNullOrEmpty(raw)) raw = Environment.MachineName + Environment.UserName;

            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(
                    System.Text.Encoding.UTF8.GetBytes(raw + "EasyAimHWID_v1"));
                return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16);
            }
        }

        private static byte[] MachineAesKey()
        {
            string raw = GetHwid() + "EasyAimAES_v1";
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
        }

        private static string Encrypt(string plain)
        {
            byte[] key = MachineAesKey();
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Key = key;
                aes.GenerateIV();
                using (var enc = aes.CreateEncryptor())
                {
                    byte[] data = System.Text.Encoding.UTF8.GetBytes(plain);
                    byte[] cipher = enc.TransformFinalBlock(data, 0, data.Length);
                    byte[] result = new byte[aes.IV.Length + cipher.Length];
                    Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
                    Buffer.BlockCopy(cipher, 0, result, aes.IV.Length, cipher.Length);
                    return Convert.ToBase64String(result);
                }
            }
        }

        private static string Decrypt(string base64)
        {
            byte[] key = MachineAesKey();
            byte[] data = Convert.FromBase64String(base64);
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Key = key;
                byte[] iv = new byte[aes.BlockSize / 8];
                byte[] cipher = new byte[data.Length - iv.Length];
                Buffer.BlockCopy(data, 0, iv, 0, iv.Length);
                Buffer.BlockCopy(data, iv.Length, cipher, 0, cipher.Length);
                aes.IV = iv;
                using (var dec = aes.CreateDecryptor())
                {
                    byte[] plain = dec.TransformFinalBlock(cipher, 0, cipher.Length);
                    return System.Text.Encoding.UTF8.GetString(plain);
                }
            }
        }

        public static void SaveKey(string key)
        {
            try
            {
                System.IO.Directory.CreateDirectory(
                    System.IO.Path.GetDirectoryName(CachePath)!);
                System.IO.File.WriteAllText(CachePath, Encrypt(key));
            }
            catch { }
        }

        public static string LoadKey()
        {
            try
            {
                if (!System.IO.File.Exists(CachePath)) return "";
                return Decrypt(System.IO.File.ReadAllText(CachePath));
            }
            catch { return ""; }
        }
    }
}
