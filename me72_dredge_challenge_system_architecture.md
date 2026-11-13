# ME72 Dredge Challenge — System Architecture & Control Documentation

This document compiles the electrical, wireless, and power architecture for the Caltech ME72 Dredge Challenge robots. It outlines the end-to-end setup from shore-side Xbox Elite controllers down to the ApisQueen U5 thrusters and deployable dredging/skimming mechanisms.

---

## 1. System Overview & Architecture Diagram

To overcome 2.4 GHz surface reflection, water attenuation, and local 2.4 GHz spectrum congestion, control packets are routed via a wired dry-dock base station transmitting scheduled frames over **ESP-NOW** directly to elevated receiver antennas on each robot.

```
====================================================================================================
                             DRY-DOCK BASE STATION (SHORE SIDE)
====================================================================================================

  +-----------------------+       USB-C        +-----------------------------------------------+
  | Xbox Elite Controller |===================>|                                               |
  |    (Driver 1: Dredge) |   (Wired, 0ms)     |               OPERATOR LAPTOP                 |
  +-----------------------+                    |                                               |
                                               |  Python Control Node (pygame / pyserial):     |
  +-----------------------+       USB-C        |   1. Polls 3 controllers at 100 Hz            |
  | Xbox Elite Controller |===================>|   2. Applies deadbands & cubic stick curves   |
  |    (Driver 2: Skim)   |   (Wired, 0ms)     |   3. Packs 3x 10-byte structs + CRC8          |
  +-----------------------+                    |   4. Streams over Serial at 921600 baud       |
                                               |                                               |
  +-----------------------+       USB-C        +-----------------------------------------------+
  | Xbox Elite Controller |===================>                        ||
  |   (Driver 3: Transfer)|   (Wired, 0ms)                             || USB-UART (921600 baud)
  +-----------------------+                                            \/
                                               +-----------------------------------------------+
                                               |          TRANSMITTER NODE (ESP32-U)           |
                                               |                                               |
                                                |  - Wi-Fi Radio pinned to Channel 11 (LR Mode) |
                                               |  - Hardcoded Peer MACs: [Bot1, Bot2, Bot3]    |
                                               |  - TDMA / Single-Broadcast Frame at 50 Hz     |
                                               |  - Latency: < 4 ms glass-to-packet            |
                                               +-----------------------------------------------+
                                                                       ||
                                                                       || IPEX / U.FL Coaxial Lead
                                                                       \/
                                                                     \    /  5 dBi External Dipole
                                                                      \  /   (Elevated on Dry Dock)
                                                                       ||
=======================================================================||===========================
                        2.4 GHz ESP-NOW LINK (CHANNEL 11)               ||   Sub-5ms Latency
             ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~||~~~~~~~~~~~~~~~~~~
                                       |                               ||               |
              Broadcast Frame / Bot 1  |              Broadcast Frame /||Bot 2          | Broadcast / Bot 3
          +----------------------------+                               ||               +--------------------+
          |                                                            \/                                    |
          |                                                          \    /                                  |
          |                                                           \  /                                   |
          |                                                            ||                                    |
          |                                               +--------------------------+                       |
          |                                               |   COLLECTOR BOT 2        |                       |
          \/                                              |   (Surface Skimmer)      |                       \/
    \    /  3-5 dBi Dipole Antenna                        +--------------------------+             +--------------------+
     \  /   (Mast mounted 8-10" above waterline)                                                   |   TRANSFER BOT     |
      ||                                                                                           |   (Dry Dock / Zone)|
======||========================================================================================== +--------------------+
      ||                     COLLECTOR BOT 1: SUBMERGED DREDGER (ONBOARD ARCHITECTURE)
======||=================================================================================================================
      ||
      || IPEX / U.FL Coax (passes through waterproof bulkhead / cable gland)
      \/
+---------------------------------------------------+
|            ONBOARD RECEIVER (ESP32-U)             |
|                                                   |        5V Logic Supply
|  - Filters by Peer MAC & robot_id == 0x01         |<==============================+
|  - Verifies CRC8 & monotonic sequence number      |                               ||
|  - 200ms Watchdog Timer (Failsafe -> Neutral PWM) |                               ||
|  - Slew-rate limiter (Trapezoidal ramp on thrust) |                               ||
+---------------------------------------------------+                               ||
      |             |                  |          |                                 ||
      | MCPWM 1     | MCPWM 2          | PWM+DIR  | PWM+DIR                         ||
      | (50Hz RC)   | (50Hz RC)        | (20kHz)  | (20kHz)                         ||
      \/            \/                 \/         \/                                ||
+-----------+ +-----------+   +----------------------------+         +-------------------------------+
| 100A Bi-  | | 100A Bi-  |   | Dual Brushed Motor Driver  |         | 5V Isolated Buck Converter    |
| Dir ESC   | | Dir ESC   |   | (e.g., Cytron MDD20A)      |         | + 1000uF Low-ESR Capacitor    |
+-----------+ +-----------+   +----------------------------+         +-------------------------------+
      ||            ||               ||               ||                            /\
      || 3-Phase    || 3-Phase       || 2-Wire        || 2-Wire                     ||
      \/            \/               \/               \/                            ||
+-----------+ +-----------+   +-------------+  +--------------+                     ||
| ApisQueen | | ApisQueen |   | Star Roller |  | Deployable   |                     ||
|    U5     | |    U5     |   | Drive Motor |  | Arm Worm     |                     ||
| Thruster  | | Thruster  |   | (High Speed)|  | Gearmotor    |                     ||
|  (Left)   | |  (Right)  |   +-------------+  +--------------+                     ||
+-----------+ +-----------+          /\               /\                            ||
      /\            /\               ||               ||                            ||
      ||            ||               ||               ||                            ||
      ++============++===============++===============++============================++
      ||                                                                            
      ||  14.8V Main Power Bus (12 AWG Silicone Wire + Inline 60A Main Fuse)
      ||
+-----------------------------------------------------------------------------------+
|                   EMERGENCY DISCONNECT SWITCH / XT90-S LOOP KEY                   |
|                 (Mounted on exterior top deck per Rule 6.4 / 6.5)                 |
+-----------------------------------------------------------------------------------+
      ||
      ||  Parallel XT60 Harness
      \/
+-----------------------------------+       +-----------------------------------+
|  Turnigy 2200mAh 4S LiPo (Pack A) |=======|  Turnigy 2200mAh 4S LiPo (Pack B) |
|         (14.8V Nominal)           |       |         (14.8V Nominal)           |
+-----------------------------------+       +-----------------------------------+
                            (4S2P Config: 14.8V, 4400mAh)
```

