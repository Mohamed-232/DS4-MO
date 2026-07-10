using System;
using System.Drawing;
using System.Runtime.InteropServices;
using CustomMacroBase;
using CustomMacroBase.Helper;

namespace CustomMacroPlugin0.GameList
{
    /// <summary>
    /// [PRODUCTION HARD-LOCK ENGINE v9.0]
    /// 
    /// Zero drift. Instant lock. Perfect retention. Lethal precision.
    /// 
    /// This is the engine that wins competitive shooters.
    /// Every design decision is made for MAXIMUM STICKINESS and ZERO LATENCY.
    /// </summary>
    internal sealed class HardLockEngine : IDisposable
    {
        // ════════════════════════════════════════════════════════════════════════
        // STATE MACHINE (4-State, Iron-Clad Logic)
        // ════════════════════════════════════════════════════════════════════════
        
        private enum LockState 
        { 
            Unlocked,      // No target detected, searching
            Acquiring,     // Target detected but not yet stable
            Locked,        // Target locked, actively tracking
            Coasting       // Target lost but still holding position
        }

        private LockState _state = LockState.Unlocked;
        private long _lastStateChangeTick = Environment.TickCount64;
        private int _stateFrameCounter = 0;

        // ════════════════════════════════════════════════════════════════════════
        // LOCK POSITION & HUE (Frozen once locked)
        // ════════════════════════════════════════════════════════════════════════
        
        private double _lockedX = 0, _lockedY = 0;      // Frozen position
        private double _lockedHue = 0;                  // Frozen color hue
        private double _smoothedX = 0, _smoothedY = 0;  // Exponential smoothing buffer
        
        // ════════════════════════════════════════════════════════════════════════
        // SCREEN SCANNER (SIMD-Optimized)
        // ════════════════════════════════════════════════════════════════════════
        
        private readonly ScreenScanner _scanner = new ScreenScanner();
        private (short dx, short dy)[] _spiral;         // Spiral coordinates (cached)
        private int _spiralLen = 0;
        private int _cachedRadius = -1;

        // ════════════════════════════════════════════════════════════════════════
        // PUBLIC CONFIGURATION
        // ════════════════════════════════════════════════════════════════════════
        
        public ColorAimActivation Activation { get; set; } = ColorAimActivation.ADSOnly;
        public int HexTargetR { get; set; } = 255;
        public int HexTargetG { get; set; } = 0;
        public int HexTargetB { get; set; } = 255;
        
        public double LockStrength { get; set; } = 100.0;          // 0-100: pull magnitude
        public double AcquisitionSpeed { get; set; } = 85.0;       // 0-100: tracking responsiveness
        public int DetectionQuality { get; set; } = 4;             // 1-5: sensitivity
        
        public double HorizClampFactor { get; set; } = 0.55;       // 0-1: horizontal outlier rejection
        public double VertBiasTopFraction { get; set; } = 0.45;    // 0-1: headshot bias
        public double DeadZonePx { get; set; } = 1.5;              // pixels: stop moving within this
        
        public double LockRetentionMs { get; set; } = 150.0;       // ms: coasting duration
        public double LockedHueTightness { get; set; } = 0.35;     // 0-1: locked hue tolerance multiplier
        
        // ════════════════════════════════════════════════════════════════════════
        // MAIN TICK FUNCTION
        // ════════════════════════════════════════════════════════════════════════
        
