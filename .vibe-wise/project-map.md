# Project Map

## Purpose
A real-time robot base station desktop application and wireless embedded relay system for Caltech ME72 Dredge Challenge. Reads up to three wired Xbox controllers, applies live-tunable input shaping (deadbands, cubic curves, differential drive mixing), streams binary control packets over high-speed USB-UART (921,600 baud) to an ESP32 transmitter relay, which broadcasts 50 Hz frames over 2.4 GHz ESP-NOW to onboard receiver ESP32s on the competition robots.

## Requirements
- Multi-controller input: Up to 3 wired Xbox controllers simultaneously using XInput.
- Live tuning: Sliders for deadzones, stick curves, max reverse thrust, and slew rates that take effect in real time while driving.
- Sub-5ms control loop: Deterministic 50 Hz transmission clock.
- Multi-robot ESP-NOW: Broadcast frame containing sliced commands for Robot 1 (Dredger), Robot 2 (Skimmer), and Robot 3 (Transfer).
- Failsafe: 200 ms timeout on receivers setting thruster pulses to 1500 µs (neutral) and stopping motors.
- Immediate prototype target: Single-bot propulsion test (ApisQueen U5 differential thrusters).

## Components
- `GroundStation.Desktop` [IMPLEMENTED]: C# .NET 8 WPF desktop application (`GroundStation/`).
  - `Core/XInputNative.cs`: Native Windows `xinput1_4.dll` P/Invoke.
  - `Core/TuningConfig.cs`: Immutable record for atomic reference swap.
  - `Core/DriveMixer.cs`: Arcade drive math with deadbands and cubic polynomial stick shaping.
  - `Core/PacketProtocol.cs`: 36-byte `BroadcastFrame` serialization and CRC-16.
  - `Core/HighResolutionTimer.cs`: 50 Hz multimedia timer loop via `winmm.dll`.
  - `Core/SerialTransport.cs`: High-speed serial streamer at 921,600 baud via `System.IO.Ports`.
  - `MainWindow.xaml` / `MainWindow.xaml.cs`: Dashboard UI with live tuning sliders and stick visualizers.
- `RelayTransmitter` [IMPLEMENTED]: ESP32 Arduino/C++ firmware (`Firmware/RelayTransmitter/RelayTransmitter.ino`).
  - High-speed UART RX at 921,600 baud with 1024-byte FIFO buffer.
   - ESP-NOW broadcast on Channel 11 in Long Range (LR) mode.
- `RobotReceiver` [IMPLEMENTED]: ESP32 Arduino/C++ firmware (`Firmware/RobotReceiver/RobotReceiver.ino`).
   - ESP-NOW RX on Channel 11 (LR mode).
  - 200 ms watchdog failsafe setting neutral 1500 µs pulses.
  - Slew-rate limiter on pulse changes to prevent inrush brownouts.
  - 50 Hz LEDC PWM pulse generation on GPIO 18 (Left ESC) and GPIO 19 (Right ESC).

## Main Flow
```
[Xbox Controllers (1-3)]
       | (XInput polling)
       v
[C# .NET 8 Ground Station] -- (Live Tuning Sliders applied via Atomic Pushpin)
       | (USB-UART @ 921,600 baud, 50 Hz Binary Frame)
       v
[ESP32 Relay Transmitter]
        | (2.4 GHz ESP-NOW Channel 11 Broadcast, LR mode)
       v
[ESP32 Robot 1 Receiver] ---> [MCPWM/LEDC GPIO 18/19] ---> [Bi-Dir ESCs] ---> [ApisQueen U5 Thrusters]
```

## Data and Trust Boundaries
- Laptop USB-to-UART: 921,600 baud, 8N1, 36-byte binary frame with 0xAA55 sync header and CRC16 checksum.
- Over-the-air RF: ESP-NOW broadcast payload (unencrypted for minimal latency, validated by CRC16 and monotonic sequence counter).

## Build and Deployment
- C# .NET: .NET 8 SDK (`dotnet build GroundStation/GroundStation.csproj` - verified: 0 warnings, 0 errors).
- ESP32 Firmware: Arduino IDE / PlatformIO with ESP32 board package.
