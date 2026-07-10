# 🎯 ColorAim v8 - ULTIMATE HARD-LOCK REDESIGN PROMPT

## MISSION CRITICAL

You are redesigning the ColorAimEngine in a C# gamepad macro controller. The current system is weak—it drifts, loses targets, and has soft/mushy aiming. **Your job is to make it 10 billion times better.**

## WHAT THE USER WANTS (NOT NEGOTIABLE)

1. **HARD LOCK**: The moment a target pixel appears matching the color, LOCK ONTO IT INSTANTLY with ZERO hesitation
2. **NO DRIFT**: Once locked, aim stays centered on that color PERFECTLY—no left/right wandering, no creep
3. **PERMANENT HOLD**: Keep aiming at locked target even if target briefly disappears (frame drops, occlusion)
4. **INSTANT RELEASE**: The INSTANT the color is 100% gone, unlock and stop aiming
5. **LETHAL PRECISION**: This is for competitive shooters. Every pixel counts. Sub-millisecond response, pixel-perfect accuracy

## CURRENT PROBLEMS TO ELIMINATE

- ❌ Centroid bleeding (target drifts left/right along edges)
- ❌ Soft tracking that feels mushy
- ❌ Loss of lock on flicker/frame drops
- ❌ Over-smoothing that creates lag
- ❌ Complex weighted calculations that add latency
- ❌ Bounding-box logic that complicates the lock
- ❌ "Coast frames" concept that delays release

## DESIGN REQUIREMENTS

### 1. ACQUISITION (Find Target)
- Spiral scan from center outward (cached, sorted by distance)
- Match: `HueDiff(pixelHue, targetHue) <= tolerance` AND `saturation >= minSat` AND `value >= minVal`
- **FAST SCAN**: Only check pixels, don't blend or weight yet
- **Collect ALL matching pixels** into accumulator (sum X, sum Y, count)
- No complex centroid logic—just the raw average of all match positions

### 2. LOCK STATE MACHINE (4 states, switch instantly)

```
UNLOCKED (idle)
  ↓
  IF (hitCount > 3) → ACQUIRING
  
ACQUIRING (stabilizing)
  ↓
  IF (position stable ±3px for 2 frames) → LOCKED
  IF (timeout 300ms OR hitCount == 0) → UNLOCKED
  
LOCKED (tracking target)
  ↓
  IF (hitCount > 3) → keep tracking
  IF (hitCount == 0 for 150ms) → COASTING
  
COASTING (weathering flicker)
  ↓
  IF (hitCount > 3) → LOCKED (instant re-lock)
  IF (timeout 150ms) → UNLOCKED
```

### 3. LOCK APPLICATION (Aim at Target)

**Position Smoothing:**
- Use exponential moving average: `smooth = alpha * new + (1 - alpha) * old`
- Alpha = 0.85 (very fast, minimal lag, responsive aiming)
- Apply ONLY when tracking (LOCKED or COASTING states)

**Direction to Aim:**
```csharp
direction = normalize(smoothedLockedPos - screenCenter)
strength = (LockStrength / 100) * 127  // 0-127 analog range
virtualStick += direction * strength
```

**Dead Zone:**
- If `distance < 1.5px` → don't move (you're already on target)

### 4. COLOR TUNING - HYSTERESIS SYSTEM

**Acquisition (Easy to Lock):**
- Use `tolerance = autoTol(DetectionQuality)` — wide net

**Locked (Hard to Lose):**
- Switch to `tolerance *= 0.35` — needle vision
- This creates hysteresis: **easy to grab, hard to lose**

**DetectionQuality Mapping:**
```
Quality 1: Hue ±50°, Sat ≥ 0.30, Val ≥ 0.20  [loose grab]
Quality 2: Hue ±40°, Sat ≥ 0.40, Val ≥ 0.30
Quality 3: Hue ±28°, Sat ≥ 0.50, Val ≥ 0.40  [balanced]
Quality 4: Hue ±18°, Sat ≥ 0.60, Val ≥ 0.50
Quality 5: Hue ±10°, Sat ≥ 0.70, Val ≥ 0.60  [laser precision]
```

### 5. PERFORMANCE - NO FRAME DROPS

- **Spiral cache**: Pre-compute all circle pixels once, sort by distance, reuse
- **Early exit**: Stop scanning after 50+ pixels (you have your lock)
- **Locked hue cache**: Save once at ACQUIRING→LOCKED transition, never recalculate
- **No bounding boxes**: Delete all bbMinX/bbMaxX/bbMinY/bbMaxY logic
- **No weighted averages**: Just sum positions and divide by count
- **Fixed 8ms tick**: Consistent timing, predictable behavior

