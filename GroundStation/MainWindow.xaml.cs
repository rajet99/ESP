using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using GroundStation.Core;
using ScottPlot;
using MediaColor = System.Windows.Media.Color;
using MediaBrush = System.Windows.Media.SolidColorBrush;

namespace GroundStation;

public partial class MainWindow : Window
{
    private volatile TuningConfig _currentTuning = new(0.08, 1.90, 0.08, 1.90, 1.00, 0.75, 50, DriveMode.SingleStickArcade, CurveType.GammaPower);
    private readonly SerialTransport _serial = new();
    private HighResolutionTimer? _timer;

    private ushort _sequenceNumber;
    private long _packetsSent;
    private long _fpsCounter;
    private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();

    private volatile bool _emergencyStop;
    private int _uiThrottleDivider;

    // Telemetry display cache
    private double _rawThrottle;
    private double _rawSteer;
    private double _rawLeftX;
    private double _rawLeftY;
    private double _rawRightX;
    private double _rawRightY;
    private double _shapedThrottle;
    private double _shapedSteer;
    private short _lastLeftUs = 1500;
    private short _lastRightUs = 1500;
    private short _lastRollerPwm;
    private byte _lastArmAction;
    private bool _controllerConnected;

    // ScottPlot 5 backing arrays
    private readonly double[] _curveXs = new double[61];
    private readonly double[] _throttleCurveYs = new double[61];
    private readonly double[] _steerCurveYs = new double[61];

    private readonly double[] _leftStickDbXs = new double[5];
    private readonly double[] _leftStickDbYs = new double[5];
    private readonly double[] _rightStickDbXs = new double[5];
    private readonly double[] _rightStickDbYs = new double[5];

    private ScottPlot.Plottables.Scatter? _throttleCurveScatter;
    private ScottPlot.Plottables.Scatter? _steerCurveScatter;
    private ScottPlot.Plottables.Scatter? _leftStickDeadbandBox;
    private ScottPlot.Plottables.Scatter? _rightStickDeadbandBox;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshComPorts();
        UpdateSliderLabels();

        // 1. Initialize curve X coordinates [-1.0 .. 1.0]
        for (int i = 0; i <= 60; i++)
        {
            _curveXs[i] = -1.0 + (i / 60.0) * 2.0;
        }

        // 2. Setup ScottPlot 5 Scopes
        InitScopePlot(ThrottlePlot, _curveXs, _throttleCurveYs, out _throttleCurveScatter);
        InitScopePlot(SteerPlot, _curveXs, _steerCurveYs, out _steerCurveScatter);
        InitStickPlot(LeftStickPlot, _leftStickDbXs, _leftStickDbYs, out _leftStickDeadbandBox);
        InitStickPlot(RightStickPlot, _rightStickDbXs, _rightStickDbYs, out _rightStickDeadbandBox);

        UpdateCurveData();

        // Start 50 Hz high-resolution loop
        _timer = new HighResolutionTimer(20.0, On50HzTick);
        _timer.Start();

