/**
 * Caltech ME72 Dredge Challenge — Single Motor Bench Test Firmware
 * Target: ESP32-WROOM-32U
 *
 * Purpose: Standalone ESC/motor verification with NO ground station dependency.
 * Outputs a constant low-throttle pulse on GPIO 18 (Left ESC signal, matches
 * RobotReceiver pinout) using the same 50 Hz LEDC PWM scheme.
 *
 * Wiring:
 * - GPIO 18: ESC Signal (white)
 * - GND:     ESC Signal Ground (black)
 * - ESC BEC red wire: CLIPPED / INSULATED — do NOT connect (USB powers the ESP32)
 * - GPIO 2:  Status LED
 * - Power: ESP32 via USB ONLY (no buck converter simultaneously)
 *
 * Sequence (auto-terminating, prop-safe):
 * 1. NEUTRAL_HOLD: 1500 us for STARTUP_DELAY_MS (LED slow blink) — ESC arming window
 * 2. RAMP_UP:      slew-limited ramp to TARGET_US (LED fast blink)
 * 3. RUN:          constant TARGET_US for RUN_DURATION_MS (LED solid)
 * 4. RAMP_DOWN:    slew-limited ramp back to 1500 us
 * 5. IDLE:         permanent neutral (LED double-blink heartbeat). Re-power or press EN to restart.
 *
 * NOTE: No LED required — all state transitions are printed over USB Serial (115200).
 * Wire an optional LED + 330R from GPIO 2 to GND only if you want visual indication.
 *
 * Safety:
 * - Prop attached: clamp motor firmly, stand clear during arming, loop key in hand.
 * - If the motor twitches during NEUTRAL_HOLD, disconnect the battery immediately.
 */

#include <esp_idf_version.h>

#define ESC_PIN           18
#define STATUS_LED_PIN    2
#define TARGET_US         2000     // ~14% forward throttle (1500..2000)
#define STARTUP_DELAY_MS  5000     // Neutral hold after boot (arming window)
#define RUN_DURATION_MS   10000    // Thrust window before auto-stop
#define RAMP_US_PER_CYCLE 10       // Max pulse change per 20 ms cycle

#define PWM_FREQ          50       // 50 Hz RC Servo standard
#define PWM_RES_BITS      16       // 16-bit resolution (0..65535 per 20 ms)

enum TestState {
    NEUTRAL_HOLD,
    RAMP_UP,
    RUN,
    RAMP_DOWN,
    IDLE
};

TestState state = NEUTRAL_HOLD;
uint32_t stateEnteredMs = 0;
uint32_t lastCycleMs = 0;
uint32_t lastAnnounceMs = 0;
int16_t currentUs = 1500;

void enterState(TestState next) {
    state = next;
    stateEnteredMs = millis();
    lastAnnounceMs = millis();
}

// Convert microsecond pulse width (1000..2000 us) to 16-bit LEDC duty cycle
// At 50 Hz, period = 20,000 us -> Duty = (us / 20000) * 65535
uint32_t usToDuty(int16_t us) {
    us = constrain(us, 1000, 2000);
    return (uint32_t)(((uint64_t)us * 65535ULL) / 20000ULL);
}

void writeEscPwm(int16_t us) {
#if defined(ESP_ARDUINO_VERSION_MAJOR) && (ESP_ARDUINO_VERSION_MAJOR >= 3)
    ledcWrite(ESC_PIN, usToDuty(us));
#else
    ledcWrite(0, usToDuty(us));
#endif
}

void setup() {
    pinMode(STATUS_LED_PIN, OUTPUT);
    digitalWrite(STATUS_LED_PIN, LOW);

    // Initialize 50 Hz PWM on ESC signal pin
#if defined(ESP_ARDUINO_VERSION_MAJOR) && (ESP_ARDUINO_VERSION_MAJOR >= 3)
    ledcAttach(ESC_PIN, PWM_FREQ, PWM_RES_BITS);
#else
    ledcSetup(0, PWM_FREQ, PWM_RES_BITS);
    ledcAttachPin(ESC_PIN, 0);
#endif

    // Immediate neutral pulse so the ESC arms silently
    writeEscPwm(1500);

    Serial.begin(115200);
    Serial.println();
    Serial.println("[SingleMotorTest] Boot: neutral hold, ESC arming window");
    enterState(NEUTRAL_HOLD);
}

void loop() {
    uint32_t now = millis();

    // 50 Hz control cadence
    if (now - lastCycleMs < 20) return;
    lastCycleMs = now;

    switch (state) {
        case NEUTRAL_HOLD: {
            writeEscPwm(1500);
            // Slow blink (2 Hz) during arming window
            digitalWrite(STATUS_LED_PIN, (now / 250) % 2 == 0 ? HIGH : LOW);
            // Per-second countdown (no LED required to follow the test)
            if (now - lastAnnounceMs >= 1000) {
                uint32_t remain = (STARTUP_DELAY_MS - (now - stateEnteredMs) + 999) / 1000;
                Serial.print("[SingleMotorTest] Arming, neutral in 1500 us ... ");
                Serial.print((int)remain);
                Serial.println(" s");
                lastAnnounceMs = now;
            }
            if (now - stateEnteredMs >= STARTUP_DELAY_MS) {
                Serial.println("[SingleMotorTest] Arming complete: ramping up");
                enterState(RAMP_UP);
            }
            break;
        }

        case RAMP_UP:
            // Slew-limited ramp toward target
            currentUs += constrain(TARGET_US - currentUs, -RAMP_US_PER_CYCLE, RAMP_US_PER_CYCLE);
            writeEscPwm(currentUs);
            // Fast blink (5 Hz) while ramping
            digitalWrite(STATUS_LED_PIN, (now / 100) % 2 == 0 ? HIGH : LOW);
            if (currentUs >= TARGET_US) {
                Serial.print("[SingleMotorTest] RUN at ");
                Serial.print(currentUs);
                Serial.println(" us");
                enterState(RUN);
            }
            break;

        case RUN: {
            writeEscPwm(TARGET_US);
            digitalWrite(STATUS_LED_PIN, HIGH);  // Solid = thrust active
            // Per-second countdown of the thrust window
            if (now - lastAnnounceMs >= 1000) {
                uint32_t remain = (RUN_DURATION_MS - (now - stateEnteredMs) + 999) / 1000;
                Serial.print("[SingleMotorTest] Thrust at ");
                Serial.print((int)TARGET_US);
                Serial.print(" us ... ");
                Serial.print((int)remain);
                Serial.println(" s remaining");
                lastAnnounceMs = now;
            }
            if (now - stateEnteredMs >= RUN_DURATION_MS) {
                Serial.println("[SingleMotorTest] Run complete: ramping down");
                enterState(RAMP_DOWN);
            }
            break;
        }

        case RAMP_DOWN:
            currentUs += constrain(1500 - currentUs, -RAMP_US_PER_CYCLE, RAMP_US_PER_CYCLE);
            writeEscPwm(currentUs);
            digitalWrite(STATUS_LED_PIN, (now / 100) % 2 == 0 ? HIGH : LOW);
            if (currentUs <= 1500) {
                Serial.println("[SingleMotorTest] IDLE: neutral until re-power");
                enterState(IDLE);
            }
            break;

        case IDLE: {
            writeEscPwm(1500);
            // Double-blink heartbeat (~0.5 Hz)
            uint32_t phase = (now / 1000) % 2;
            digitalWrite(STATUS_LED_PIN, phase == 0 && (now / 125) % 2 == 0 ? HIGH : LOW);
            break;
        }
    }
}
