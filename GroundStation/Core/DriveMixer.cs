using System;

namespace GroundStation.Core;

public static class DriveMixer
{
    /// <summary>
    /// Applies deadband and selectable curve shaping (Gamma power curve or Cubic polynomial) to raw [-1.0, 1.0] axis.
    /// </summary>
    public static double ShapeAxis(double raw, double deadband, double param, CurveType curveType = CurveType.GammaPower)
    {
        if (Math.Abs(raw) <= deadband)
        {
            return 0.0;
        }

        double sign = Math.Sign(raw);
        double scaled = (Math.Abs(raw) - deadband) / (1.0 - deadband);
        scaled = Math.Clamp(scaled, 0.0, 1.0);

        if (curveType == CurveType.GammaPower)
        {
            // Gamma Power Law: y = scaled^gamma (gamma >= 1.0)
            double gamma = Math.Max(1.0, param);
            return sign * Math.Pow(scaled, gamma);
        }
        else
        {
            // Blend linear with cubic polynomial: f(x) = (1 - expo)*x + expo*x^3
            double expo = Math.Clamp(param, 0.0, 1.0);
            return sign * ((1.0 - expo) * scaled + expo * Math.Pow(scaled, 3));
        }
    }

    /// <summary>
    /// Computes left and right thruster pulse widths in microseconds (1000 - 2000 us, 1500 neutral).
    /// Supports Single-Stick Arcade, Split-Stick Arcade, and Dual-Stick Tank drive modes.
    /// In Tank mode, Left Stick Y directly controls Left Thruster and Right Stick Y directly controls Right Thruster.
    /// </summary>
    public static (short leftUs, short rightUs, double shapedThrottle, double shapedSteer) ComputeDriveThrust(
        double leftX,
        double leftY,
        double rightX,
        double rightY,
        TuningConfig config)
    {
        double minReverse = -Math.Abs(config.MaxReverse);
        double maxForward = Math.Abs(config.MaxForward);

        if (config.Mode == DriveMode.DualStickTank)
        {
            // Tank Drive: Left Stick Y -> Left Thruster, Right Stick Y -> Right Thruster
            // Left Thruster shaped by Curve 1 (Throttle parameters)
            // Right Thruster shaped by Curve 2 (Steering parameters)
            double leftShaped = ShapeAxis(leftY, config.ThrottleDeadband, config.ThrottleCurveParam, config.Curve);
            double rightShaped = ShapeAxis(rightY, config.SteerDeadband, config.SteerCurveParam, config.Curve);

            double leftClamped = Math.Clamp(leftShaped, minReverse, maxForward);
            double rightClamped = Math.Clamp(rightShaped, minReverse, maxForward);

            short leftUs = (short)Math.Round(1500.0 + (leftClamped * 500.0));
            short rightUs = (short)Math.Round(1500.0 + (rightClamped * 500.0));

            leftUs = Math.Clamp(leftUs, (short)1000, (short)2000);
            rightUs = Math.Clamp(rightUs, (short)1000, (short)2000);

            return (leftUs, rightUs, leftShaped, rightShaped);
        }
        else
        {
            // Arcade Drive
            double throttleRaw = leftY;
            double steerRaw = config.Mode == DriveMode.SingleStickArcade ? leftX : rightX;

            double throttle = ShapeAxis(throttleRaw, config.ThrottleDeadband, config.ThrottleCurveParam, config.Curve);
            double steer = ShapeAxis(steerRaw, config.SteerDeadband, config.SteerCurveParam, config.Curve);

            double leftNorm = throttle + steer;
            double rightNorm = throttle - steer;

            leftNorm = Math.Clamp(leftNorm, minReverse, maxForward);
            rightNorm = Math.Clamp(rightNorm, minReverse, maxForward);

            short leftUs = (short)Math.Round(1500.0 + (leftNorm * 500.0));
            short rightUs = (short)Math.Round(1500.0 + (rightNorm * 500.0));

            leftUs = Math.Clamp(leftUs, (short)1000, (short)2000);
            rightUs = Math.Clamp(rightUs, (short)1000, (short)2000);

            return (leftUs, rightUs, throttle, steer);
        }
    }
}