### 6. PUBLIC API (SIMPLIFIED & CLEAR)

```csharp
public ColorAimActivation Activation { get; set; } = ColorAimActivation.ADSOnly;
public ColorAimPreset Preset { get; set; } = ColorAimPreset.HexInput;
public int HexTargetR { get; set; } = 255;
public int HexTargetG { get; set; } = 0;
public int HexTargetB { get; set; } = 255;

public double LockStrength { get; set; } = 100.0;           // 0-100: % of max pull [DEFAULT 100]
public double AcquisitionSpeed { get; set; } = 85.0;        // 0-100: frame smoothing [DEFAULT 85]
public int DetectionQuality { get; set; } = 4;              // 1-5: sensitivity [DEFAULT 4]

// Advanced tuning
public double LockedHueTightness { get; set; } = 0.35;      // multiply tolerance when locked
public double SmoothingAlpha { get; set; } = 0.85;          // position tracking alpha (0-1)
public double LockRetentionMs { get; set; } = 150.0;        // coast duration
public double DeadZonePx { get; set; } = 1.5;               // stop aiming within this distance
public int MinPixelsForLock { get; set; } = 3;              // minimum matching pixels to acquire
```

### 7. INTERNAL STATE (Telemetry)

```csharp
private enum LockState { Unlocked, Acquiring, Locked, Coasting }
private LockState _currentState = LockState.Unlocked;
private int _framesSinceStateChange = 0;
private long _lastStateChangeTick = 0;
private double _lockedX = 0, _lockedY = 0;
private double _lockedHue = 0;  // frozen hue once locked
private int _lastPixelCount = 0;
```

### 8. PSEUDOCODE STRUCTURE

```csharp
public unsafe bool Tick(bool ads, bool fire, int radius, int customR, int customG, int customB,
                        ref byte virtRX, ref byte virtRY)
{
    // 1. GATE: Check activation condition
    if (!IsActivatedByGate(ads, fire)) {
        ResetAllLockState();
        return false;
    }

    // 2. SCAN: Find all matching pixels
    int hitCount = ScanScreenForMatches(radius, out double detectedX, out double detectedY);
    _lastPixelCount = hitCount;

    // 3. STATE MACHINE: Update lock state
    UpdateLockStateMachine(hitCount, detectedX, detectedY);

    // 4. AIM: If in any active state, apply aim correction
    if (_currentState != LockState.Unlocked) {
        ApplyLockAim(ref virtRX, ref virtRY);
        return true;
    }

    return false;
}

// ─────────────────────────────────────────────────────────────

private int ScanScreenForMatches(int radius, out double avgX, out double avgY)
{
    RebuildSpiralIfNeeded(radius);
    CaptureScreenROI(radius);
    
    int sumX = 0, sumY = 0, hitCount = 0;
    double targetHue = _currentState == LockState.Locked 
        ? _lockedHue 
        : GetTargetHueFromSliders();
    double tolerance = _currentState == LockState.Locked
        ? GetAcquisitionTolerance() * LockedHueTightness
        : GetAcquisitionTolerance();

    // Fast spiral scan (center → outward)
    for (int i = 0; i < _spiralLen; i++) {
        if (hitCount >= 50) break;  // Early exit
        
        int px = centerPx + _spiral[i].dx;
        int py = centerPy + _spiral[i].dy;
        byte* pixelPtr = GetPixelPointer(px, py);
        
        RgbToHsv(pixelPtr[2], pixelPtr[1], pixelPtr[0], out double h, out double s, out double v);
        
        if (HueDiff(h, targetHue) <= tolerance && s >= satMin && v >= valMin) {
            sumX += px;
            sumY += py;
            hitCount++;
        }
    }

    if (hitCount > 0) {
        avgX = (sumX / (double)hitCount) - radius;
        avgY = (sumY / (double)hitCount) - radius;
    } else {
        avgX = avgY = 0;
    }

    return hitCount;
}

// ─────────────────────────────────────────────────────────────

private void UpdateLockStateMachine(int hitCount, double detX, double detY)
{
    long now = Environment.TickCount64;
    _framesSinceStateChange++;

    switch (_currentState) {
        case LockState.Unlocked:
            if (hitCount >= MinPixelsForLock) {
                _currentState = LockState.Acquiring;
                _framesSinceStateChange = 0;
                _lastStateChangeTick = now;
            }
            break;

        case LockState.Acquiring:
            if (IsPositionStable() && _framesSinceStateChange >= 2) {
                _currentState = LockState.Locked;
                _lockedX = detX;
                _lockedY = detY;
                _lockedHue = GetTargetHueFromSliders();  // FREEZE hue
                _framesSinceStateChange = 0;
                _lastStateChangeTick = now;
            } else if (hitCount == 0 || (now - _lastStateChangeTick) > 300) {
                _currentState = LockState.Unlocked;
                _framesSinceStateChange = 0;
            }
            break;

        case LockState.Locked:
            if (hitCount >= MinPixelsForLock) {
                // Smooth update toward detected position
                double alpha = AcquisitionSpeed / 100.0;
                _lockedX = alpha * detX + (1.0 - alpha) * _lockedX;
                _lockedY = alpha * detY + (1.0 - alpha) * _lockedY;
                _framesSinceStateChange = 0;
                _lastStateChangeTick = now;
            } else if ((now - _lastStateChangeTick) > (long)LockRetentionMs) {
                _currentState = LockState.Coasting;
                _framesSinceStateChange = 0;
                _lastStateChangeTick = now;
            }
            break;

        case LockState.Coasting:
            if (hitCount >= MinPixelsForLock) {
                // Instant re-lock
                _currentState = LockState.Locked;
                _lockedX = detX;
                _lockedY = detY;
                _framesSinceStateChange = 0;
                _lastStateChangeTick = now;
            } else if ((now - _lastStateChangeTick) > (long)LockRetentionMs) {
                _currentState = LockState.Unlocked;
                _framesSinceStateChange = 0;
            }
            break;
    }
}

// ─────────────────────────────────────────────────────────────

private void ApplyLockAim(ref byte virtRX, ref byte virtRY)
{
    // Smooth the locked position
    double smoothAlpha = SmoothingAlpha;
    _lockedX = smoothAlpha * _lockedX + (1.0 - smoothAlpha) * _lockedX;  // typically just stays same
    _lockedY = smoothAlpha * _lockedY + (1.0 - smoothAlpha) * _lockedY;

    double dist = Math.Sqrt(_lockedX * _lockedX + _lockedY * _lockedY);
    if (dist < DeadZonePx) return;  // Already on target

    // Normalize and apply strength
    double ux = _lockedX / dist;
    double uy = _lockedY / dist;
    double lockMag = (LockStrength / 100.0) * 127.0;

    int dX = (int)Math.Round(ux * lockMag);
    int dY = (int)Math.Round(uy * lockMag);

    virtRX = (byte)Math.Clamp(virtRX + dX, 0, 255);
    virtRY = (byte)Math.Clamp(virtRY + dY, 0, 255);
}
```

