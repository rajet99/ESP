# Learning Progress

## Topic: Architecture & Technology Selection
- **Introduced**:
  - Comparison between Python, C++, and C# .NET for high-speed robot base station.
  - XInput kernel interface vs generic DirectInput.
  - ESP-NOW single-broadcast 50 Hz framing vs multi-unicast.
  - ESC BEC vs dedicated buck converter thermal and noise considerations.
- **Demonstrated understanding**:
  - Learner recognized value of live parameter tuning on the fly and chose C# .NET to combine live GUI slider tuning with native XInput and low-latency serial streaming.
  - Learner highlighted existing background in Unity C# and extensive ESP32 C++ experience.

## Topic: Thread Synchronization & Concurrency
- **Introduced**:
  - Decoupling UI thread from 50 Hz serial transmission loop.
- **Demonstrated understanding**:
  - Learner proposed a lockless atomic sharing mechanism ("pushpin" pattern) where the UI thread posts updates and the worker thread reads atomically, preventing locks and thread contention.

## Topic: Wireless Protocol & Packet Framing
- **Introduced**:
  - ESP-NOW payload capacity (250-byte maximum).
  - Breakdown of our 36-byte application frame (Sync, Seq, 3x Bot payloads, CRC16).
  - RF airtime, multipath packet corruption, and serial transmission latency.
- **Demonstrated understanding**:
  - Learner confirmed framing architecture and authorized implementation.

## Topic: Visual Curve Graphs & 2D Stick Visualizer
- **Implemented**:
  - Independent Throttle and Steering curves (separate Deadband and Expo sliders).
  - Interactive 2D transfer curve graphs rendered with WPF `Polyline` and live glowing `Ellipse` markers.
  - Real-time 2D physical left stick position visualizer canvas with crosshair and deadband ring.
  - Numerical readouts for input deflection magnitude and output pulse widths.
  - Verified build with 0 warnings, 0 errors.

## Topic: Controller Ergonomics & Drive Modes
- **Implemented**:
  - Added `DriveMode` enum (`SingleStickArcade` vs `SplitStickArcade`) to `TuningConfig`.
  - Added Drive Mode selector dropdown in `MainWindow.xaml` header.
  - Updated 50 Hz control loop to dynamically read either Left Stick X or Right Stick X based on selected mode.
  - 2D Stick Pad automatically updates header text to show active controls.
  - Verified build with 0 warnings, 0 errors.

## Topic: Thruster Separation & Gamma Curve Shaping
- **Mathematical Comparison**:
  - Cubic polynomial: $y = (1 - a)x + a x^3$ has initial slope $1 - a$ at $x=0$ ($0.40$ for expo=0.60).
  - Gamma power curve: $y = x^\gamma$ has zero derivative at $x=0$ for $\gamma > 1$, providing a flatter deadband-exit and progressive ramp.
  - Equivalence matching: At mid-stick $x = 0.5$, Cubic($0.60$) yields $0.275$. Solving $0.5^\gamma = 0.275 \implies \gamma \approx 1.86$. Initializing Gamma to $1.90$ matches the mid-throttle feel while giving softer center control.
- **Learner Design Decisions**:
  - Single dropdown in `AXIS TUNING` header for both axes: `Curve: Gamma (Power)` [Enabled by default] vs `Cubic Polynomial`.
  - Direct Gamma value slider ($\gamma \in [1.00, 4.00]$), initialized to $1.90$.
  - Two distinct separated thruster bars with spacing between them.
  - Sub-tick lines inside each bar at 25%, 50%, 75%, 100% in each direction with zero labels.
  - Live percentage number directly to the immediate left of the Left bar, and to the immediate right of the Right bar.
  - Fixed microsecond value (`1500 µs`) positioned directly above each respective bar.