---

## 2. Wireless Protocol & Communication Stack

### Why ESP-NOW Instead of Raw Bluetooth / Wi-Fi UDP
* **Layer-2 Framing:** ESP-NOW operates directly at the data link layer without the overhead of TCP/IP handshakes, DHCP negotiation, or BLE GATT pairing.
* **Resilience Near Water:** If spray or an obstacle briefly degrades the signal, there is no multi-second reconnection sequence. The receiver simply processes the next incoming frame.
* **Zero Cross-Talk:** Transmitter and receivers are bound by hardware MAC addresses and can be pinned to **Wi-Fi Channel 11** (the highest FCC-legal 2.4 GHz channel in the US).
* **Long Range (LR) Mode:** Enabling `WIFI_PROTOCOL_LR` boosts receiver sensitivity by ~4 dB using low-rate direct-sequence spread spectrum.

### Telemetry Packet Struct (C++ / ESP-IDF)

```cpp
#pragma pack(push, 1)
struct RobotCommand {
    uint8_t  robot_id;     // 0x01: Dredger, 0x02: Skimmer, 0x03: Transfer
    uint16_t seq_num;      // Monotonically increasing packet ID
    int16_t  left_thrust;  // 1000us (Full Rev) to 2000us (Full Fwd), 1500us Neutral
    int16_t  right_thrust; // 1000us (Full Rev) to 2000us (Full Fwd), 1500us Neutral
    int16_t  roller_pwm;   // -255 to 255 (Bi-directional intake speed)
    uint8_t  arm_action;   // 0x00: Hold, 0x01: Deploy/Down, 0x02: Retract/Up
    uint8_t  flags;        // Bit 0: Emergency Stop, Bit 1: Hopper Open/Dump
    uint8_t  crc8;         // Polynomial checksum for data verification
};

// Global frame broadcasted over ESP-NOW every 20ms (50 Hz)
struct BroadcastPayload {
    uint16_t sync_marker;         // 0xAA55
    RobotCommand bots[3];         // Commands for all three robots
};
#pragma pack(pop)
```

### Safety Failsafe (Rule 6.4 & 6.5 Compliance)
Every onboard ESP32 runs an independent software watchdog:
* If no valid packet passing CRC checks arrives within **200 ms**, outputs instantly drop to neutral:
  * Thrusters: `1500 µs`
  * Intake & Winch Drivers: `0 PWM`

---

