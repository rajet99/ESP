/**
 * Caltech ME72 Dredge Challenge — Controlled Single Motor Test Receiver
 * Target: ESP32-WROOM-32U (External 2.4 GHz Antenna)
 *
 * Purpose: Verify the full wireless control chain (GroundStation -> RelayTransmitter
 * -> ESP-NOW) by driving ONE thruster (Right, per RobotReceiver pinout) from the
 * Xbox controller. The drive-mode mixing (Arcade/Tank) happens entirely in the
 * GroundStation; this receiver only consumes bot1.right_thrust.
 *
 * Hardware Connections:
 * - GPIO 19: ESC Signal (Right thruster, matches RobotReceiver)
 * - GPIO 2:  Optional status LED (boards without LED: serial telemetry replaces it)
 * - ESC BEC red wire: CLIPPED / INSULATED — do NOT connect
 * - Power: ESP32 via USB ONLY (no buck converter simultaneously)
 *
 * Safety Features:
 * - 200 ms watchdog: no valid packet -> neutral 1500 us
 * - E-stop flag (bot1.flags bit 0) -> neutral
 * - Slew-rate limiter (50 us per 20 ms cycle) prevents inrush
 *
 * Serial Telemetry @ 115200 (replaces LED indication):
 * - Boot banner with configuration echo
 * - 500 ms status line: RX count, link state, right pulse, failsafe event count
 * - Event lines for watchdog trips and E-stop flags
 *
 * Dry-test note: U5 thrusters rely on water cooling. Keep dry bursts short
 * (< ~30 s) and check motor/ESC temperature between runs.
 */

#include <esp_now.h>
#include <WiFi.h>
#include <esp_wifi.h>
#include <esp_idf_version.h>

#define ESC_RIGHT_PIN     19
#define STATUS_LED_PIN    2      // Optional; wire LED + 330R to GND if desired
#define TELEMETRY_PIN_ID  0      // 0 = disable LED blink output (no LED on board)

#define WATCHDOG_TIMEOUT_MS 200   // Failsafe triggers if no packet within 200 ms
#define MAX_SLEW_US_PER_CYCLE 50  // Max pulse delta per 20 ms cycle
#define PACKET_SIZE        36
#define SYNC_HEADER        0xAA55
#define THIS_ROBOT_ID      0x01

#define TELEMETRY_INTERVAL_MS 500
#define PWM_FREQ           50     // 50 Hz RC Servo standard (ApisQueen U5 spec)
#define PWM_RES_BITS       16     // 16-bit resolution (0..65535 per 20 ms)

// Struct layout must match C# .NET PacketProtocol and RelayTransmitter
#pragma pack(push, 1)
struct RobotCommand {
    uint8_t  robot_id;
    int16_t  left_thrust;
    int16_t  right_thrust;
    int16_t  roller_pwm;
    uint8_t  arm_action;
    uint8_t  flags;
    uint8_t  reserved;  // Padding: bots are 10 bytes so frame = 36 bytes
};

struct BroadcastFrame {
    uint16_t sync_marker;
    uint16_t seq_num;
    RobotCommand bot1;
    RobotCommand bot2;
    RobotCommand bot3;
    uint16_t crc16;
};
#pragma pack(pop)

// State updated in ESP-NOW receive callback (WiFi task context)
volatile uint32_t lastValidRxMs = 0;
volatile int16_t targetRightUs = 1500;
volatile uint8_t targetFlags = 0;

// Slew-rate filtered output
int16_t currentRightUs = 1500;

// Telemetry counters
uint32_t rxPacketCount = 0;
uint32_t failsafeEventCount = 0;
uint32_t lastTelemetryMs = 0;
bool failsafeActive = false;

// CRC-16-CCITT (Polynomial 0x1021, Initial 0xFFFF)
uint16_t computeCrc16(const uint8_t* data, size_t len) {
    uint16_t crc = 0xFFFF;
    for (size_t i = 0; i < len; i++) {
        crc ^= (uint16_t)(data[i] << 8);
        for (uint8_t j = 0; j < 8; j++) {
            if (crc & 0x8000) {
                crc = (crc << 1) ^ 0x1021;
            } else {
                crc <<= 1;
            }
        }
    }
    return crc;
}

// Convert microsecond pulse width (1000..2000 us) to 16-bit LEDC duty cycle
// At 50 Hz, period = 20,000 us -> Duty = (us / 20000) * 65535
uint32_t usToDuty(int16_t us) {
    us = constrain(us, 1000, 2000);
    return (uint32_t)(((uint64_t)us * 65535ULL) / 20000ULL);
}

void writeEscPwm(int16_t us) {
#if defined(ESP_ARDUINO_VERSION_MAJOR) && (ESP_ARDUINO_VERSION_MAJOR >= 3)
    ledcWrite(ESC_RIGHT_PIN, usToDuty(us));
#else
    ledcWrite(0, usToDuty(us));
#endif
}