- **Implemented**:
  - `GroundStation/Core/TuningConfig.cs`: Added `CurveType` enum (`GammaPower` vs `CubicPolynomial`), initialized with `Curve = CurveType.GammaPower` and `ThrottleCurveParam = 1.90`, `SteerCurveParam = 1.90`.
  - `GroundStation/Core/DriveMixer.cs`: Updated `ShapeAxis` to implement pure power-law $y = x^\gamma$ for `CurveType.GammaPower` alongside weighted cubic polynomial blending.
  - `GroundStation/MainWindow.xaml`: Added unified `CurveTypeCombo` dropdown in `AXIS TUNING` header; dynamic slider adaptation ($1.00-4.00$ for Gamma vs $0.00-1.00$ for Cubic); separated thruster meter enclosures with internal 25%, 50%, 75%, 100% tick lines; fixed `1500 µs` microsecond headers centered above each bar; live digital percentage placed to immediate left of Left bar and immediate right of Right bar.
  - `GroundStation/MainWindow.xaml.cs`: Wired curve selection event handler, updated real-time ScottPlot 5 curve generation for gamma/cubic, and formatted flanking percentage and fixed $\mu\text{s}$ readouts.
- **Verification**: `dotnet build GroundStation/GroundStation.csproj` passed with 0 Warnings and 0 Errors.
## Topic: Dual-Stick Tank Mode & Dual Stick Visualizers
- **Learner Design Decisions**:
  - Add `DualStickTank` mode to `DriveMode` enum: Left Stick Y directly drives Left Thruster, Right Stick Y directly drives Right Thruster.
  - In Tank mode, the two tuning curves dynamically adapt into `LEFT THRUSTER AXIS` and `RIGHT THRUSTER AXIS`, providing independent deadband and gamma curve shaping per thruster motor.
  - Implement two distinct 2D stick visualizer scopes (`LeftStickPlot` and `RightStickPlot`) side-by-side to independently track both physical thumbsticks in real-time with unclipped corner-visible dots across all 3 drive modes.
- **Implemented**:
  - `GroundStation/Core/TuningConfig.cs`: Added `DualStickTank` to `DriveMode` enum.
  - `GroundStation/Core/DriveMixer.cs`: Implemented unified `ComputeDriveThrust` accepting all 4 raw stick axes (`leftX`, `leftY`, `rightX`, `rightY`), handling `SingleStickArcade`, `SplitStickArcade`, and `DualStickTank`.
  - `GroundStation/MainWindow.xaml`: Added `Dual-Stick Tank (Left Y + Right Y)` to `DriveModeCombo`; named `ThrottleAxisTitle` and `SteerAxisTitle`; embedded side-by-side 130x130 `LeftStickPlot` and `RightStickPlot` with unclipped Canvas marker overlays; added dual telemetry readouts.
  - `GroundStation/MainWindow.xaml.cs`: Initialized both stick scopes with independent deadband boxes; updated 50 Hz control loop to poll both sticks and compute mixed/tank outputs; wired dynamic axis retargeting titles on mode change; updated dual live marker tracking.
- **Verification**: `dotnet build GroundStation/GroundStation.csproj` passed with 0 Warnings and 0 Errors.
- **Stage**: complete

## Topic: ESP-NOW Communication Test Bench
- **Learner Design Decisions**:
  - Create a standalone test sketch in a dedicated directory: `Firmware/TestReceiver/TestReceiver.ino`.
  - Built-in LED (GPIO 2) remains solid OFF when off-throttle (within deadband around 1500 µs).
  - Blink frequency scales linearly with throttle magnitude: $f = \frac{|\text{LeftThrust} - 1500|}{500} \times 10.0\text{ Hz}$ (reaching 10 Hz at full forward/reverse).
  - Use existing `RelayTransmitter.ino` and C# `GroundStation` on laptop as the transmitter.
- **Stage**: awaiting implementation confirmation
- **Scope**: Create `Firmware/TestReceiver/TestReceiver.ino` with non-blocking timing, CRC-16 validation, and failsafe handling.
