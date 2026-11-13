namespace GroundStation.Core;

public enum DriveMode
{
    SingleStickArcade, // Left Stick X (Steer) + Left Stick Y (Throttle)
    SplitStickArcade,  // Right Stick X (Steer) + Left Stick Y (Throttle)
    DualStickTank      // Left Stick Y (Left Thrust) + Right Stick Y (Right Thrust)
}

public enum CurveType
{
    GammaPower,       // f(x) = x^gamma
    CubicPolynomial   // f(x) = (1 - a)x + a*x^3
}

/// <summary>
/// Immutable snapshot of live-tunable vehicle parameters.
/// Features independent tuning for vertical (throttle) and horizontal (steering) axes (or Left and Right thruster axes in Tank mode),
/// forward and reverse power limits, selectable drive modes, and curve interpolation engines.
/// Swapped atomically using reference replacement ("pushpin" pattern).
/// </summary>
public record TuningConfig(
    double ThrottleDeadband = 0.08,
    double ThrottleCurveParam = 1.90, // Gamma (default 1.90) or Expo (0.60)
    double SteerDeadband = 0.08,
    double SteerCurveParam = 1.90,    // Gamma (default 1.90) or Expo (0.60)
    double MaxForward = 1.00,
    double MaxReverse = 0.75,
    int SlewRateUsPerCycle = 50,
    DriveMode Mode = DriveMode.SingleStickArcade,
    CurveType Curve = CurveType.GammaPower
);