// Universal ESP-NOW receive callback compatible with Arduino Core 2.x and 3.x
#if ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(5, 0, 0)
void onDataRecv(const esp_now_recv_info_t *recv_info, const uint8_t *incomingData, int len)
#else
void onDataRecv(const uint8_t *mac_addr, const uint8_t *incomingData, int len)
#endif
{
    if (len != PACKET_SIZE) return;

    // Verify CRC-16 over bytes 0..33
    uint16_t expectedCrc = computeCrc16(incomingData, PACKET_SIZE - 2);
    uint16_t receivedCrc = (uint16_t)(incomingData[PACKET_SIZE - 2] | (incomingData[PACKET_SIZE - 1] << 8));
    if (expectedCrc != receivedCrc) return;

    // Check sync header
    const BroadcastFrame* frame = (const BroadcastFrame*)incomingData;
    if (frame->sync_marker != SYNC_HEADER) return;

    // Match this robot's ID (Bot 1)
    if (frame->bot1.robot_id == THIS_ROBOT_ID) {
        targetRightUs = constrain(frame->bot1.right_thrust, 1000, 2000);
        targetFlags = frame->bot1.flags;
        lastValidRxMs = millis();
        rxPacketCount++;
    }
}

void setup() {
    Serial.begin(115200);
    Serial.println();
    Serial.println("==============================================");
    Serial.println("[CtrlSingleMotorRx] Boot");
    Serial.println("[CtrlSingleMotorRx] Config: RIGHT thruster, GPIO 19");
    Serial.print(  "[CtrlSingleMotorRx] Frame: ");
    Serial.print(PACKET_SIZE);
    Serial.println(" bytes, CRC-16, Channel 11 (LR)");
    Serial.println("==============================================");

#if TELEMETRY_PIN_ID
    pinMode(STATUS_LED_PIN, OUTPUT);
    digitalWrite(STATUS_LED_PIN, LOW);
#endif

    // Initialize 50 Hz PWM on ESC signal pin
#if defined(ESP_ARDUINO_VERSION_MAJOR) && (ESP_ARDUINO_VERSION_MAJOR >= 3)
    ledcAttach(ESC_RIGHT_PIN, PWM_FREQ, PWM_RES_BITS);
#else
    ledcSetup(0, PWM_FREQ, PWM_RES_BITS);
    ledcAttachPin(ESC_RIGHT_PIN, 0);
#endif

    // Initial neutral pulse so the ESC arms silently
    writeEscPwm(1500);

    // Initialize Wi-Fi in Station mode on Channel 11 (LR mode)
    WiFi.mode(WIFI_STA);
    WiFi.disconnect();

    esp_wifi_set_promiscuous(true);
    esp_wifi_set_channel(11, WIFI_SECOND_CHAN_NONE);
    esp_wifi_set_promiscuous(false);
    esp_wifi_set_protocol(WIFI_IF_STA, WIFI_PROTOCOL_LR);

    // Initialize ESP-NOW
    if (esp_now_init() == ESP_OK) {
        esp_now_register_recv_cb(onDataRecv);
        Serial.println("[CtrlSingleMotorRx] ESP-NOW initialized, listening...");
    } else {
        Serial.println("[CtrlSingleMotorRx] ERROR: ESP-NOW init failed (looping)");
        while (1) {
            delay(1000);
            Serial.println("[CtrlSingleMotorRx] ERROR: ESP-NOW init failed");
        }
    }

    lastValidRxMs = millis();
}

void loop() {
    uint32_t now = millis();

    // 1. SAFETY WATCHDOG CHECK
    bool newFailsafe = (now - lastValidRxMs > WATCHDOG_TIMEOUT_MS) || (targetFlags & 0x01);

    if (newFailsafe && !failsafeActive) {
        failsafeActive = true;
        failsafeEventCount++;
        if (targetFlags & 0x01) {
            Serial.println("[CtrlSingleMotorRx] EVENT: E-STOP flag received (neutral)");
        } else {
            Serial.println("[CtrlSingleMotorRx] EVENT: WATCHDOG - no valid packet for 200 ms (neutral)");
        }
    } else if (!newFailsafe && failsafeActive) {
        failsafeActive = false;
        Serial.println("[CtrlSingleMotorRx] EVENT: link recovered, thrust control active");
    }

    int16_t desiredUs = failsafeActive ? 1500 : targetRightUs;

    // 2. SLEW-RATE LIMITING
    currentRightUs += constrain(desiredUs - currentRightUs, -MAX_SLEW_US_PER_CYCLE, MAX_SLEW_US_PER_CYCLE);

    // 3. APPLY OUTPUT TO HARDWARE ESC
    writeEscPwm(currentRightUs);

#if TELEMETRY_PIN_ID
    // Optional LED: solid when link healthy, fast flash on failsafe
    if (failsafeActive) {
        digitalWrite(STATUS_LED_PIN, (now / 100) % 2 == 0 ? HIGH : LOW);
    } else {
        digitalWrite(STATUS_LED_PIN, HIGH);
    }
#endif

    // 4. PERIODIC SERIAL TELEMETRY (500 ms)
    if (now - lastTelemetryMs >= TELEMETRY_INTERVAL_MS) {
        lastTelemetryMs = now;
        Serial.print("[CtrlSingleMotorRx] RX:");
        Serial.print(rxPacketCount);
        Serial.print(" link=");
        Serial.print(failsafeActive ? "FAILSAFE" : "OK");
        Serial.print(" right=");
        Serial.print(currentRightUs);
        Serial.print("us");
        Serial.print(" failsafe_events=");
        Serial.println(failsafeEventCount);
    }

    delay(20); // 50 Hz control loop cadence
}
