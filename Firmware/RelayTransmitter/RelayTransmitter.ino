/**
 * Caltech ME72 Dredge Challenge — Transmitter Relay Node Firmware
 * Target: ESP32-WROOM-32U (External 2.4 GHz Antenna)
 * 
 * Functions:
 * 1. Reads 36-byte binary frames from Laptop over Serial at 921,600 baud.
 * 2. Validates sync header (0xAA55) and CRC-16.
 * 3. Broadcasts frame over ESP-NOW pinned to Channel 11 in Long Range (LR) mode.
 */

#include <esp_now.h>
#include <WiFi.h>
#include <esp_wifi.h>

#define STATUS_LED_PIN   2     // Built-in Blue LED
#define SERIAL_BAUD_RATE 921600
#define PACKET_SIZE      36
#define SYNC_HEADER      0xAA55

// Global broadcast peer MAC (all FF's)
uint8_t broadcastAddress[] = {0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF};

// Exact struct layout matching C# .NET PacketProtocol
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

uint8_t rxBuffer[PACKET_SIZE];
size_t rxIndex = 0;
uint32_t packetsSent = 0;
uint32_t lastLedToggle = 0;

void setup() {
    pinMode(STATUS_LED_PIN, OUTPUT);
    digitalWrite(STATUS_LED_PIN, LOW);

    // Initialize high-speed UART (1024-byte software RX buffer, must be set before begin)
    Serial.setRxBufferSize(1024);
    Serial.begin(SERIAL_BAUD_RATE);

    // Initialize Wi-Fi in Station mode
    WiFi.mode(WIFI_STA);
    WiFi.disconnect();

    // Pin Wi-Fi channel to Channel 11
    esp_wifi_set_promiscuous(true);
    esp_wifi_set_channel(11, WIFI_SECOND_CHAN_NONE);
    esp_wifi_set_promiscuous(false);

    // Set 802.11 Long Range (LR) protocol mode for superior water surface immunity
    esp_wifi_set_protocol(WIFI_IF_STA, WIFI_PROTOCOL_LR);

    // Initialize ESP-NOW
    if (esp_now_init() != ESP_OK) {
        while (1) {
            digitalWrite(STATUS_LED_PIN, HIGH);
            delay(100);
            digitalWrite(STATUS_LED_PIN, LOW);
            delay(100);
        }
    }

    // Register broadcast peer
    esp_now_peer_info_t peerInfo = {};
    memcpy(peerInfo.peer_addr, broadcastAddress, 6);
    peerInfo.channel = 11;
    peerInfo.encrypt = false;
    esp_now_add_peer(&peerInfo);

    digitalWrite(STATUS_LED_PIN, HIGH);
    delay(500);
    digitalWrite(STATUS_LED_PIN, LOW);
}

void loop() {
    while (Serial.available() > 0) {
        uint8_t b = Serial.read();

        // State Machine: Hunt for 0x55 0xAA (Little-Endian for 0xAA55)
        if (rxIndex == 0) {
            if (b == 0x55) {
                rxBuffer[0] = b;
                rxIndex = 1;
            }
        } else if (rxIndex == 1) {
            if (b == 0xAA) {
                rxBuffer[1] = b;
                rxIndex = 2;
            } else if (b == 0x55) {
                rxIndex = 1; // Stay at index 1 if repeated 0x55
            } else {
                rxIndex = 0;
            }
        } else {
            // Read remaining payload bytes
            rxBuffer[rxIndex++] = b;

            if (rxIndex >= PACKET_SIZE) {
                // Verify CRC-16 (over bytes 0 to PACKET_SIZE - 3)
                uint16_t expectedCrc = computeCrc16(rxBuffer, PACKET_SIZE - 2);
                uint16_t receivedCrc = (uint16_t)(rxBuffer[PACKET_SIZE - 2] | (rxBuffer[PACKET_SIZE - 1] << 8));

                if (expectedCrc == receivedCrc) {
                    // Valid frame: Broadcast over ESP-NOW immediately (< 250 us airtime)
                    esp_now_send(broadcastAddress, rxBuffer, PACKET_SIZE);
                    packetsSent++;

                    // Heartbeat flash (toggles every 25 packets = ~0.5s at 50Hz)
                    if (packetsSent % 25 == 0) {
                        digitalWrite(STATUS_LED_PIN, !digitalRead(STATUS_LED_PIN));
                    }
                }

                // Reset for next frame
                rxIndex = 0;
            }
        }
    }
}
