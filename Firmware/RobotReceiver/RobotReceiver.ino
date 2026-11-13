/**
 * Caltech ME72 Dredge Challenge — Robot 1 Receiver Node Firmware
 * Target: ESP32-WROOM-32U (External 2.4 GHz Antenna)
 * Phase 1: Pure Propulsion Test Bench (ApisQueen U5 Differential Drive)
 * 
 * Hardware Connections:
 * - GPIO 18: Left ESC Signal (ApisQueen U5 Left Thruster)
 * - GPIO 19: Right ESC Signal (ApisQueen U5 Right Thruster)
 * - GPIO 2:  Status LED (Solid = Active Link, Fast Flash = Failsafe Signal Lost)
 * - 5V/GND:  Regulated 5V from Buck Converter (+ 1000uF Low-ESR cap)
 * 
 * Safety Features:
 * - 200 ms Watchdog Timer (snaps to 1500us neutral if transmitter drops)
 * - Slew-rate limiter to eliminate battery brownouts from sudden reversals
 */

#include <esp_now.h>
#include <WiFi.h>
#include <esp_wifi.h>
#include <esp_idf_version.h>

#define STATUS_LED_PIN     2
#define ESC_LEFT_PIN       18
#define ESC_RIGHT_PIN      19

#define WATCHDOG_TIMEOUT_MS 200   // Failsafe triggers if no packet within 200 ms
#define MAX_SLEW_US_PER_CYCLE 50  // Maximum pulse delta per 20 ms (~250 us / 100 ms)
#define PACKET_SIZE        36
#define SYNC_HEADER        0xAA55
#define THIS_ROBOT_ID      0x01   // Robot 1: Dredger

#define PWM_FREQ           50     // 50 Hz RC Servo standard
#define PWM_RES_BITS       16     // 16-bit resolution (0..65535 per 20 ms)

// Struct matching C# .NET PacketProtocol
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

// Volatile state updated in ESP-NOW ISR
volatile uint32_t lastValidRxMs = 0;
volatile int16_t targetLeftUs = 1500;
volatile int16_t targetRightUs = 1500;
volatile uint8_t targetFlags = 0;

// Slew-rate filtered outputs
int16_t currentLeftUs = 1500;
int16_t currentRightUs = 1500;

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

void writeThrusterPwm(int16_t leftUs, int16_t rightUs) {
#if defined(ESP_ARDUINO_VERSION_MAJOR) && (ESP_ARDUINO_VERSION_MAJOR >= 3)
    ledcWrite(ESC_LEFT_PIN, usToDuty(leftUs));
    ledcWrite(ESC_RIGHT_PIN, usToDuty(rightUs));
#else
    ledcWrite(0, usToDuty(leftUs));
    ledcWrite(1, usToDuty(rightUs));
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

    // Verify CRC-16
    uint16_t expectedCrc = computeCrc16(incomingData, PACKET_SIZE - 2);
    uint16_t receivedCrc = (uint16_t)(incomingData[PACKET_SIZE - 2] | (incomingData[PACKET_SIZE - 1] << 8));
    if (expectedCrc != receivedCrc) return;

    // Check sync header
    const BroadcastFrame* frame = (const BroadcastFrame*)incomingData;
    if (frame->sync_marker != SYNC_HEADER) return;

    // Match this robot's ID (Bot 1 = Dredger)
    if (frame->bot1.robot_id == THIS_ROBOT_ID) {
        targetLeftUs = constrain(frame->bot1.left_thrust, 1000, 2000);
        targetRightUs = constrain(frame->bot1.right_thrust, 1000, 2000);
        targetFlags = frame->bot1.flags;
        lastValidRxMs = millis();
    }
}

void setup() {
    pinMode(STATUS_LED_PIN, OUTPUT);
    digitalWrite(STATUS_LED_PIN, LOW);

    // Initialize 50 Hz PWM on ESC pins
#if defined(ESP_ARDUINO_VERSION_MAJOR) && (ESP_ARDUINO_VERSION_MAJOR >= 3)
    ledcAttach(ESC_LEFT_PIN, PWM_FREQ, PWM_RES_BITS);
    ledcAttach(ESC_RIGHT_PIN, PWM_FREQ, PWM_RES_BITS);
#else
    ledcSetup(0, PWM_FREQ, PWM_RES_BITS);
    ledcAttachPin(ESC_LEFT_PIN, 0);
    ledcSetup(1, PWM_FREQ, PWM_RES_BITS);
    ledcAttachPin(ESC_RIGHT_PIN, 1);
#endif

    // Initial neutral pulses (1500 us) to arm ESCs safely
    writeThrusterPwm(1500, 1500);

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
    }
}

void loop() {
    uint32_t now = millis();

    // 1. SAFETY WATCHDOG CHECK (Rule 6.4 & 6.5)
    bool failsafeActive = (now - lastValidRxMs > WATCHDOG_TIMEOUT_MS) || (targetFlags & 0x01);

    int16_t desiredLeft;
    int16_t desiredRight;

    if (failsafeActive) {
        desiredLeft = 1500;
        desiredRight = 1500;

        // Fast flash LED (10 Hz) to indicate failsafe / signal loss
        digitalWrite(STATUS_LED_PIN, (now / 100) % 2 == 0 ? HIGH : LOW);
    } else {
        desiredLeft = targetLeftUs;
        desiredRight = targetRightUs;

        // Solid ON indicates healthy active radio link
        digitalWrite(STATUS_LED_PIN, HIGH);
    }

    // 2. SLEW-RATE LIMITING (Prevents sudden 36A inrush brownouts & water-hammer)
    int16_t deltaLeft = desiredLeft - currentLeftUs;
    currentLeftUs += constrain(deltaLeft, -MAX_SLEW_US_PER_CYCLE, MAX_SLEW_US_PER_CYCLE);

    int16_t deltaRight = desiredRight - currentRightUs;
    currentRightUs += constrain(deltaRight, -MAX_SLEW_US_PER_CYCLE, MAX_SLEW_US_PER_CYCLE);

    // 3. APPLY OUTPUTS TO HARDWARE ESCs
    writeThrusterPwm(currentLeftUs, currentRightUs);

    delay(20); // 50 Hz control loop cadence
}