## 3. Power Budget: 4S Battery Calculation

### Match Parameters
* Heat Duration: 4 minutes 30 seconds ($4.5\text{ min} = 0.075\text{ hours}$)
* Working Nominal Voltage: $14.8\text{V}$ (4S LiPo)
* Recommended Discharge Depth: Max 80% to protect pack cycle life and avoid voltage cutoff sag.

### Current Draw Breakdown (Per Collector Bot)
| Subsystem | Load Type | Current @ 14.8V (Single) | Total Subsystem Draw |
| :--- | :--- | :--- | :--- |
| **ApisQueen U5 Thrusters (x2)** | Continuous Cruising (~50% duty) | $8.0\text{ A}$ each | $16.0\text{ A}$ |
| **ApisQueen U5 Thrusters (x2)** | Peak Punch / Steering Slew | $18.0\text{ A}$ each | $36.0\text{ A}$ peak |
| **Star Roller Brushed Motor** | Sweeping / Dredging against pebbles | $4.0 - 6.0\text{ A}$ cont. | $5.0\text{ A}$ avg |
| **Deploy Arm Actuator (Worm)** | Articulating / Lifting | $1.5 - 3.0\text{ A}$ intermittent | $0.5\text{ A}$ avg |
| **ESP32 + Logic + Buck Reg.** | Continuous | $0.25\text{ A}$ | $0.25\text{ A}$ |
| **Total Average Operating Draw** | — | — | **$\sim 21.75\text{ A}$** |

### Capacity Calculation
$$\text{Required Capacity} = 21.75\text{ A} \times 0.075\text{ hours} = 1.63\text{ Ah}\ (1630\text{ mAh})$$

* **One 2200 mAh Turnigy Pack (80% usable):** $1760\text{ mAh}$ usable.
  * *Verdict:* Margin is $< 8\%$. High stick input, wave resistance, or a tie-breaker heat will trigger brownouts or drain the pack below safe cell voltage.
* **Two 2200 mAh Packs in Parallel (4S2P = 4400 mAh):** $3520\text{ mAh}$ usable.
  * *Verdict:* Provides a robust $2.1\times$ safety factor, halves internal resistance ($R_i$), cuts voltage sag under thruster spikes, and keeps battery discharge rate under a cool 5C.

---

## 4. Actuator & Motor Selection Matrix

### A. Star Roller Intake Drive (High Speed / Moderate Torque)
* **Goal:** Spin compliant star flaps/brushes fast enough to overcome water bow waves and guide balls into the collector.
* **Target Spec:** 300 to 500 RPM under load, $1.5 - 2.5\ \text{N}\cdot\text{m}$ stall torque.
* **Top Options:**
  1. *GoBILDA 5203 / 5204 Series Yellow Jacket (13.7:1 or 19.2:1 ratio):* Runs 312 to 435 RPM at 12V. Driven at 14.8V with an 80% PWM duty cap, it delivers reliable brushed torque with an integrated quad encoder.
  2. *AndyMark PG27 Planetary Gearmotor:* Rugged, metal-geared, handles shock loads if a flap hits a pebble.
  3. *Waterproofed Low-KV Brushless Outrunner (Direct Submerged):* 5065 140KV motor with potted stator windings. Eliminates rotating dynamic shaft seals.

### B. Deployable Arm / Dredger Articulation (High Torque / Self-Locking)
* **Goal:** Lower dredger head onto the pebble bed and retract to clear the 2–3" staging perimeter rails without drawing continuous stall current.
* **Target Spec:** 15 to 40 RPM, $> 6\ \text{N}\cdot\text{m}$ torque, non-backdrivable.
* **Top Options:**
  1. *5840-31ZY High-Torque Worm Gearmotor (12V/24V):* Self-locking worm gearbox holds arm height at zero current draw.
  2. *Winch Drum with Dyneema Line + Torsion Spring Return:* Uses gravity/spring to float over uneven pebbles, winch cable only pulls upward to retract.

---

## 5. Electrical Best Practices for Water Operation

1. **Galvanic & Signal Grounding:** Connect the ESP32 ground, buck converter ground, and motor driver signal grounds together. Never use hull or water as a return path.
2. **Capacitive Filtering:** Place a $1000\ \mu\text{F}$, 35V low-ESR electrolytic capacitor across the input of the 5V logic buck converter to prevent thruster back-EMF spikes from causing ESP32 brownout resets.
3. **Emergency Disconnect:** Install a high-current XT90-S anti-spark loop key or marine rotary cutoff switch on the top deck for quick manual intervention per safety rules.