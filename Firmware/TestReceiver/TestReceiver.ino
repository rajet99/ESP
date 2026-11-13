/**
 * Caltech ME72 Dredge Challenge — Test Receiver Node
 * Target: ESP32-WROOM-32U
 * 
 * Functionality:
 * 1. Wireless ESP-NOW packet reception (Sync: 0xAA55, CRC-16 Check).
 * 2. LED blink frequency modulation based on Bot 1 Left Throttle.
 *    - Off at neutral (1500 us +/- 25 us deadband)
 *    - Freq = |us - 1500| * 10/500 Hz
 */

#include <esp_now.h>
#include <WiFi.h>
#include <esp_wifi.h>

#define STATUS_LED_PIN     2
#define PACKET_SIZE        36
#define SYNC_HEADER        0xAA55
#define THIS_ROBOT_ID      0x01
#define WATCHDOG_TIMEOUT_MS 200

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

volatile uint32_t lastValidRxMs = 0;
volatile int16_t targetLeftUs = 1500;
volatile uint8_t targetFlags = 0;

uint32_t lastLedToggle = 0;
bool ledState = false;

uint16_t computeCrc16(const uint8_t* data, size_t len) {
    uint16_t crc = 0xFFFF;
    for (size_t i = 0; i < len; i++) {
        crc ^= (uint16_t)(data[i] << 8);
        for (uint8_t j = 0; j < 8; j++) {
            if (crc & 0x8000) crc = (crc << 1) ^ 0x1021;
            else crc <<= 1;
        }
    }
    return crc;
}

#if ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(5, 0, 0)
void onDataRecv(const esp_now_recv_info_t *recv_info, const uint8_t *incomingData, int len)
#else
void onDataRecv(const uint8_t *mac_addr, const uint8_t *incomingData, int len)
#endif
{
    if (len != PACKET_SIZE) return;
    uint16_t expectedCrc = computeCrc16(incomingData, PACKET_SIZE - 2);
    uint16_t receivedCrc = (uint16_t)(incomingData[PACKET_SIZE - 2] | (incomingData[PACKET_SIZE - 1] << 8));
    if (expectedCrc != receivedCrc) return;

    const BroadcastFrame* frame = (const BroadcastFrame*)incomingData;
    if (frame->sync_marker == SYNC_HEADER && frame->bot1.robot_id == THIS_ROBOT_ID) {
        targetLeftUs = constrain(frame->bot1.left_thrust, 1000, 2000);
        targetFlags = frame->bot1.flags;
        lastValidRxMs = millis();
    }
}

void setup() {
    Serial.begin(115200);
    pinMode(STATUS_LED_PIN, OUTPUT);
    digitalWrite(STATUS_LED_PIN, LOW);

    WiFi.mode(WIFI_STA);
    WiFi.disconnect();
    esp_wifi_set_promiscuous(true);
    esp_wifi_set_channel(11, WIFI_SECOND_CHAN_NONE);
    esp_wifi_set_promiscuous(false);
    esp_wifi_set_protocol(WIFI_IF_STA, WIFI_PROTOCOL_LR);

    if (esp_now_init() == ESP_OK) {
        esp_now_register_recv_cb(onDataRecv);
    }
}

void loop() {
    uint32_t now = millis();
    bool failsafe = (now - lastValidRxMs > WATCHDOG_TIMEOUT_MS) || (targetFlags & 0x01);

    if (failsafe) {
        digitalWrite(STATUS_LED_PIN, (now / 100) % 2 == 0 ? HIGH : LOW);
    } else {
        int16_t diff = abs(targetLeftUs - 1500);
        if (diff < 25) {
            digitalWrite(STATUS_LED_PIN, LOW);
        } else {
            // Freq = diff * 10 / 500 Hz
            float freq = (float)diff * 0.02f;
            uint32_t intervalMs = (uint32_t)(500.0f / freq);
            
            if (now - lastLedToggle >= intervalMs) {
                ledState = !ledState;
                digitalWrite(STATUS_LED_PIN, ledState);
                lastLedToggle = now;
            }
        }
    }
}