## DEFAULT UI VALUES

```csharp
private double _lockStrength = 100.0;           // LETHAL: 100% pull
private double _acqSpeed = 85.0;                // Fast, responsive
private int _detQuality = 4;                    // Precise (not loose)
private double _smoothAlpha = 0.85;             // Snappy aiming
private double _lockRetention = 150.0;          // 150ms flicker tolerance
private int _minPixels = 3;                     // need 3+ pixels to acquire
```

## WHAT CHANGES FROM v7

| Aspect | Old | New |
|--------|-----|-----|
| **Tracking** | Soft, mushy | Hard lock, snappy |
| **Drift** | Bleeds along edges | Perfectly centered |
| **Flicker handling** | Loses lock | Coasts through it |
| **Hue tolerance** | Static | Hysteresis (tight when locked) |
| **Logic** | Weighted centroid hell | Clean state machine |
| **Latency** | 50-80ms | <8ms |
| **Default strength** | 38% | **100%** |
| **Default quality** | 3 | **4** |

## SUCCESS METRICS (100% Must-Have)

✅ Lock within **1 frame** (8ms) of color appearing  
✅ Hold lock **perfectly still** (no drift, no creep)  
✅ Release **instantly** when color disappears (within coast window)  
✅ Aiming feels **snappy and responsive**  
✅ No visible pixel bleeding or wandering  
✅ Works at **60/120/144/240 Hz** without tuning  
✅ **Zero frame drops** from scanning  
✅ **No mushy feel** — lethal precision  

## NON-NEGOTIABLE CONSTRAINTS

1. ✋ **Hard lock behavior is religion** — no soft tracking compromises
2. ✋ **Pixel-perfect** — the lock spot is exactly on target pixels
3. ✋ **Sub-8ms** — faster than a frame at 120Hz
4. ✋ **State machine clarity** — no spaghetti logic, clean switch statements
5. ✋ **Zero latency** — every millisecond counts in competitive play

## DELIVER

- ✅ Complete rewritten `ColorAimEngine` class
- ✅ All helper methods (scan, smooth, aim)
- ✅ State machine with clear comments
- ✅ Spiral caching system (optimized)
- ✅ Inline documentation explaining lock logic
- ✅ Ready to drop into the code with zero refactoring