        // 1-second timer for FPS calculation
        var fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        fpsTimer.Tick += (s, ev) =>
        {
            double seconds = _fpsStopwatch.Elapsed.TotalSeconds;
            double fps = _fpsCounter / (seconds > 0 ? seconds : 1.0);
            _fpsCounter = 0;
            _fpsStopwatch.Restart();
            FpsLabel.Text = $"{fps:F1} Hz | {_packetsSent} pkts";
        };
        fpsTimer.Start();
    }

    private static void InitScopePlot(
        ScottPlot.WPF.WpfPlot wpfPlot,
        double[] xs,
        double[] ys,
        out ScottPlot.Plottables.Scatter curveScatter)
    {
        var plot = wpfPlot.Plot;
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#131418");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#131418");
        plot.Axes.Color(ScottPlot.Color.FromHex("#8A91A0"));

        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#22252D");
        plot.Grid.MajorLineWidth = 1.0f;

        // Exact [-1.0, 1.0] limits with zero boundary margin
        plot.Axes.SetLimits(-1.0, 1.0, -1.0, 1.0);

        // Minimalist manual ticks (-1.0, 0.0, 1.0)
        var tickGen = new ScottPlot.TickGenerators.NumericManual();
        tickGen.AddMajor(-1.0, "-1.0");
        tickGen.AddMajor(0.0, "0.0");
        tickGen.AddMajor(1.0, "1.0");
        plot.Axes.Bottom.TickGenerator = tickGen;
        plot.Axes.Left.TickGenerator = tickGen;

        // Thin yellow curve (User requirement: Yellow line)
        curveScatter = plot.Add.Scatter(xs, ys);
        curveScatter.Color = ScottPlot.Color.FromHex("#FACC15");
        curveScatter.LineWidth = 1.5f;
        curveScatter.MarkerSize = 0;

        wpfPlot.Interaction.Disable();
        wpfPlot.Refresh();
    }

    private void InitStickPlot(
        ScottPlot.WPF.WpfPlot wpfPlot,
        double[] dbXs,
        double[] dbYs,
        out ScottPlot.Plottables.Scatter deadbandBox)
    {
        var plot = wpfPlot.Plot;
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#131418");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#131418");
        plot.Axes.Color(ScottPlot.Color.FromHex("#8A91A0"));

        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#22252D");
        plot.Grid.MajorLineWidth = 1.0f;

        plot.Axes.SetLimits(-1.0, 1.0, -1.0, 1.0);

        // Center crosshairs
        var hLine = plot.Add.HorizontalLine(0);
        hLine.Color = ScottPlot.Color.FromHex("#3A3F4C");
        hLine.LineWidth = 1.0f;

        var vLine = plot.Add.VerticalLine(0);
        vLine.Color = ScottPlot.Color.FromHex("#3A3F4C");
        vLine.LineWidth = 1.0f;

        // Outer limit circle (sampled)
        const int circlePts = 40;
        double[] cXs = new double[circlePts + 1];
        double[] cYs = new double[circlePts + 1];
        for (int i = 0; i <= circlePts; i++)
        {
            double angle = (i / (double)circlePts) * 2.0 * Math.PI;
            cXs[i] = Math.Cos(angle);
            cYs[i] = Math.Sin(angle);
        }
        var boundaryCircle = plot.Add.Scatter(cXs, cYs);
        boundaryCircle.Color = ScottPlot.Color.FromHex("#262A33");
        boundaryCircle.LineWidth = 1.0f;
        boundaryCircle.MarkerSize = 0;

        deadbandBox = plot.Add.Scatter(dbXs, dbYs);
        deadbandBox.Color = ScottPlot.Color.FromHex("#4A5060");
        deadbandBox.LineWidth = 1.0f;
        deadbandBox.MarkerSize = 0;

        // Hide axis ticks on stick pad for ultra-clean look
        var emptyTicks = new ScottPlot.TickGenerators.NumericManual();
        plot.Axes.Bottom.TickGenerator = emptyTicks;
        plot.Axes.Left.TickGenerator = emptyTicks;

        wpfPlot.Interaction.Disable();
        wpfPlot.Refresh();
    }

    /// <summary>
    /// Converts data coordinates to overlay-canvas DIP positions.
    /// ScottPlot's GetPixel returns device pixels (bitmap space, scaled by the
    /// monitor's DPI factor), while WPF Canvas positioning uses DIPs, so the
    /// result must be divided by the DPI scale or dots drift at 125%/150% scaling.
    /// </summary>
    private static System.Windows.Point DataToCanvasDip(ScottPlot.WPF.WpfPlot plot, double x, double y)
    {
        var px = plot.Plot.GetPixel(new Coordinates(x, y));
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(plot);
        return new System.Windows.Point(px.X / dpi.DpiScaleX, px.Y / dpi.DpiScaleY);
    }

    private void UpdateCurveData()
    {
        var tuning = _currentTuning;

        for (int i = 0; i <= 60; i++)
        {
            double x = _curveXs[i];
            _throttleCurveYs[i] = DriveMixer.ShapeAxis(x, tuning.ThrottleDeadband, tuning.ThrottleCurveParam, tuning.Curve);
            _steerCurveYs[i] = DriveMixer.ShapeAxis(x, tuning.SteerDeadband, tuning.SteerCurveParam, tuning.Curve);
        }

        ThrottlePlot.Refresh();
        SteerPlot.Refresh();

        if (_leftStickDeadbandBox != null)
        {
            double leftDbX = tuning.Mode == DriveMode.SingleStickArcade ? tuning.SteerDeadband : 0.04;
            double leftDbY = tuning.ThrottleDeadband;
            _leftStickDbXs[0] = -leftDbX; _leftStickDbYs[0] = -leftDbY;
            _leftStickDbXs[1] = leftDbX;  _leftStickDbYs[1] = -leftDbY;
            _leftStickDbXs[2] = leftDbX;  _leftStickDbYs[2] = leftDbY;
            _leftStickDbXs[3] = -leftDbX; _leftStickDbYs[3] = leftDbY;
            _leftStickDbXs[4] = -leftDbX; _leftStickDbYs[4] = -leftDbY;
            LeftStickPlot.Refresh();
        }

        if (_rightStickDeadbandBox != null)
        {
            double rightDbX = tuning.Mode == DriveMode.SplitStickArcade ? tuning.SteerDeadband : 0.04;
            double rightDbY = tuning.Mode == DriveMode.DualStickTank ? tuning.SteerDeadband : 0.04;
            _rightStickDbXs[0] = -rightDbX; _rightStickDbYs[0] = -rightDbY;
            _rightStickDbXs[1] = rightDbX;  _rightStickDbYs[1] = -rightDbY;
            _rightStickDbXs[2] = rightDbX;  _rightStickDbYs[2] = rightDbY;
            _rightStickDbXs[3] = -rightDbX; _rightStickDbYs[3] = rightDbY;
            _rightStickDbXs[4] = -rightDbX; _rightStickDbYs[4] = -rightDbY;
            RightStickPlot.Refresh();
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer?.Dispose();
        _serial.Dispose();
    }

    private void RefreshComPorts()
    {
        PortComboBox.Items.Clear();
        var ports = SerialTransport.GetAvailablePorts();
        foreach (var port in ports)
        {
            PortComboBox.Items.Add(port);
        }
        if (PortComboBox.Items.Count > 0)
        {
            PortComboBox.SelectedIndex = 0;
        }
    }

    private void RefreshPortsBtn_Click(object sender, RoutedEventArgs e) => RefreshComPorts();

    private void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_serial.IsOpen)
        {
            _serial.Close();
            UpdateConnectionBadge(false);
            ConnectBtn.Content = "CONNECT (921600)";
        }
        else
        {
            if (PortComboBox.SelectedItem is string portName)
            {
                if (_serial.Open(portName, 921600))
                {
                    UpdateConnectionBadge(true);
                    ConnectBtn.Content = "DISCONNECT";
                }
                else
                {
                    MessageBox.Show($"Could not open {portName} at 921,600 baud.\nEnsure the device is not opened by another program.",
                        "Serial Connection Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                MessageBox.Show("Please select a COM port first.", "No Port Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private void UpdateConnectionBadge(bool connected)
    {
        if (connected)
        {
            StatusBadge.BorderBrush = new MediaBrush(MediaColor.FromRgb(0x22, 0xC5, 0x5E));
            StatusText.Text = $"ONLINE: {_serial.CurrentPortName}";
            StatusText.Foreground = new MediaBrush(MediaColor.FromRgb(0x22, 0xC5, 0x5E));
        }
        else
        {
            StatusBadge.BorderBrush = new MediaBrush(MediaColor.FromRgb(0xDC, 0x26, 0x26));
            StatusText.Text = "LINK OFFLINE";
            StatusText.Foreground = new MediaBrush(MediaColor.FromRgb(0xDC, 0x26, 0x26));
        }
    }

    private void EStopBtn_Click(object sender, RoutedEventArgs e)
    {
        _emergencyStop = !_emergencyStop;
        if (_emergencyStop)
        {
            EStopText.Text = "E-STOP ACTIVE";
            EStopBtn.Background = new MediaBrush(MediaColor.FromRgb(0x7F, 0x1D, 0x1D));
            EStopIcon.Fill = new MediaBrush(MediaColor.FromRgb(0xFA, 0xCC, 0x15)); // Accent yellow octagon
        }
        else
        {
            EStopText.Text = "EMERGENCY STOP";
            EStopBtn.Background = new MediaBrush(MediaColor.FromRgb(0xB9, 0x1C, 0x1C));
            EStopIcon.Fill = System.Windows.Media.Brushes.White; // Regular white octagon
        }
    }

    private void DriveModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var mode = DriveModeCombo.SelectedIndex switch
        {
            0 => DriveMode.SingleStickArcade,
            1 => DriveMode.SplitStickArcade,
            2 => DriveMode.DualStickTank,
            _ => DriveMode.SingleStickArcade
        };
        _currentTuning = _currentTuning with { Mode = mode };

        if (ThrottleAxisTitle != null && SteerAxisTitle != null)
        {
            if (mode == DriveMode.DualStickTank)
            {
                ThrottleAxisTitle.Text = "LEFT THRUSTER AXIS";
                SteerAxisTitle.Text = "RIGHT THRUSTER AXIS";
            }
            else
            {
                ThrottleAxisTitle.Text = "VERTICAL AXIS";
                SteerAxisTitle.Text = "HORIZONTAL AXIS";
            }
        }

        UpdateCurveData();
    }

    private void CurveTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var curve = CurveTypeCombo.SelectedIndex == 0 ? CurveType.GammaPower : CurveType.CubicPolynomial;

        if (curve == CurveType.GammaPower)
        {
            ThrottleCurveLabel.Text = "Gamma:";
            SteerCurveLabel.Text = "Gamma:";
            ThrottleCurveSlider.Minimum = 1.00;
            ThrottleCurveSlider.Maximum = 4.00;
            ThrottleCurveSlider.Value = 1.90;
            SteerCurveSlider.Minimum = 1.00;
            SteerCurveSlider.Maximum = 4.00;
            SteerCurveSlider.Value = 1.90;
        }
        else
        {
            ThrottleCurveLabel.Text = "Expo:";
            SteerCurveLabel.Text = "Expo:";
            ThrottleCurveSlider.Minimum = 0.00;
            ThrottleCurveSlider.Maximum = 1.00;
            ThrottleCurveSlider.Value = 0.60;
            SteerCurveSlider.Minimum = 0.00;
            SteerCurveSlider.Maximum = 1.00;
            SteerCurveSlider.Value = 0.60;
        }

        _currentTuning = _currentTuning with
        {
            Curve = curve,
            ThrottleCurveParam = ThrottleCurveSlider.Value,
            SteerCurveParam = SteerCurveSlider.Value
        };

        UpdateCurveData();
        UpdateSliderLabels();
    }

    private void TuningSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;

        double tDeadband = ThrottleDeadbandSlider.Value;
        double tCurve = ThrottleCurveSlider.Value;
        double sDeadband = SteerDeadbandSlider.Value;
        double sCurve = SteerCurveSlider.Value;
        double maxFwd = MaxFwdSlider != null ? MaxFwdSlider.Value : 1.00;
        double maxRev = MaxRevSlider != null ? MaxRevSlider.Value : 0.75;
        int slew = SlewSlider != null ? (int)SlewSlider.Value : 50;
        var curve = CurveTypeCombo.SelectedIndex == 0 ? CurveType.GammaPower : CurveType.CubicPolynomial;

        _currentTuning = new TuningConfig(tDeadband, tCurve, sDeadband, sCurve, maxFwd, maxRev, slew, _currentTuning.Mode, curve);

        UpdateCurveData();
        UpdateSliderLabels();
    }

    private void UpdateSliderLabels()
    {
        if (ThrottleDeadbandText != null) ThrottleDeadbandText.Text = $"{ThrottleDeadbandSlider.Value:F2}";
        if (ThrottleCurveText != null) ThrottleCurveText.Text = $"{ThrottleCurveSlider.Value:F2}";
        if (SteerDeadbandText != null) SteerDeadbandText.Text = $"{SteerDeadbandSlider.Value:F2}";
        if (SteerCurveText != null) SteerCurveText.Text = $"{SteerCurveSlider.Value:F2}";

        if (MaxFwdValText != null && MaxFwdSlider != null) MaxFwdValText.Text = $"{(int)(MaxFwdSlider.Value * 100)}%";
        if (MaxRevValText != null && MaxRevSlider != null) MaxRevValText.Text = $"{(int)(MaxRevSlider.Value * 100)}%";
        if (SlewValText != null && SlewSlider != null) SlewValText.Text = $"{(int)SlewSlider.Value}";
    }

    /// <summary>
    /// Worker thread 50 Hz tick (runs every 20.0 ms).
    /// </summary>
    private void On50HzTick()
    {
        _sequenceNumber++;
        _fpsCounter++;

        var tuning = _currentTuning;

        // Poll Xbox Controller 0 (Driver 1: Dredger)
        bool connected = XInputNative.GetState(0, out var state);
        _controllerConnected = connected;

        short leftUs = 1500;
        short rightUs = 1500;
        short rollerPwm = 0;
        byte armAction = 0;
        byte flags = 0;
        double throttle = 0.0;
        double steer = 0.0;
        double sThrottle = 0.0;
        double sSteer = 0.0;

        if (connected)
        {
            if ((state.Gamepad.wButtons & (ushort)XInputNative.GamepadButtons.Back) != 0)
            {
                _emergencyStop = true;
            }

            if (!_emergencyStop)
            {
                double rawLeftX = XInputNative.NormalizeAxis(state.Gamepad.sThumbLX);
                double rawLeftY = XInputNative.NormalizeAxis(state.Gamepad.sThumbLY);
                double rawRightX = XInputNative.NormalizeAxis(state.Gamepad.sThumbRX);
                double rawRightY = XInputNative.NormalizeAxis(state.Gamepad.sThumbRY);

                var (l, r, st, ss) = DriveMixer.ComputeDriveThrust(rawLeftX, rawLeftY, rawRightX, rawRightY, tuning);
                leftUs = l;
                rightUs = r;
                sThrottle = st;
                sSteer = ss;

                _rawLeftX = rawLeftX;
                _rawLeftY = rawLeftY;
                _rawRightX = rawRightX;
                _rawRightY = rawRightY;

                if (tuning.Mode == DriveMode.DualStickTank)
                {
                    throttle = rawLeftY;
                    steer = rawRightY;
                }
                else
                {
                    throttle = rawLeftY;
                    steer = tuning.Mode == DriveMode.SingleStickArcade ? rawLeftX : rawRightX;
                }

                // Triggers for star roller intake
                double rt = XInputNative.NormalizeTrigger(state.Gamepad.bRightTrigger);
                double lt = XInputNative.NormalizeTrigger(state.Gamepad.bLeftTrigger);
                if (rt > 0.1)
                {
                    rollerPwm = (short)Math.Round(rt * 255.0);
                }
                else if (lt > 0.1)
                {
                    rollerPwm = (short)-Math.Round(lt * 255.0);
                }

                // Arm actions: A = Lower, Y = Raise
                if ((state.Gamepad.wButtons & (ushort)XInputNative.GamepadButtons.A) != 0)
                {
                    armAction = 0x01;
                }
                else if ((state.Gamepad.wButtons & (ushort)XInputNative.GamepadButtons.Y) != 0)
                {
                    armAction = 0x02;
                }
            }
        }

        if (_emergencyStop)
        {
            flags |= 0x01;
            leftUs = 1500;
            rightUs = 1500;
            rollerPwm = 0;
            armAction = 0;
            sThrottle = 0.0;
            sSteer = 0.0;
        }

        // Cache for UI dispatch
        _rawThrottle = throttle;
        _rawSteer = steer;
        _shapedThrottle = sThrottle;
        _shapedSteer = sSteer;
        _lastLeftUs = leftUs;
        _lastRightUs = rightUs;
        _lastRollerPwm = rollerPwm;
        _lastArmAction = armAction;

        // Assemble Robot Commands
        var bot1 = new RobotCommand
        {
            RobotId = 0x01,
            LeftThrust = leftUs,
            RightThrust = rightUs,
            RollerPwm = rollerPwm,
            ArmAction = armAction,
            Flags = flags
        };

        var bot2 = new RobotCommand { RobotId = 0x02, LeftThrust = 1500, RightThrust = 1500 };
        var bot3 = new RobotCommand { RobotId = 0x03, LeftThrust = 1500, RightThrust = 1500 };

        // Serialize & send packet
        byte[] packet = PacketProtocol.Serialize(_sequenceNumber, bot1, bot2, bot3);
        if (_serial.WriteFrame(packet))
        {
            _packetsSent++;
        }

        // Throttled UI Dispatch (~25 Hz update)
        if (++_uiThrottleDivider % 2 == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, UpdateDashboardUi);
        }
    }

    private void UpdateDashboardUi()
    {
        if (_controllerConnected)
        {
            ControllerStatusText.Text = "CONTROLLER CONNECTED";
            ControllerStatusText.Foreground = new MediaBrush(MediaColor.FromRgb(0x22, 0xC5, 0x5E));
        }
        else
        {
            ControllerStatusText.Text = "CONTROLLER DISCONNECTED";
            ControllerStatusText.Foreground = new MediaBrush(MediaColor.FromRgb(0xDC, 0x26, 0x26));
        }

        // Cartesian format (Input, Output) on curves
        ThrottlePointText.Text = $"({_rawThrottle:+0.00;-0.00;0.00}, {_shapedThrottle:+0.00;-0.00;0.00})";
        SteerPointText.Text = $"({_rawSteer:+0.00;-0.00;0.00}, {_shapedSteer:+0.00;-0.00;0.00})";

        // Dual Stick Telemetry
        LeftInputText.Text = $"({_rawLeftX:+0.00;-0.00;0.00}, {_rawLeftY:+0.00;-0.00;0.00})";
        RightInputText.Text = $"({_rawRightX:+0.00;-0.00;0.00}, {_rawRightY:+0.00;-0.00;0.00})";

        double leftMag = Math.Min(1.0, Math.Sqrt((_rawLeftX * _rawLeftX) + (_rawLeftY * _rawLeftY)));
        double rightMag = Math.Min(1.0, Math.Sqrt((_rawRightX * _rawRightX) + (_rawRightY * _rawRightY)));
        LeftMagText.Text = $"{leftMag:F2}";
        RightMagText.Text = $"{rightMag:F2}";

        // 1. Update Live Marker Dots (Topmost unclipped layer, full dot visible even in corners)
        if (ThrottleMarkerDot != null)
        {
            var p = DataToCanvasDip(ThrottlePlot, Math.Clamp(_rawThrottle, -1.0, 1.0), Math.Clamp(_shapedThrottle, -1.0, 1.0));
            Canvas.SetLeft(ThrottleMarkerDot, p.X - 4.5);
            Canvas.SetTop(ThrottleMarkerDot, p.Y - 4.5);
        }

        if (SteerMarkerDot != null)
        {
            var p = DataToCanvasDip(SteerPlot, Math.Clamp(_rawSteer, -1.0, 1.0), Math.Clamp(_shapedSteer, -1.0, 1.0));
            Canvas.SetLeft(SteerMarkerDot, p.X - 4.5);
            Canvas.SetTop(SteerMarkerDot, p.Y - 4.5);
        }

        if (LeftStickMarkerDot != null)
        {
            var p = DataToCanvasDip(LeftStickPlot, Math.Clamp(_rawLeftX, -1.0, 1.0), Math.Clamp(_rawLeftY, -1.0, 1.0));
            Canvas.SetLeft(LeftStickMarkerDot, p.X - 4.5);
            Canvas.SetTop(LeftStickMarkerDot, p.Y - 4.5);
        }

        if (RightStickMarkerDot != null)
        {
            var p = DataToCanvasDip(RightStickPlot, Math.Clamp(_rawRightX, -1.0, 1.0), Math.Clamp(_rawRightY, -1.0, 1.0));
            Canvas.SetLeft(RightStickMarkerDot, p.X - 4.5);
            Canvas.SetTop(RightStickMarkerDot, p.Y - 4.5);
        }

        // 2. Update Fixed Microsecond Values directly above corresponding bars
        LeftUsText.Text = $"{_lastLeftUs} µs";
        RightUsText.Text = $"{_lastRightUs} µs";

        // 3. Update Live Percentage to Immediate Left of Left Bar and Immediate Right of Right Bar
        double leftPct = (_lastLeftUs - 1500) / 5.0; // [-100 .. 100]
        double rightPct = (_lastRightUs - 1500) / 5.0;

        LeftPctText.Text = $"{leftPct:+0;-0;0}%";
        RightPctText.Text = $"{rightPct:+0;-0;0}%";

        // 4. Update Separated Vertical Thruster Meters (Canvas height: 176, Baseline: 88)
        UpdateVerticalMeter(LeftMeterBar, _lastLeftUs);
        UpdateVerticalMeter(RightMeterBar, _lastRightUs);

        RollerValText.Text = _lastRollerPwm == 0 ? "0 PWM (OFF)" : $"{_lastRollerPwm} PWM";
        ArmValText.Text = _lastArmAction switch
        {
            0x01 => "LOWERING (0x01)",
            0x02 => "RAISING (0x02)",
            _ => "HOLD (0x00)"
        };
    }

    private static void UpdateVerticalMeter(Rectangle bar, short pulseUs)
    {
        const double centerBaseline = 88.0;
        const double maxExtent = 87.0;

        if (pulseUs >= 1500)
        {
            // Forward: climbs upward in green from baseline
            double frac = Math.Clamp((pulseUs - 1500) / 500.0, 0.0, 1.0);
            double h = frac * maxExtent;
            bar.Height = Math.Max(0, h);
            bar.Fill = new MediaBrush(MediaColor.FromRgb(0x22, 0xC5, 0x5E));
            Canvas.SetTop(bar, centerBaseline - h);
        }
        else
        {
            // Reverse: climbs downward in red from baseline (User requirement: turns red when going backwards)
            double frac = Math.Clamp((1500 - pulseUs) / 500.0, 0.0, 1.0);
            double h = frac * maxExtent;
            bar.Height = Math.Max(0, h);
            bar.Fill = new MediaBrush(MediaColor.FromRgb(0xDC, 0x26, 0x26));
            Canvas.SetTop(bar, centerBaseline);
        }
    }
}