        public unsafe bool Tick(
            bool ads, bool fire, int radius,
            int customR, int customG, int customB,
            ref byte virtRX, ref byte virtRY)
        {
            // GATE: Check if we should even be active
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

            // ── PHASE 1: Screen Capture ──────────────────────────────────────
            var scr = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            int screenCenterX = scr.Width >> 1;
            int screenCenterY = scr.Height >> 1;
            int diameter = radius * 2;

            _scanner.EnsureSize(diameter);
            _scanner.Capture(screenCenterX, screenCenterY);

            // ── PHASE 2: Spiral Scan (Center-Outward) ─────────────────────────
            RebuildSpiralIfNeeded(radius);

            int detectedPixels = 0;
            double sumX = 0, sumY = 0;
            double targetHue = GetTargetHue(customR, customG, customB);
            double hueTolerance = GetHueTolerance(targetHue);

            var bmd = _scanner.Lock(out byte* basePtr, out int stride);
            try
            {
                // Fast spiral scan (center to edge)
                for (int i = 0; i < _spiralLen; i++)
                {
                    // Early exit if we have enough samples
                    if (detectedPixels >= 80) break;

                    short sdx = _spiral[i].dx;
                    short sdy = _spiral[i].dy;
                    int px = radius + sdx;
                    int py = radius + sdy;

                    byte* pixelPtr = basePtr + (py * stride) + (px << 2);  // BGRA
                    
                    // Extract RGB
                    byte r = pixelPtr[2];
                    byte g = pixelPtr[1];
                    byte b = pixelPtr[0];

                    // Convert to HSV for robust color matching
                    RgbToHsv(r, g, b, out double h, out double s, out double v);

                    // Quality-based thresholds
                    GetQualityThresholds(DetectionQuality, out double satMin, out double valMin);

                    // Hue match with locked-vs-acquiring tolerance
                    double hDiff = HueDifference(h, targetHue);
                    double effectiveTolerance = hueTolerance * 
                        (_state == LockState.Locked ? LockedHueTightness : 1.0);

                    // Color match check
                    if (hDiff <= effectiveTolerance && s >= satMin && v >= valMin)
                    {
                        sumX += px;
                        sumY += py;
                        detectedPixels++;
                    }
                }

                _scanner.Unlock(bmd);
            }
            catch { _scanner.Unlock(bmd); throw; }

            // ── PHASE 3: State Machine ──────────────────────────────────────
            
            if (detectedPixels >= 3)  // Minimum pixels for valid lock
            {
                double detectedX = (sumX / detectedPixels) - radius;
                double detectedY = (sumY / detectedPixels) - radius;
                
                UpdateStateMachine_TargetFound(detectedX, detectedY);
            }
            else
            {
                UpdateStateMachine_TargetLost();
            }

            // ── PHASE 4: Apply Aim ──────────────────────────────────────────
            
            if (_state == LockState.Locked || _state == LockState.Coasting)
            {
                ApplyLockAim(ref virtRX, ref virtRY);
                return true;
            }

            return false;
        }

        // ════════════════════════════════════════════════════════════════════════
        // STATE MACHINE LOGIC
        // ════════════════════════════════════════════════════════════════════════

        private void UpdateStateMachine_TargetFound(double detectedX, double detectedY)
        {
            long now = Environment.TickCount64;
            _stateFrameCounter++;

            switch (_state)
            {
                case LockState.Unlocked:
                    // First detection → start acquiring
                    _state = LockState.Acquiring;
                    _stateFrameCounter = 0;
                    _lastStateChangeTick = now;
                    _lockedX = detectedX;
                    _lockedY = detectedY;
                    break;

                case LockState.Acquiring:
                    // Verify stability for 2 frames before full lock
                    double delta = Math.Sqrt(
                        Math.Pow(detectedX - _lockedX, 2) + 
                        Math.Pow(detectedY - _lockedY, 2));

                    if (delta < 3.0 && _stateFrameCounter >= 2)
                    {
                        // LOCK ESTABLISHED
                        _state = LockState.Locked;
                        _lockedHue = GetTargetHue(HexTargetR, HexTargetG, HexTargetB);
                        _lockedX = detectedX;
                        _lockedY = detectedY;
                        _smoothedX = detectedX;
                        _smoothedY = detectedY;
                        _stateFrameCounter = 0;
                        _lastStateChangeTick = now;
                    }
                    else
                    {
                        // Update potential lock position
                        double alpha = 0.6;
                        _lockedX = alpha * detectedX + (1.0 - alpha) * _lockedX;
                        _lockedY = alpha * detectedY + (1.0 - alpha) * _lockedY;

                        // Timeout: if we can't acquire after 300ms, give up
                        if ((now - _lastStateChangeTick) > 300)
                        {
                            _state = LockState.Unlocked;
                            _stateFrameCounter = 0;
                        }
                    }
                    break;

                case LockState.Locked:
                    // Update lock position with smooth blend
                    double trackAlpha = AcquisitionSpeed / 100.0;  // 0.85 typical
                    _lockedX = trackAlpha * detectedX + (1.0 - trackAlpha) * _lockedX;
                    _lockedY = trackAlpha * detectedY + (1.0 - trackAlpha) * _lockedY;
                    _stateFrameCounter = 0;
                    _lastStateChangeTick = now;  // Reset coast timer
                    break;

                case LockState.Coasting:
                    // Re-acquire instantly if target reappears
                    _state = LockState.Locked;
                    _lockedX = detectedX;
                    _lockedY = detectedY;
                    _stateFrameCounter = 0;
                    _lastStateChangeTick = now;
                    break;
            }
        }

        private void UpdateStateMachine_TargetLost()
        {
            long now = Environment.TickCount64;

            switch (_state)
            {
                case LockState.Acquiring:
                    // Lost target while acquiring → back to unlocked
                    _state = LockState.Unlocked;
                    _stateFrameCounter = 0;
                    break;

                case LockState.Locked:
                    // Start coasting (hold position without update)
                    _state = LockState.Coasting;
                    _lastStateChangeTick = now;
                    _stateFrameCounter = 0;
                    break;

                case LockState.Coasting:
                    // Check if coast timeout exceeded
                    if ((now - _lastStateChangeTick) > (long)LockRetentionMs)
                    {
                        _state = LockState.Unlocked;
                        _stateFrameCounter = 0;
                    }
                    break;

                case LockState.Unlocked:
                    // Already unlocked, nothing to do
                    break;
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // AIM APPLICATION (Smooth, Lethal)
        // ════════════════════════════════════════════════════════════════════════

        private void ApplyLockAim(ref byte virtRX, ref byte virtRY)
        {
            double dist = Math.Sqrt(_lockedX * _lockedX + _lockedY * _lockedY);

            // If already on target, don't move
            if (dist < DeadZonePx) return;

            // Normalize direction
            double ux = _lockedX / dist;
            double uy = _lockedY / dist;

            // Apply strength (0-100% → 0-127 analog range)
            double lockMagnitude = (LockStrength / 100.0) * 127.0;
            int deltaX = (int)Math.Round(ux * lockMagnitude);
            int deltaY = (int)Math.Round(uy * lockMagnitude);

            // Clamp to stick range
            virtRX = (byte)Math.Clamp(virtRX + deltaX, 0, 255);
            virtRY = (byte)Math.Clamp(virtRY + deltaY, 0, 255);
        }

        // ════════════════════════════════════════════════════════════════════════
        // COLOR MATCHING UTILITIES
        // ════════════════════════════════════════════════════════════════════════

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

        // ════════════════════════════════════════════════════════════════════════
        // SPIRAL CACHING (Performance Optimization)
        // ════════════════════════════════════════════════════════════════════════

        private void RebuildSpiralIfNeeded(int radius)
        {
            if (radius == _cachedRadius) return;

            _cachedRadius = radius;
            int r2 = radius * radius;
            int capacity = radius * 2 * radius * 2;
            var temp = new (int dist2, short dx, short dy)[capacity];
            int count = 0;

            // Generate all circle pixels
            for (int dy = -radius + 1; dy < radius; dy++)
            {
                for (int dx = -radius + 1; dx < radius; dx++)
                {
                    int d2 = dx * dx + dy * dy;
                    if (d2 > r2) continue;
                    temp[count++] = (d2, (short)dx, (short)dy);
                }
            }

            // Sort by distance (center-outward)
            Array.Sort(temp, 0, count,
                System.Collections.Generic.Comparer<(int, short, short)>.Create(
                    (a, b) => a.Item1.CompareTo(b.Item1)));

            // Copy to final array
            _spiral = new (short, short)[count];
            _spiralLen = count;
            for (int i = 0; i < count; i++)
            {
                _spiral[i] = (temp[i].dx, temp[i].dy);
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // STATE RESET
        // ════════════════════════════════════════════════════════════════════════

        private void ResetAllState()
        {
            _state = LockState.Unlocked;
            _lockedX = 0;
            _lockedY = 0;
            _smoothedX = 0;
            _smoothedY = 0;
            _stateFrameCounter = 0;
            _lastStateChangeTick = Environment.TickCount64;
        }

        public void Dispose()
        {
            _scanner?.Dispose();
        }
    }
}
