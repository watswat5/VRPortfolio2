// WiFiProbe v1 prototype firmware for Arduino-ESP32.
// Hardware capabilities remain subject to the verification gates in AGENTS.md.

#include <Arduino.h>
#include <WiFi.h>
#include <esp_system.h>
#include <esp_timer.h>
#include <esp_wifi.h>
#include <errno.h>
#include <math.h>
#include <string.h>

#if __has_include(<soc/soc_caps.h>)
#include <soc/soc_caps.h>
#endif

// Select SERIAL, BLE, or BOTH in the sketch build flags. BOTH keeps the USB
// development console available while allowing the Quest client to use BLE.
#define WIFI_PROBE_TRANSPORT_SERIAL 1
#define WIFI_PROBE_TRANSPORT_BLE 2
#define WIFI_PROBE_TRANSPORT_BOTH 3
#ifndef WIFI_PROBE_TRANSPORT
#define WIFI_PROBE_TRANSPORT WIFI_PROBE_TRANSPORT_BOTH
#endif

#if WIFI_PROBE_TRANSPORT != WIFI_PROBE_TRANSPORT_SERIAL && \
    WIFI_PROBE_TRANSPORT != WIFI_PROBE_TRANSPORT_BLE && \
    WIFI_PROBE_TRANSPORT != WIFI_PROBE_TRANSPORT_BOTH
#error "WIFI_PROBE_TRANSPORT must be SERIAL, BLE, or BOTH"
#endif

#if WIFI_PROBE_TRANSPORT == WIFI_PROBE_TRANSPORT_SERIAL || WIFI_PROBE_TRANSPORT == WIFI_PROBE_TRANSPORT_BOTH
#define PROBE_SERIAL_ENABLED 1
#else
#define PROBE_SERIAL_ENABLED 0
#endif

#if WIFI_PROBE_TRANSPORT == WIFI_PROBE_TRANSPORT_BLE || WIFI_PROBE_TRANSPORT == WIFI_PROBE_TRANSPORT_BOTH
#define PROBE_BLE_ENABLED 1
#include <BLEDevice.h>
#include <BLEServer.h>
#include <BLESecurity.h>
#include <BLEUtils.h>
#if defined(CONFIG_BLUEDROID_ENABLED) && CONFIG_BLUEDROID_ENABLED
#include <BLE2902.h>
#endif
#else
#define PROBE_BLE_ENABLED 0
#endif

// Protocol 1.1 adds BUZZ/BUZZ_OFF, button state (SAMPLE/STATUS buttons= and BUTTONS events) and CAPS
// buzzer=/buttons=. Each link negotiates the lower of the host's requested minor and ours, so a 1.0 host sees
// exactly the 1.0 protocol (none of the additions), and a 1.1 host talking to 1.0 firmware is told protocol=1.0.
// Protocol 1.2 adds SELECT ... group=ssid: every access point of the chosen one's network (same SSID) on its channel
// is measured (an institution's network has dozens of APs), and each SAMPLE reports the strongest of them over its
// window, as a device roaming on that network would connect to it. SELECTED and STATUS then say aps=<n>, a 1.2 SAMPLE
// adds from=<bssid> (the radio it reports) and heard=<n> (how many were heard), and CAPS select_group="ssid".
#define PROTOCOL_MAJOR 1
#define PROTOCOL_MINOR_MAX 2
#define FIRMWARE_VERSION "0.4.0"
#define MAX_LINE 512
#define MAX_TOKENS 8
#define PROTOCOL_OUTPUT_CAPACITY 768
#define BLE_RX_QUEUE_CAPACITY 1024
// Notification payload: the ATT MTU the central negotiated, minus the 3-byte header. A central that never asks
// for a larger MTU keeps the BLE default of 23, so older apps still get 20-byte notifications; newer ones request
// 247 and get up to 244, about 12x fewer packets for a long SCAN reply.
#define BLE_NOTIFICATION_CHUNK 20
#define BLE_NOTIFICATION_CHUNK_MAX 244
// A notification the stack can't queue (its transmit buffers are full) is retried after a short wait
// rather than dropped, for at most this long per chunk.
#define BLE_NOTIFY_RETRY_MS 500
#define DEFAULT_INTERVAL_MS 100
#define MIN_INTERVAL_MS 20
#define MAX_INTERVAL_MS 60000
#define MAX_BUZZ_MS 10000
#define DEFAULT_BUZZ_HZ 4000
#define MIN_BUZZ_HZ 100
#define MAX_BUZZ_HZ 10000
#define BUTTON_DEBOUNCE_MS 20

// Board extras: power hold, a passive buzzer and two buttons. The M5StickC Plus2 (board m5stack_stickc_plus2) gets
// M5Stack's published pins; any board can set them as build flags. -1 leaves a feature off.
#if defined(ARDUINO_M5STACK_STICKC_PLUS2)
#ifndef PROBE_POWER_HOLD_PIN
#define PROBE_POWER_HOLD_PIN 4    // on battery the Plus2 stays powered only while this is held high
#endif
#ifndef PROBE_BUZZER_PIN
#define PROBE_BUZZER_PIN 2
#endif
#ifndef PROBE_BUTTON_A_PIN
#define PROBE_BUTTON_A_PIN 37     // the large front button
#endif
#ifndef PROBE_BUTTON_B_PIN
#define PROBE_BUTTON_B_PIN 39     // the side button
#endif
#endif
#ifndef PROBE_POWER_HOLD_PIN
#define PROBE_POWER_HOLD_PIN -1
#endif
#ifndef PROBE_BUZZER_PIN
#define PROBE_BUZZER_PIN -1
#endif
#ifndef PROBE_BUTTON_A_PIN
#define PROBE_BUTTON_A_PIN -1
#endif
#ifndef PROBE_BUTTON_B_PIN
#define PROBE_BUTTON_B_PIN -1
#endif
#ifndef PROBE_BUTTONS_ACTIVE_LOW
#define PROBE_BUTTONS_ACTIVE_LOW 1
#endif
// M5StickC Plus2 status UI: the screen through M5Stack's M5GFX library (Library Manager: "M5GFX"), plus the red LED
// and a C-button power off. On by default for that board; -DPROBE_M5_UI=0 builds without it. (M5Unified, which wraps
// M5GFX, doesn't fit: with Wi-Fi and Bluedroid BLE its speaker and power drivers overflow the ESP32's IRAM.)
#if defined(ARDUINO_M5STACK_STICKC_PLUS2) && !defined(PROBE_M5_UI)
#define PROBE_M5_UI 1
#endif
#ifndef PROBE_M5_UI
#define PROBE_M5_UI 0
#endif
#if PROBE_M5_UI
#include <M5GFX.h>
#define M5_LED_PIN 19          // the Plus2's red LED, active high
#define M5_POWER_BUTTON_PIN 35 // C: the power button, active low; also wakes it from deep sleep
#define M5_BATTERY_ADC_PIN 38  // battery voltage through a 2:1 divider
#ifndef PROBE_M5_ROTATION
#define PROBE_M5_ROTATION 3           // landscape, read on the wrist; 1 turns it the other way up
#endif
#ifndef PROBE_M5_BRIGHTNESS
#define PROBE_M5_BRIGHTNESS 96        // 0-255
#endif
#ifndef PROBE_M5_POWER_OFF_MS
#define PROBE_M5_POWER_OFF_MS 1500    // hold C (the power button) this long to switch off
#endif
#endif
#define PROBE_HAS_BUZZER (PROBE_BUZZER_PIN >= 0)
#define PROBE_HAS_BUTTONS (PROBE_BUTTON_A_PIN >= 0 || PROBE_BUTTON_B_PIN >= 0)
#define BUTTON_A_BIT 1
#define BUTTON_B_BIT 2
#define STRINGIFY_INNER(x) #x
#define STR(x) STRINGIFY_INNER(x)

// Set this to a free output-capable GPIO to enable the pairing indicator.
// -1 leaves the feature disabled. An external LED must have a series resistor.
#ifndef BLE_PAIRING_LED_PIN
#define BLE_PAIRING_LED_PIN -1
#endif
#ifndef BLE_PAIRING_LED_ACTIVE_HIGH
#define BLE_PAIRING_LED_ACTIVE_HIGH 1
#endif

// This prototype passkey must be changed for field devices. The phone/Quest
// enters the six-digit value; it is not encoded by the single status LED.
#ifndef BLE_PAIRING_PASSKEY
#define BLE_PAIRING_PASSKEY 123456
#endif

#ifndef BLE_DEVICE_NAME
#define BLE_DEVICE_NAME "WiFiProbe-C5"
#endif

#if PROBE_BLE_ENABLED
static_assert(BLE_PAIRING_PASSKEY >= 0 && BLE_PAIRING_PASSKEY <= 999999,
              "BLE_PAIRING_PASSKEY must contain at most six digits");
#endif

#if defined(SOC_WIFI_SUPPORT_5G) && SOC_WIFI_SUPPORT_5G
#define PROBE_HAS_5G 1
#else
#define PROBE_HAS_5G 0
#endif

// Nordic UART Service-compatible UUIDs. WiFiProbe framing remains newline-
// delimited text; BLE notifications may split a line into 20-byte chunks.
#define BLE_SERVICE_UUID "6E400001-B5A3-F393-E0A9-E50E24DCCA9E"
#define BLE_RX_UUID      "6E400002-B5A3-F393-E0A9-E50E24DCCA9E"
#define BLE_TX_UUID      "6E400003-B5A3-F393-E0A9-E50E24DCCA9E"

enum class ProbeState { Boot, Idle, Scanning, Selected, Streaming, Error };
enum class Transport : uint8_t { Serial, Ble };
enum class PairingLedState : uint8_t { Off, Advertising, Pairing, Paired, Failed };

struct Aggregate {
  uint64_t frames;
  double mean;
  double m2;
  int8_t minimum;
  int8_t maximum;
  uint64_t firstUs;
  uint64_t lastUs;
};

struct LineFramer {
  char bytes[MAX_LINE + 1];
  size_t length;
  bool discardingOversize;
};

static ProbeState state = ProbeState::Boot;
static bool serialNegotiated = false;
static bool bleNegotiated = false;
static uint8_t serialMinor = 0;   // negotiated protocol minor per link (meaningful once negotiated)
static uint8_t bleMinor = 0;
static uint8_t buttonState = 0;          // debounced BUTTON_A_BIT | BUTTON_B_BIT
static uint8_t buttonCandidate = 0;
static uint32_t buttonCandidateSinceMs = 0;
static bool buzzing = false;
// What the M5 screen shows: the measured network's name (remembered from the last scan) and the latest reading.
// It also holds the radios a group=ssid SELECT gathers, so it keeps a large scan whole.
#define SCAN_MEMORY 128
#define MAX_GROUP_APS 64
struct ScannedAp { uint8_t bssid[6]; uint8_t channel; char ssid[33]; };
static ScannedAp scanMemory[SCAN_MEMORY];
static int scanMemoryCount = 0;
static int lastScanCount = -1;
static char selectedSsid[33] = "";
static int lastSampleRssi = 0;
static uint32_t lastSampleMs = 0;
static bool haveSample = false;
static void statusChanged();   // redraws the M5 screen at once (a no-op on other boards)
static uint32_t buzzStartMs = 0;
static uint32_t buzzDurationMs = 0;
static Transport currentCommandTransport = Transport::Serial;
static Transport streamTransport = Transport::Serial;
static uint64_t bootId = 0;
static uint64_t sessionId = 0;
static uint64_t sequenceNumber = 0;
static uint64_t reports = 0;
static uint64_t dropped = 0;
static uint64_t transportWriteFailures = 0;
static uint8_t selectedBssid[6] = {};          // the chosen radio, reported in SAMPLE bssid=
static uint8_t selectedBssids[MAX_GROUP_APS][6] = {};   // every radio measured: the chosen one first
static int selectedBssidCount = 0;
static uint8_t selectedChannel = 0;
static uint32_t reportIntervalMs = DEFAULT_INTERVAL_MS;
static uint64_t lastReportUs = 0;
static Aggregate intervalStats[MAX_GROUP_APS] = {};   // per selected radio, for the window being gathered
static Aggregate sessionStats = {};                   // every reported window's frames (the radio each reported)
#if PROBE_SERIAL_ENABLED
static LineFramer serialFramer = {};
#endif
#if PROBE_BLE_ENABLED
static LineFramer bleFramer = {};
#endif
static portMUX_TYPE statsMux = portMUX_INITIALIZER_UNLOCKED;

#if PROBE_BLE_ENABLED
static BLEServer *bleServer = nullptr;
static BLECharacteristic *bleTxCharacteristic = nullptr;
static BLESecurity *bleSecurity = nullptr;
static volatile bool bleConnected = false;
static volatile bool bleAuthenticated = false;
static volatile bool bleDisconnectPending = false;
static volatile bool bleRxOverflow = false;
static volatile PairingLedState pairingLedState = PairingLedState::Off;
static volatile uint32_t pairingFailureUntilMs = 0;
static uint8_t bleRxQueue[BLE_RX_QUEUE_CAPACITY];
static size_t bleRxHead = 0;
static size_t bleRxTail = 0;
static portMUX_TYPE bleRxMux = portMUX_INITIALIZER_UNLOCKED;
#endif

static uint64_t probeMicros() { return static_cast<uint64_t>(esp_timer_get_time()); }

static const char *transportName(Transport transport) {
  return transport == Transport::Ble ? "BLE" : "SERIAL";
}

static const char *stateName() {
  switch (state) {
    case ProbeState::Boot: return "BOOT";
    case ProbeState::Idle: return "IDLE";
    case ProbeState::Scanning: return "SCANNING";
    case ProbeState::Selected: return "SELECTED";
    case ProbeState::Streaming: return "STREAMING";
    default: return "ERROR";
  }
}

#if PROBE_BLE_ENABLED
static volatile bool bleNotifyFailed = false;

// Set from notify(), which reports ERROR_GATT synchronously when the stack refuses the notification.
class ProbeTxCallbacks final : public BLECharacteristicCallbacks {
  void onStatus(BLECharacteristic *characteristic, Status status, uint32_t code) override {
    (void)characteristic;
    (void)code;
    if (status == ERROR_GATT) bleNotifyFailed = true;
  }
};

static size_t bleNotificationChunk() {
  const uint16_t mtu = bleServer ? bleServer->getPeerMTU(bleServer->getConnId()) : 0;
  if (mtu <= BLE_NOTIFICATION_CHUNK + 3) return BLE_NOTIFICATION_CHUNK;
  return min(static_cast<size_t>(mtu - 3), static_cast<size_t>(BLE_NOTIFICATION_CHUNK_MAX));
}

static bool sendBleBytes(const uint8_t *bytes, size_t length) {
  if (!bleConnected || !bleAuthenticated || !bleTxCharacteristic) return false;
  const size_t chunkSize = bleNotificationChunk();
  for (size_t offset = 0; offset < length; offset += chunkSize) {
    const size_t chunk = min(chunkSize, length - offset);
    const uint32_t started = millis();
    for (;;) {
      bleNotifyFailed = false;
      bleTxCharacteristic->setValue(bytes + offset, chunk);
      bleTxCharacteristic->notify();
      if (!bleNotifyFailed) break;
      if (!bleConnected || millis() - started >= BLE_NOTIFY_RETRY_MS) return false;
      delay(5);   // let the stack drain its transmit buffers
    }
    // A small yield prevents a long SCAN response from monopolizing the BLE host task.
    delay(1);
  }
  return true;
}
#endif

static bool sendTransportBytes(Transport transport, const uint8_t *bytes, size_t length) {
  if (transport == Transport::Serial) {
#if PROBE_SERIAL_ENABLED
    return Serial.write(bytes, length) == length;
#else
    return false;
#endif
  }
#if PROBE_BLE_ENABLED
  return sendBleBytes(bytes, length);
#else
  return false;
#endif
}

// Print-compatible line writer used by every protocol response. Keeping full
// lines here prevents response fragments from being routed to different links.
class ProtocolPrint final : public Print {
 public:
  size_t write(uint8_t value) override {
    if (overflow_) {
      if (value == '\n') reset();
      return 1;
    }
    if (length_ >= sizeof(buffer_)) {
      overflow_ = true;
      lastFlushSucceeded_ = false;
      ++transportWriteFailures;
      return 1;
    }
    buffer_[length_++] = value;
    if (value == '\n') flushLine();
    return 1;
  }

  using Print::write;

  void routeTo(Transport transport) { transport_ = transport; }
  bool lastFlushSucceeded() const { return lastFlushSucceeded_; }

 private:
  void flushLine() {
    lastFlushSucceeded_ = sendTransportBytes(transport_, buffer_, length_);
    if (!lastFlushSucceeded_) ++transportWriteFailures;
    length_ = 0;
    overflow_ = false;
  }

  void reset() {
    length_ = 0;
    overflow_ = false;
  }

  Transport transport_ = Transport::Serial;
  uint8_t buffer_[PROTOCOL_OUTPUT_CAPACITY];
  size_t length_ = 0;
  bool overflow_ = false;
  bool lastFlushSucceeded_ = true;
};

static ProtocolPrint protocolOut;

static void clearAggregate(Aggregate &value) {
  value.frames = 0;
  value.mean = 0;
  value.m2 = 0;
  value.minimum = 127;
  value.maximum = -128;
  value.firstUs = 0;
  value.lastUs = 0;
}

static void addRssi(Aggregate &value, int8_t rssi, uint64_t observedUs) {
  if (!value.frames) value.firstUs = observedUs;
  value.lastUs = observedUs;
  value.frames++;
  const double delta = rssi - value.mean;
  value.mean += delta / static_cast<double>(value.frames);
  value.m2 += delta * (rssi - value.mean);
  if (rssi < value.minimum) value.minimum = rssi;
  if (rssi > value.maximum) value.maximum = rssi;
}

static void clearIntervals() {
  for (int i = 0; i < MAX_GROUP_APS; ++i) clearAggregate(intervalStats[i]);
}

// Adds b's frames to a (Chan et al.'s pairwise update of mean and M2).
static void mergeAggregate(Aggregate &a, const Aggregate &b) {
  if (!b.frames) return;
  if (!a.frames) { a = b; return; }
  const double n = static_cast<double>(a.frames + b.frames);
  const double delta = b.mean - a.mean;
  a.m2 += b.m2 + delta * delta * static_cast<double>(a.frames) * static_cast<double>(b.frames) / n;
  a.mean += delta * static_cast<double>(b.frames) / n;
  a.frames += b.frames;
  if (b.minimum < a.minimum) a.minimum = b.minimum;
  if (b.maximum > a.maximum) a.maximum = b.maximum;
  if (b.firstUs < a.firstUs) a.firstUs = b.firstUs;
  if (b.lastUs > a.lastUs) a.lastUs = b.lastUs;
}

static double standardDeviation(const Aggregate &value) {
  return value.frames > 1 ? sqrt(value.m2 / static_cast<double>(value.frames)) : 0.0;
}

static void printU64(uint64_t value) {
  protocolOut.printf("%llu", static_cast<unsigned long long>(value));
}

static void printMac(const uint8_t *mac) {
  protocolOut.printf("%02X:%02X:%02X:%02X:%02X:%02X", mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
}

static void printQuoted(const uint8_t *bytes, size_t length) {
  protocolOut.print('"');
  for (size_t i = 0; i < length; ++i) {
    const uint8_t c = bytes[i];
    switch (c) {
      case '"': protocolOut.print("\\\""); break;
      case '\\': protocolOut.print("\\\\"); break;
      case '\b': protocolOut.print("\\b"); break;
      case '\f': protocolOut.print("\\f"); break;
      case '\n': protocolOut.print("\\n"); break;
      case '\r': protocolOut.print("\\r"); break;
      case '\t': protocolOut.print("\\t"); break;
      default:
        if (c < 0x20) protocolOut.printf("\\u%04X", c);
        else protocolOut.write(c);
    }
  }
  protocolOut.print('"');
}

static void errorResponse(uint64_t id, const char *code, const char *message) {
  protocolOut.print("ERR "); printU64(id); protocolOut.print(' '); protocolOut.print(code); protocolOut.print(' ');
  printQuoted(reinterpret_cast<const uint8_t *>(message), strlen(message)); protocolOut.println();
}

static bool parseU64(const char *text, uint64_t &value) {
  if (!text || !*text || *text == '-') return false;
  char *end = nullptr;
  errno = 0;
  unsigned long long parsed = strtoull(text, &end, 10);
  if (errno || !end || *end) return false;
  value = static_cast<uint64_t>(parsed);
  return true;
}

static bool parseMac(const char *text, uint8_t out[6]) {
  if (!text || strlen(text) != 17) return false;
  for (int i = 0; i < 6; ++i) {
    const int offset = i * 3;
    char part[3] = {text[offset], text[offset + 1], 0};
    char *end = nullptr;
    long value = strtol(part, &end, 16);
    if (!end || *end || value < 0 || value > 255) return false;
    if (i < 5 && text[offset + 2] != ':') return false;
    out[i] = static_cast<uint8_t>(value);
  }
  return true;
}

static int tokenize(char *line, char *tokens[], int capacity) {
  int count = 0;
  char *cursor = line;
  while (*cursor) {
    while (*cursor == ' ' || *cursor == '\t') ++cursor;
    if (!*cursor) break;
    if (count == capacity) return -1;
    tokens[count++] = cursor;
    while (*cursor && *cursor != ' ' && *cursor != '\t') ++cursor;
    if (*cursor) *cursor++ = 0;
  }
  return count;
}

static bool isCurrentTransportNegotiated() {
  return currentCommandTransport == Transport::Ble ? bleNegotiated : serialNegotiated;
}

static void markCurrentTransportNegotiated(uint8_t minor) {
  if (currentCommandTransport == Transport::Ble) { bleNegotiated = true; bleMinor = minor; }
  else { serialNegotiated = true; serialMinor = minor; }
}

// The protocol minor a negotiated link agreed on; 1.1 additions are only ever sent on links at minor >= 1.
static uint8_t transportMinor(Transport transport) {
  return transport == Transport::Ble ? (bleNegotiated ? bleMinor : 0) : (serialNegotiated ? serialMinor : 0);
}

static bool linkSupports11(Transport transport) { return transportMinor(transport) >= 1; }
static bool linkSupports12(Transport transport) { return transportMinor(transport) >= 2; }

static bool requireNegotiated(uint64_t id) {
  if (isCurrentTransportNegotiated()) return true;
  errorResponse(id, "NOT_NEGOTIATED", "HELLO must succeed first");
  return false;
}

static bool isSupportedChannel(uint64_t id, uint64_t channel) {
  if (channel >= 1 && channel <= 14) return true;
#if PROBE_HAS_5G
  if (channel >= 32 && channel <= 177) return true;
#endif
  errorResponse(id, "UNSUPPORTED_CHANNEL", "channel is not supported by this build");
  return false;
}

static const char *securityName(wifi_auth_mode_t mode) {
  switch (mode) {
    case WIFI_AUTH_OPEN: return "OPEN";
    case WIFI_AUTH_WEP: return "WEP";
    case WIFI_AUTH_WPA_PSK: return "WPA";
    case WIFI_AUTH_WPA2_PSK: return "WPA2";
    case WIFI_AUTH_WPA_WPA2_PSK: return "WPA/WPA2";
    default: return "UNKNOWN";
  }
}

static void stopMonitor() {
  esp_wifi_set_promiscuous(false);
}

// Which selected radio a frame is to or from (its index in selectedBssids), or -1.
static int selectedRadioOf(const uint8_t *payload, uint16_t length) {
  if (length < 24) return -1;
  for (int i = 0; i < selectedBssidCount; ++i) {
    const uint8_t *bssid = selectedBssids[i];
    if (memcmp(payload + 4, bssid, 6) == 0 || memcmp(payload + 10, bssid, 6) == 0 || memcmp(payload + 16, bssid, 6) == 0)
      return i;
  }
  return -1;
}

static void promiscuousCallback(void *buffer, wifi_promiscuous_pkt_type_t type) {
  if (state != ProbeState::Streaming || type == WIFI_PKT_MISC) return;
  const wifi_promiscuous_pkt_t *packet = static_cast<wifi_promiscuous_pkt_t *>(buffer);
  if (packet->rx_ctrl.channel != selectedChannel) return;
  const int radio = selectedRadioOf(packet->payload, packet->rx_ctrl.sig_len);
  if (radio < 0) return;
  const uint64_t observedUs = probeMicros();
  portENTER_CRITICAL(&statsMux);
  addRssi(intervalStats[radio], packet->rx_ctrl.rssi, observedUs);
  portEXIT_CRITICAL(&statsMux);
}

static void emitSampleIfDue() {
  if (state != ProbeState::Streaming) return;
  const uint64_t now = probeMicros();
  if (now - lastReportUs < static_cast<uint64_t>(reportIntervalMs) * 1000ULL) return;
  lastReportUs = now;
  // The window's reading is the strongest radio heard in it (by its mean), as a device on that network would pick.
  Aggregate snapshot;
  int best = -1, heard = 0;
  portENTER_CRITICAL(&statsMux);
  for (int i = 0; i < selectedBssidCount; ++i) {
    if (!intervalStats[i].frames) continue;
    ++heard;
    if (best < 0 || intervalStats[i].mean > intervalStats[best].mean) best = i;
  }
  if (best >= 0) { snapshot = intervalStats[best]; mergeAggregate(sessionStats, snapshot); }
  clearIntervals();
  portEXIT_CRITICAL(&statsMux);
  if (best < 0) return;
  ++sequenceNumber;
  ++reports;
  const uint64_t observationMidpointUs = snapshot.firstUs + (snapshot.lastUs - snapshot.firstUs) / 2;
  lastSampleRssi = static_cast<int>(lround(snapshot.mean));
  lastSampleMs = millis();
  haveSample = true;
  protocolOut.routeTo(streamTransport);
  protocolOut.print("SAMPLE session="); printU64(sessionId);
  protocolOut.print(" seq="); printU64(sequenceNumber);
  protocolOut.print(" probe_us="); printU64(observationMidpointUs);
  protocolOut.print(" bssid="); printMac(selectedBssid);
  protocolOut.printf(" channel=%u rssi=%d frames=", selectedChannel, static_cast<int>(lround(snapshot.mean)));
  printU64(snapshot.frames);
  protocolOut.printf(" mean=%.2f min=%d max=%d stddev=%.2f window_start_us=", snapshot.mean, snapshot.minimum, snapshot.maximum, standardDeviation(snapshot));
  printU64(snapshot.firstUs); protocolOut.print(" window_end_us="); printU64(snapshot.lastUs);
#if PROBE_HAS_BUTTONS
  if (linkSupports11(streamTransport)) protocolOut.printf(" buttons=%u", buttonState);
#endif
  if (linkSupports12(streamTransport)) { protocolOut.print(" from="); printMac(selectedBssids[best]); protocolOut.printf(" heard=%d", heard); }
  protocolOut.println();
  if (!protocolOut.lastFlushSucceeded()) ++dropped;
}

static void handleHello(uint64_t id, char *tokens[], int count) {
  if (count != 3) { errorResponse(id, "BAD_ARGS", "usage: HELLO <id> <host_protocol>"); return; }
  const char *dot = strchr(tokens[2], '.');
  const size_t majorLength = dot ? static_cast<size_t>(dot - tokens[2]) : strlen(tokens[2]);
  if (majorLength != 1 || tokens[2][0] != '0' + PROTOCOL_MAJOR) { errorResponse(id, "INCOMPATIBLE_PROTOCOL", "supported protocol major is 1"); return; }
  // Any 1.x host is accepted (as in 1.0); it gets the lower of its minor and ours. A missing or unreadable minor
  // counts as 0, the earliest protocol.
  uint64_t hostMinor = 0;
  if (!dot || !parseU64(dot + 1, hostMinor)) hostMinor = 0;
  const uint8_t minor = hostMinor < PROTOCOL_MINOR_MAX ? static_cast<uint8_t>(hostMinor) : PROTOCOL_MINOR_MAX;
  markCurrentTransportNegotiated(minor);
  if (state == ProbeState::Boot) state = ProbeState::Idle;
  protocolOut.print("HELLO "); printU64(id);
  protocolOut.printf(" protocol=%d.%u", PROTOCOL_MAJOR, minor);
  protocolOut.print(" device=\"WiFiProbe\" fw=\"" FIRMWARE_VERSION "\" boot="); printU64(bootId); protocolOut.println();
}

static void handleCaps(uint64_t id, int count) {
  if (count != 2) { errorResponse(id, "BAD_ARGS", "usage: CAPS <id>"); return; }
  protocolOut.print("CAPS "); printU64(id);
#if PROBE_HAS_5G
  protocolOut.print(" bands=2.4,5 channels=\"runtime-validated\"");
#else
  protocolOut.print(" bands=2.4 channels=\"1-14-runtime-validated\"");
#endif
  protocolOut.print(" monitor=true");
#if WIFI_PROBE_TRANSPORT == WIFI_PROBE_TRANSPORT_BOTH
  protocolOut.print(" transports=\"serial,ble\" usb=\"arduino-serial\"");
#elif WIFI_PROBE_TRANSPORT == WIFI_PROBE_TRANSPORT_BLE
  protocolOut.print(" transports=\"ble\" usb=\"disabled\"");
#else
  protocolOut.print(" transports=\"serial\" usb=\"arduino-serial\"");
#endif
#if PROBE_BLE_ENABLED
  protocolOut.print(" ble_security=\"bond-mitm-sc-passkey\"");
#endif
  protocolOut.print(" ts_hz=1000000 max_line=" STR(MAX_LINE));
  protocolOut.print(" scan_while_streaming=false aggregation=\"arithmetic-dbm\"");
  if (linkSupports11(currentCommandTransport)) {
    protocolOut.printf(" protocol_max=%d.%d buzzer=%s", PROTOCOL_MAJOR, PROTOCOL_MINOR_MAX, PROBE_HAS_BUZZER ? "true" : "false");
    protocolOut.print(" buttons=\"");
    bool first = true;
    if (PROBE_BUTTON_A_PIN >= 0) { protocolOut.print("A"); first = false; }
    if (PROBE_BUTTON_B_PIN >= 0) { if (!first) protocolOut.print(','); protocolOut.print("B"); }
    protocolOut.print('"');
  }
  if (linkSupports12(currentCommandTransport)) protocolOut.print(" select_group=\"ssid\" group_reading=\"strongest-ap\"");
  protocolOut.println();
}

static void handleScan(uint64_t id, char *tokens[], int count) {
  if (state != ProbeState::Idle && state != ProbeState::Selected) { errorResponse(id, "BAD_STATE", "SCAN requires IDLE or SELECTED"); return; }
  const char *band = "all";
  if (count == 3 && strncmp(tokens[2], "band=", 5) == 0) band = tokens[2] + 5;
  else if (count != 2) { errorResponse(id, "BAD_ARGS", "usage: SCAN <id> [band=2.4|5|all]"); return; }
  if (strcmp(band, "all") && strcmp(band, "2.4") && strcmp(band, "5")) { errorResponse(id, "BAD_ARGS", "invalid band"); return; }
#if !PROBE_HAS_5G
  if (!strcmp(band, "5")) { errorResponse(id, "UNSUPPORTED_BAND", "5 GHz is not supported by this build"); return; }
#endif
  state = ProbeState::Scanning;
  statusChanged();   // the scan blocks for seconds; show it first
  const int found = WiFi.scanNetworks(false, true);
  if (found < 0) { state = ProbeState::Idle; errorResponse(id, "SCAN_FAILED", "Wi-Fi scan failed"); return; }
  int emitted = 0;
  scanMemoryCount = 0;
  for (int i = 0; i < found; ++i) {
    const int channel = WiFi.channel(i);
    const bool is5 = channel > 14;
    if ((!strcmp(band, "2.4") && is5) || (!strcmp(band, "5") && !is5)) continue;
    protocolOut.print("AP "); printU64(id); protocolOut.print(" bssid="); printMac(WiFi.BSSID(i));
    protocolOut.printf(" channel=%d band=%s rssi=%ld ssid=", channel, is5 ? "5" : "2.4", static_cast<long>(WiFi.RSSI(i)));
    const String ssid = WiFi.SSID(i);
    printQuoted(reinterpret_cast<const uint8_t *>(ssid.c_str()), ssid.length());
    if (scanMemoryCount < SCAN_MEMORY) {
      memcpy(scanMemory[scanMemoryCount].bssid, WiFi.BSSID(i), 6);
      scanMemory[scanMemoryCount].channel = static_cast<uint8_t>(channel);
      strlcpy(scanMemory[scanMemoryCount].ssid, ssid.c_str(), sizeof(scanMemory[0].ssid));
      ++scanMemoryCount;
    }
    protocolOut.print(" security=\""); protocolOut.print(securityName(WiFi.encryptionType(i))); protocolOut.println('"');
    ++emitted;
  }
  WiFi.scanDelete();
  state = ProbeState::Idle;
  selectedChannel = 0;
  selectedBssidCount = 0;
  selectedSsid[0] = 0;
  lastScanCount = emitted;
  haveSample = false;
  statusChanged();
  protocolOut.print("SCAN_DONE "); printU64(id); protocolOut.print(" count="); protocolOut.println(emitted);
}

static void handleSelect(uint64_t id, char *tokens[], int count) {
  if (state != ProbeState::Idle && state != ProbeState::Selected) { errorResponse(id, "BAD_STATE", "SELECT requires IDLE or SELECTED"); return; }
  uint8_t mac[6]; uint64_t channel;
  // 1.2: group=ssid also measures the other radios of the chosen one's network on its channel.
  const bool group = count == 5 && linkSupports12(currentCommandTransport) && !strcmp(tokens[4], "group=ssid");
  if ((count != 4 && !group) || !parseMac(tokens[2], mac) || !parseU64(tokens[3], channel) || channel > 255) {
    errorResponse(id, "BAD_ARGS", linkSupports12(currentCommandTransport)
      ? "usage: SELECT <id> <bssid> <channel> [group=ssid]" : "usage: SELECT <id> <bssid> <channel>"); return;
  }
  if (!isSupportedChannel(id, channel)) return;
  if (esp_wifi_set_channel(static_cast<uint8_t>(channel), WIFI_SECOND_CHAN_NONE) != ESP_OK) {
    errorResponse(id, "RADIO_ERROR", "radio rejected channel"); return;
  }
  memcpy(selectedBssid, mac, sizeof(selectedBssid)); selectedChannel = static_cast<uint8_t>(channel); state = ProbeState::Selected;
  selectedSsid[0] = 0;
  for (int i = 0; i < scanMemoryCount; ++i)
    if (!memcmp(scanMemory[i].bssid, mac, 6)) { strlcpy(selectedSsid, scanMemory[i].ssid, sizeof(selectedSsid)); break; }
  // The group: the chosen radio, then every other remembered from the last scan with its SSID on this channel (not
  // hidden networks: an empty SSID says nothing about which network a radio belongs to).
  memcpy(selectedBssids[0], mac, 6);
  selectedBssidCount = 1;
  if (group && selectedSsid[0]) {
    for (int i = 0; i < scanMemoryCount && selectedBssidCount < MAX_GROUP_APS; ++i) {
      const ScannedAp &ap = scanMemory[i];
      if (ap.channel == selectedChannel && !strcmp(ap.ssid, selectedSsid) && memcmp(ap.bssid, mac, 6))
        memcpy(selectedBssids[selectedBssidCount++], ap.bssid, 6);
    }
  }
  haveSample = false;
  statusChanged();
  protocolOut.print("SELECTED "); printU64(id); protocolOut.print(" bssid="); printMac(selectedBssid);
  protocolOut.printf(" channel=%u band=%s", selectedChannel, selectedChannel > 14 ? "5" : "2.4");
  if (linkSupports12(currentCommandTransport)) protocolOut.printf(" aps=%d", selectedBssidCount);
  protocolOut.println();
}

static void handleStart(uint64_t id, char *tokens[], int count) {
  if (state != ProbeState::Selected) { errorResponse(id, "BAD_STATE", "START requires SELECTED"); return; }
  uint64_t interval = DEFAULT_INTERVAL_MS;
  if (count == 3 && strncmp(tokens[2], "interval_ms=", 12) == 0) {
    if (!parseU64(tokens[2] + 12, interval) || interval < MIN_INTERVAL_MS || interval > MAX_INTERVAL_MS) {
      errorResponse(id, "OUT_OF_RANGE", "interval_ms must be 20..60000"); return;
    }
  } else if (count != 2) { errorResponse(id, "BAD_ARGS", "usage: START <id> [interval_ms=<n>]"); return; }
  reportIntervalMs = static_cast<uint32_t>(interval); sequenceNumber = reports = dropped = 0;
  clearIntervals(); clearAggregate(sessionStats);
  sessionId++;
  if (!sessionId) sessionId = 1;
  esp_wifi_set_promiscuous_rx_cb(promiscuousCallback);
  if (esp_wifi_set_promiscuous(true) != ESP_OK) { state = ProbeState::Error; errorResponse(id, "RADIO_ERROR", "could not enable monitor mode"); return; }
  streamTransport = currentCommandTransport;
  state = ProbeState::Streaming; lastReportUs = probeMicros();
  protocolOut.print("STARTED "); printU64(id); protocolOut.print(" session="); printU64(sessionId); protocolOut.print(" probe_us="); printU64(lastReportUs); protocolOut.println();
}

static void handleStop(uint64_t id, int count) {
  if (count != 2) { errorResponse(id, "BAD_ARGS", "usage: STOP <id>"); return; }
  if (state == ProbeState::Streaming) { stopMonitor(); state = ProbeState::Selected; }
  else if (state != ProbeState::Selected) { errorResponse(id, "BAD_STATE", "STOP requires SELECTED or STREAMING"); return; }
  protocolOut.print("STOPPED "); printU64(id); protocolOut.print(" session="); printU64(sessionId);
  protocolOut.print(" samples="); printU64(reports); protocolOut.print(" probe_us="); printU64(probeMicros()); protocolOut.println();
}

static void handleStatus(uint64_t id, int count) {
  if (count != 2) { errorResponse(id, "BAD_ARGS", "usage: STATUS <id>"); return; }
  protocolOut.print("STATUS "); printU64(id); protocolOut.print(" state="); protocolOut.print(stateName());
  protocolOut.print(" command_transport="); protocolOut.print(transportName(currentCommandTransport));
  if (state == ProbeState::Streaming) { protocolOut.print(" stream_transport="); protocolOut.print(transportName(streamTransport)); }
  if (selectedChannel) {
    protocolOut.print(" bssid="); printMac(selectedBssid); protocolOut.printf(" channel=%u", selectedChannel);
    if (linkSupports12(currentCommandTransport)) protocolOut.printf(" aps=%d", selectedBssidCount);
  }
  if (sessionId) { protocolOut.print(" session="); printU64(sessionId); }
  protocolOut.print(" transport_write_failures="); printU64(transportWriteFailures);
  if (linkSupports11(currentCommandTransport)) {
#if PROBE_HAS_BUTTONS
    protocolOut.printf(" buttons=%u", buttonState);
#endif
    protocolOut.printf(" buzzing=%s", buzzing ? "true" : "false");
  }
  protocolOut.println();
}

static void handleStats(uint64_t id, int count) {
  if (count != 2) { errorResponse(id, "BAD_ARGS", "usage: STATS <id>"); return; }
  Aggregate snapshot;
  portENTER_CRITICAL(&statsMux); snapshot = sessionStats; portEXIT_CRITICAL(&statsMux);
  protocolOut.print("STATS "); printU64(id); protocolOut.print(" session="); printU64(sessionId);
  protocolOut.print(" frames="); printU64(snapshot.frames); protocolOut.print(" reports="); printU64(reports);
  protocolOut.print(" dropped="); printU64(dropped);
  if (snapshot.frames) protocolOut.printf(" mean=%.2f min=%d max=%d stddev=%.2f\n", snapshot.mean, snapshot.minimum, snapshot.maximum, standardDeviation(snapshot));
  else protocolOut.println(" mean=0 min=0 max=0 stddev=0");
}

static void handlePing(uint64_t id, char *tokens[], int count, uint64_t receiveUs) {
  uint64_t hostUs;
  if (count != 3 || !parseU64(tokens[2], hostUs)) { errorResponse(id, "BAD_ARGS", "usage: PING <id> <host_us>"); return; }
  protocolOut.print("PONG "); printU64(id); protocolOut.print(" host_us="); printU64(hostUs);
  protocolOut.print(" probe_rx_us="); printU64(receiveUs); protocolOut.print(" probe_tx_us="); printU64(probeMicros());
  protocolOut.print(" boot="); printU64(bootId); protocolOut.println();
}

static void handleReset(uint64_t id, int count) {
  if (count != 2) { errorResponse(id, "BAD_ARGS", "usage: RESET <id>"); return; }
  stopMonitor(); selectedChannel = 0; selectedBssidCount = 0; sessionId = sequenceNumber = reports = dropped = 0;
  clearIntervals(); clearAggregate(sessionStats); state = ProbeState::Idle;
  protocolOut.print("OK "); printU64(id); protocolOut.println(" RESET");
}

// ---- M5StickC Plus2: status screen, red LED and C-button power off ----

static void buzzerOff();

#if PROBE_M5_UI
static M5GFX m5Display;                  // detects the Plus2's ST7789 panel and backlight itself
static M5Canvas m5Screen(&m5Display);    // drawn off-screen, then pushed whole, so it never flickers
static uint32_t m5NextDrawMs = 0;
static uint32_t m5PowerHeldSinceMs = 0;
static bool m5PowerArmed = false;        // C also switches it on: a hold only counts once C has been let go
static bool m5LedLit = false, m5LedKnown = false;

static void setupM5() {
  pinMode(M5_LED_PIN, OUTPUT);
  digitalWrite(M5_LED_PIN, LOW);
  pinMode(M5_POWER_BUTTON_PIN, INPUT);
  m5Display.init();
  m5Display.setRotation(PROBE_M5_ROTATION);
  m5Display.setBrightness(PROBE_M5_BRIGHTNESS);
  m5Screen.setColorDepth(16);
  m5Screen.createSprite(m5Display.width(), m5Display.height());
}

// The red LED mirrors the pairing pattern (updatePairingLed): a blip while advertising, fast blinks while pairing,
// steady when paired, three flashes after a failure.
static void m5SetLed(bool on) {
  if (m5LedKnown && on == m5LedLit) return;
  m5LedKnown = true;
  m5LedLit = on;
  digitalWrite(M5_LED_PIN, on ? HIGH : LOW);
}

static void m5Draw() {
  auto &g = m5Screen;
  const int w = g.width();
  char line[72];
  g.fillScreen(TFT_BLACK);
  g.setTextDatum(top_left);
  g.setFont(&fonts::Font2);

  g.setTextColor(TFT_WHITE);
  g.drawString("WiFiProbe", 4, 2);
  // Battery: a rough charge from the cell voltage (3.3 V empty, 4.15 V full); above that it is charging on USB.
  const int batteryMv = static_cast<int>(analogReadMilliVolts(M5_BATTERY_ADC_PIN)) * 2;
  if (batteryMv > 2500) {
    if (batteryMv > 4250) snprintf(line, sizeof(line), "USB");
    else snprintf(line, sizeof(line), "%d%%", constrain((batteryMv - 3300) * 100 / (4150 - 3300), 0, 100));
    g.setTextDatum(top_right);
    g.drawString(line, w - 4, 2);
    g.setTextDatum(top_left);
  }

  // Pairing status
  const char *ble = "BLE off";
  uint16_t bleColor = TFT_DARKGREY;
#if PROBE_BLE_ENABLED
  switch (pairingLedState) {
    case PairingLedState::Advertising: ble = "BLE: waiting for headset"; bleColor = TFT_CYAN; break;
    case PairingLedState::Pairing:
      snprintf(line, sizeof(line), "BLE: pairing  PIN %06lu", static_cast<unsigned long>(BLE_PAIRING_PASSKEY));
      ble = line; bleColor = TFT_YELLOW; break;
    case PairingLedState::Paired: ble = bleNegotiated ? "BLE: connected" : "BLE: paired"; bleColor = TFT_GREEN; break;
    case PairingLedState::Failed: ble = "BLE: pairing failed"; bleColor = TFT_RED; break;
    default: break;
  }
#endif
  g.setTextColor(bleColor);
  g.drawString(ble, 4, 22);

  // The network being measured (or scanned for), and the probe state
  if (state == ProbeState::Scanning) snprintf(line, sizeof(line), "Scanning for networks...");
  else if (selectedChannel && selectedBssidCount > 1)
    snprintf(line, sizeof(line), "%s  ch %u  %d APs", selectedSsid, selectedChannel, selectedBssidCount);
  else if (selectedChannel) snprintf(line, sizeof(line), "%s  ch %u", selectedSsid[0] ? selectedSsid : "(hidden network)", selectedChannel);
  else if (lastScanCount >= 0) snprintf(line, sizeof(line), "No network chosen (%d found)", lastScanCount);
  else snprintf(line, sizeof(line), "No network chosen");
  g.setTextColor(TFT_WHITE);
  g.drawString(line, 4, 42);
  g.setTextColor(TFT_DARKGREY);
  g.drawString(stateName(), 4, 60);

  // Signal strength: the latest SAMPLE's mean, and a bar from -90 (red) to -30 dBm (green)
  const bool fresh = state == ProbeState::Streaming && haveSample && millis() - lastSampleMs < 3000;
  if (fresh) snprintf(line, sizeof(line), "%d dBm", lastSampleRssi);
  else snprintf(line, sizeof(line), "-- dBm");
  g.setFont(&fonts::Font4);
  g.setTextColor(fresh ? TFT_WHITE : TFT_DARKGREY);
  g.drawString(line, 4, 82);
  const int segments = 10, barX = 128, barY = 86, segmentWidth = (w - 4 - barX) / segments;
  const int lit = fresh ? constrain((lastSampleRssi + 90) * segments / 60 + 1, 0, segments) : 0;
  for (int i = 0; i < segments; ++i) {
    const uint16_t color = i < 3 ? TFT_RED : i < 6 ? TFT_YELLOW : TFT_GREEN;
    g.fillRect(barX + i * segmentWidth, barY, segmentWidth - 2, 18, i < lit ? color : 0x2104);
  }

  g.setFont(&fonts::Font0);
  g.setTextColor(TFT_DARKGREY);
  g.drawString("Hold C to switch off", 4, 124);
  g.pushSprite(0, 0);
}

static void m5PowerOff() {
  stopMonitor();
  if (state == ProbeState::Streaming) state = ProbeState::Selected;
  buzzerOff();
  m5Screen.fillScreen(TFT_BLACK);
  m5Screen.setFont(&fonts::Font4);
  m5Screen.setTextColor(TFT_WHITE);
  m5Screen.setTextDatum(middle_center);
  m5Screen.drawString("Switching off", m5Screen.width() / 2, m5Screen.height() / 2);
  m5Screen.pushSprite(0, 0);
  digitalWrite(M5_LED_PIN, LOW);
  while (digitalRead(M5_POWER_BUTTON_PIN) == LOW) delay(10);   // let go of C first, or it would wake straight away
  delay(300);
#if PROBE_POWER_HOLD_PIN >= 0
  digitalWrite(PROBE_POWER_HOLD_PIN, LOW);   // on battery, this switches it off
#endif
  delay(500);
  // Still running: it's on USB power, which can't be cut. Sleep until C is pressed again (a wake restarts it).
  m5Display.sleep();
  m5Display.setBrightness(0);
  esp_sleep_enable_ext0_wakeup(static_cast<gpio_num_t>(M5_POWER_BUTTON_PIN), 0);
  esp_deep_sleep_start();
}

static void statusChanged() {
  m5Draw();
  m5NextDrawMs = millis() + 250;
}

static void statusChangedIfDue(uint32_t now) {
  if (static_cast<int32_t>(now - m5NextDrawMs) >= 0) statusChanged();
}

static void updateM5() {
  const uint32_t now = millis();
  if (digitalRead(M5_POWER_BUTTON_PIN) == LOW) {
    if (!m5PowerArmed) return statusChangedIfDue(now);
    if (!m5PowerHeldSinceMs) m5PowerHeldSinceMs = now ? now : 1;
    else if (now - m5PowerHeldSinceMs >= PROBE_M5_POWER_OFF_MS) m5PowerOff();
  } else {
    m5PowerHeldSinceMs = 0;
    m5PowerArmed = true;
  }
  statusChangedIfDue(now);
}

#else
static void statusChanged() {}
#endif

// ---- Protocol 1.1: buzzer ----

static void buzzerOn(uint32_t frequency, uint32_t durationMs) {
  (void)durationMs;   // updateBuzzer ends it
#if PROBE_HAS_BUZZER
  ledcWriteTone(PROBE_BUZZER_PIN, frequency);
#endif
}

static void buzzerOff() {
#if PROBE_HAS_BUZZER
  ledcWriteTone(PROBE_BUZZER_PIN, 0);
#endif
  buzzing = false;
}

static void updateBuzzer() {
  if (buzzing && millis() - buzzStartMs >= buzzDurationMs) buzzerOff();
}

// BUZZ <id> <duration_ms> [freq=<hz>]: sound the buzzer for duration_ms (1..10000), replacing any tone already
// playing. The reply comes at once; the tone stops by itself, or on BUZZ_OFF.
static void handleBuzz(uint64_t id, char *tokens[], int count) {
  uint64_t duration = 0, frequency = DEFAULT_BUZZ_HZ;
  if (count < 3 || count > 4 || !parseU64(tokens[2], duration) || duration < 1 || duration > MAX_BUZZ_MS ||
      (count == 4 && (strncmp(tokens[3], "freq=", 5) != 0 || !parseU64(tokens[3] + 5, frequency) ||
                      frequency < MIN_BUZZ_HZ || frequency > MAX_BUZZ_HZ))) {
    errorResponse(id, "BAD_ARGS", "usage: BUZZ <id> <duration_ms 1-10000> [freq=<hz 100-10000>]");
    return;
  }
#if PROBE_HAS_BUZZER
  buzzerOn(static_cast<uint32_t>(frequency), static_cast<uint32_t>(duration));
  buzzing = true;
  buzzStartMs = millis();
  buzzDurationMs = static_cast<uint32_t>(duration);
  protocolOut.print("OK "); printU64(id);
  protocolOut.print(" BUZZ duration_ms="); printU64(duration); protocolOut.print(" freq="); printU64(frequency); protocolOut.println();
#else
  errorResponse(id, "NOT_SUPPORTED", "this build has no buzzer");
#endif
}

static void handleBuzzOff(uint64_t id, int count) {
  if (count != 2) { errorResponse(id, "BAD_ARGS", "usage: BUZZ_OFF <id>"); return; }
#if PROBE_HAS_BUZZER
  buzzerOff();
  protocolOut.print("OK "); printU64(id); protocolOut.println(" BUZZ_OFF");
#else
  errorResponse(id, "NOT_SUPPORTED", "this build has no buzzer");
#endif
}

// ---- Protocol 1.1: buttons ----

static uint8_t readButtonsRaw() {
  uint8_t bits = 0;
#if PROBE_BUTTON_A_PIN >= 0
  if ((digitalRead(PROBE_BUTTON_A_PIN) == LOW) == (PROBE_BUTTONS_ACTIVE_LOW != 0)) bits |= BUTTON_A_BIT;
#endif
#if PROBE_BUTTON_B_PIN >= 0
  if ((digitalRead(PROBE_BUTTON_B_PIN) == LOW) == (PROBE_BUTTONS_ACTIVE_LOW != 0)) bits |= BUTTON_B_BIT;
#endif
  return bits;
}

static void emitButtons(Transport transport) {
  protocolOut.routeTo(transport);
  protocolOut.printf("BUTTONS buttons=%u probe_us=", buttonState); printU64(probeMicros()); protocolOut.println();
}

// Debounced button state; each change is sent as a BUTTONS event on every link negotiated at 1.1 or later.
static void updateButtons() {
#if PROBE_HAS_BUTTONS
  const uint8_t raw = readButtonsRaw();
  const uint32_t now = millis();
  if (raw != buttonCandidate) { buttonCandidate = raw; buttonCandidateSinceMs = now; return; }
  if (raw == buttonState || now - buttonCandidateSinceMs < BUTTON_DEBOUNCE_MS) return;
  buttonState = raw;
#if PROBE_SERIAL_ENABLED
  if (linkSupports11(Transport::Serial)) emitButtons(Transport::Serial);
#endif
#if PROBE_BLE_ENABLED
  if (linkSupports11(Transport::Ble) && bleConnected && bleAuthenticated) emitButtons(Transport::Ble);
#endif
#endif
}

static void setupBoardExtras() {
#if PROBE_POWER_HOLD_PIN >= 0
  pinMode(PROBE_POWER_HOLD_PIN, OUTPUT);
  digitalWrite(PROBE_POWER_HOLD_PIN, HIGH);
#endif
#if PROBE_HAS_BUZZER
  ledcAttach(PROBE_BUZZER_PIN, DEFAULT_BUZZ_HZ, 8);
  ledcWriteTone(PROBE_BUZZER_PIN, 0);
#endif
#if PROBE_M5_UI
  setupM5();
#endif
#if PROBE_BUTTON_A_PIN >= 0
  pinMode(PROBE_BUTTON_A_PIN, INPUT);   // the Plus2's button pins are input-only with board pull-ups
#endif
#if PROBE_BUTTON_B_PIN >= 0
  pinMode(PROBE_BUTTON_B_PIN, INPUT);
#endif
  buttonState = buttonCandidate = readButtonsRaw();
  buttonCandidateSinceMs = millis();
}

static void processLine(char *line, uint64_t receiveUs, Transport source) {
  currentCommandTransport = source;
  protocolOut.routeTo(source);
  char *tokens[MAX_TOKENS];
  const int count = tokenize(line, tokens, MAX_TOKENS);
  if (count < 0) { errorResponse(0, "TOO_MANY_TOKENS", "command has too many tokens"); return; }
  if (!count) return;
  uint64_t id = 0;
  if (count < 2 || !parseU64(tokens[1], id)) { errorResponse(0, "BAD_ID", "missing or invalid command id"); return; }
  if (!strcmp(tokens[0], "HELLO")) { handleHello(id, tokens, count); return; }
  if (!requireNegotiated(id)) return;
  if (!strcmp(tokens[0], "CAPS")) handleCaps(id, count);
  else if (!strcmp(tokens[0], "SCAN")) handleScan(id, tokens, count);
  else if (!strcmp(tokens[0], "SELECT")) handleSelect(id, tokens, count);
  else if (!strcmp(tokens[0], "START")) handleStart(id, tokens, count);
  else if (!strcmp(tokens[0], "STOP")) handleStop(id, count);
  else if (!strcmp(tokens[0], "STATUS")) handleStatus(id, count);
  else if (!strcmp(tokens[0], "STATS")) handleStats(id, count);
  else if (!strcmp(tokens[0], "PING")) handlePing(id, tokens, count, receiveUs);
  else if (!strcmp(tokens[0], "RESET")) handleReset(id, count);
  // 1.1 commands are unknown on a 1.0 link, exactly as in 1.0 firmware.
  else if (linkSupports11(source) && !strcmp(tokens[0], "BUZZ")) handleBuzz(id, tokens, count);
  else if (linkSupports11(source) && !strcmp(tokens[0], "BUZZ_OFF")) handleBuzzOff(id, count);
  else errorResponse(id, "UNKNOWN_COMMAND", "unknown command");
}

static void acceptTransportByte(LineFramer &framer, char value, Transport source) {
  if (value == '\n') {
    protocolOut.routeTo(source);
    if (framer.discardingOversize) errorResponse(0, "LINE_TOO_LONG", "maximum line length is 512 bytes");
    else {
      if (framer.length && framer.bytes[framer.length - 1] == '\r') --framer.length;
      framer.bytes[framer.length] = 0;
      processLine(framer.bytes, probeMicros(), source);
    }
    framer.length = 0;
    framer.discardingOversize = false;
  } else if (!framer.discardingOversize) {
    if (framer.length < MAX_LINE) framer.bytes[framer.length++] = value;
    else framer.discardingOversize = true;
  }
}

#if PROBE_SERIAL_ENABLED
static void readSerialCommands() {
  while (Serial.available()) acceptTransportByte(serialFramer, static_cast<char>(Serial.read()), Transport::Serial);
}
#endif

#if PROBE_BLE_ENABLED
static bool enqueueBleByte(uint8_t value) {
  bool accepted = false;
  portENTER_CRITICAL(&bleRxMux);
  const size_t next = (bleRxHead + 1) % BLE_RX_QUEUE_CAPACITY;
  if (next != bleRxTail) {
    bleRxQueue[bleRxHead] = value;
    bleRxHead = next;
    accepted = true;
  }
  portEXIT_CRITICAL(&bleRxMux);
  return accepted;
}

static bool dequeueBleByte(uint8_t &value) {
  bool available = false;
  portENTER_CRITICAL(&bleRxMux);
  if (bleRxTail != bleRxHead) {
    value = bleRxQueue[bleRxTail];
    bleRxTail = (bleRxTail + 1) % BLE_RX_QUEUE_CAPACITY;
    available = true;
  }
  portEXIT_CRITICAL(&bleRxMux);
  return available;
}

static void clearBleInput() {
  portENTER_CRITICAL(&bleRxMux);
  bleRxHead = bleRxTail = 0;
  portEXIT_CRITICAL(&bleRxMux);
  bleFramer.length = 0;
  bleFramer.discardingOversize = false;
}

class ProbeServerCallbacks final : public BLEServerCallbacks {
  void onConnect(BLEServer *server) override {
    (void)server;
    bleConnected = true;
    bleAuthenticated = false;
    pairingLedState = PairingLedState::Pairing;
  }

  void onDisconnect(BLEServer *server) override {
    (void)server;
    bleConnected = false;
    bleAuthenticated = false;
    bleDisconnectPending = true;
    if (pairingLedState != PairingLedState::Failed) pairingLedState = PairingLedState::Advertising;
  }
};

class ProbeSecurityCallbacks final : public BLESecurityCallbacks {
  uint32_t onPassKeyRequest() override {
    pairingLedState = PairingLedState::Pairing;
    return BLE_PAIRING_PASSKEY;
  }

  void onPassKeyNotify(uint32_t passKey) override {
    (void)passKey;
    pairingLedState = PairingLedState::Pairing;
  }

  bool onConfirmPIN(uint32_t passKey) override {
    (void)passKey;
    pairingLedState = PairingLedState::Pairing;
    return true;
  }

  bool onSecurityRequest() override {
    pairingLedState = PairingLedState::Pairing;
    return true;
  }

#if defined(CONFIG_BLUEDROID_ENABLED) && CONFIG_BLUEDROID_ENABLED
  void onAuthenticationComplete(esp_ble_auth_cmpl_t result) override {
    bleAuthenticated = result.success;
    pairingLedState = result.success ? PairingLedState::Paired : PairingLedState::Failed;
    if (!result.success) pairingFailureUntilMs = millis() + 3000;
  }
#endif

#if defined(CONFIG_NIMBLE_ENABLED) && CONFIG_NIMBLE_ENABLED
  void onAuthenticationComplete(ble_gap_conn_desc *description) override {
    const bool success = description && description->sec_state.encrypted && description->sec_state.authenticated;
    bleAuthenticated = success;
    pairingLedState = success ? PairingLedState::Paired : PairingLedState::Failed;
    if (!success) pairingFailureUntilMs = millis() + 3000;
  }

  // Arduino-ESP32 3.3.12 added this failure-reporting overload. Omitting the
  // override specifier keeps the sketch source-compatible with 3.3.5/3.3.6,
  // where the base class only exposes the success callback above.
  void onAuthenticationComplete(ble_gap_conn_desc *description, int status) {
    if (status == 0) {
      onAuthenticationComplete(description);
      return;
    }
    bleAuthenticated = false;
    pairingLedState = PairingLedState::Failed;
    pairingFailureUntilMs = millis() + 3000;
  }
#endif
};

class ProbeRxCallbacks final : public BLECharacteristicCallbacks {
  void onWrite(BLECharacteristic *characteristic) override {
    if (!bleAuthenticated) return;
    const String value = characteristic->getValue();
    for (size_t i = 0; i < value.length(); ++i) {
      if (!enqueueBleByte(static_cast<uint8_t>(value[i]))) {
        bleRxOverflow = true;
        break;
      }
    }
  }
};

static void setupPairingLed() {
  if (BLE_PAIRING_LED_PIN < 0) return;
  pinMode(BLE_PAIRING_LED_PIN, OUTPUT);
  digitalWrite(BLE_PAIRING_LED_PIN, BLE_PAIRING_LED_ACTIVE_HIGH ? LOW : HIGH);
}

static void updatePairingLed() {
  if (BLE_PAIRING_LED_PIN < 0 && !PROBE_M5_UI) return;
  const uint32_t phase = millis();
  if (pairingLedState == PairingLedState::Failed && !bleConnected &&
      static_cast<int32_t>(phase - pairingFailureUntilMs) >= 0) {
    pairingLedState = PairingLedState::Advertising;
  }
  bool on = false;
  switch (pairingLedState) {
    case PairingLedState::Advertising: on = phase % 1000 < 100; break;
    case PairingLedState::Pairing: on = phase % 250 < 125; break;
    case PairingLedState::Paired: on = true; break;
    case PairingLedState::Failed: {
      const uint32_t failurePhase = phase % 1200;
      on = failurePhase < 100 || (failurePhase >= 200 && failurePhase < 300) || (failurePhase >= 400 && failurePhase < 500);
      break;
    }
    default: on = false; break;
  }
#if PROBE_M5_UI
  m5SetLed(on);
#endif
  if (BLE_PAIRING_LED_PIN < 0) return;
  const bool high = BLE_PAIRING_LED_ACTIVE_HIGH ? on : !on;
  digitalWrite(BLE_PAIRING_LED_PIN, high ? HIGH : LOW);
}

static void setupBle() {
  setupPairingLed();
  BLEDevice::init(BLE_DEVICE_NAME);
  BLEDevice::setMTU(247);
  BLEDevice::setSecurityCallbacks(new ProbeSecurityCallbacks());

  bleSecurity = new BLESecurity();
  bleSecurity->setPassKey(true, BLE_PAIRING_PASSKEY);
  bleSecurity->setCapability(ESP_IO_CAP_OUT);
  bleSecurity->setAuthenticationMode(true, true, true);

  bleServer = BLEDevice::createServer();
  bleServer->setCallbacks(new ProbeServerCallbacks());
  bleServer->advertiseOnDisconnect(true);
  BLEService *service = bleServer->createService(BLE_SERVICE_UUID);

  bleTxCharacteristic = service->createCharacteristic(
      BLE_TX_UUID,
      BLECharacteristic::PROPERTY_READ |
          BLECharacteristic::PROPERTY_NOTIFY |
          BLECharacteristic::PROPERTY_READ_AUTHEN);
  BLECharacteristic *rxCharacteristic = service->createCharacteristic(
      BLE_RX_UUID,
      BLECharacteristic::PROPERTY_WRITE |
          BLECharacteristic::PROPERTY_WRITE_NR |
          BLECharacteristic::PROPERTY_WRITE_AUTHEN);

#if defined(CONFIG_BLUEDROID_ENABLED) && CONFIG_BLUEDROID_ENABLED
  bleTxCharacteristic->setAccessPermissions(ESP_GATT_PERM_READ_ENC_MITM);
  rxCharacteristic->setAccessPermissions(ESP_GATT_PERM_WRITE_ENC_MITM);
  BLE2902 *notificationDescriptor = new BLE2902();
  notificationDescriptor->setAccessPermissions(ESP_GATT_PERM_READ_ENC_MITM | ESP_GATT_PERM_WRITE_ENC_MITM);
  bleTxCharacteristic->addDescriptor(notificationDescriptor);
#endif
  rxCharacteristic->setCallbacks(new ProbeRxCallbacks());
  bleTxCharacteristic->setCallbacks(new ProbeTxCallbacks());
  service->start();

  BLEAdvertising *advertising = BLEDevice::getAdvertising();
  advertising->addServiceUUID(BLE_SERVICE_UUID);
  advertising->setScanResponse(true);
  advertising->setMinPreferred(0x06);
  advertising->setMaxPreferred(0x12);
  BLEDevice::startAdvertising();
  pairingLedState = PairingLedState::Advertising;
}

static void handleBleLifecycle() {
  if (bleDisconnectPending) {
    bleDisconnectPending = false;
    bleNegotiated = false;
    bleMinor = 0;
    if (buzzing) buzzerOff();   // never leave the buzzer sounding for a host that has gone
    clearBleInput();
    if (state == ProbeState::Streaming && streamTransport == Transport::Ble) {
      stopMonitor();
      state = ProbeState::Selected;
    }
  }
  if (bleRxOverflow) {
    bleRxOverflow = false;
    clearBleInput();
    protocolOut.routeTo(Transport::Ble);
    errorResponse(0, "RX_OVERRUN", "BLE receive queue overflow");
  }
}

static void readBleCommands() {
  uint8_t value;
  while (dequeueBleByte(value)) acceptTransportByte(bleFramer, static_cast<char>(value), Transport::Ble);
}
#endif

void setup() {
  setupBoardExtras();   // first: the Plus2 powers off on battery unless its hold pin goes high promptly
#if PROBE_SERIAL_ENABLED
  Serial.begin(115200);
#endif
  WiFi.mode(WIFI_STA);
  WiFi.disconnect(false, false);
  clearIntervals(); clearAggregate(sessionStats);
  bootId = (static_cast<uint64_t>(esp_random()) << 32) | esp_random();
  if (!bootId) bootId = 1;
#if PROBE_BLE_ENABLED
  setupBle();
#endif
}

void loop() {
#if PROBE_SERIAL_ENABLED
  readSerialCommands();
#endif
#if PROBE_BLE_ENABLED
  handleBleLifecycle();
  readBleCommands();
  updatePairingLed();
#endif
  updateButtons();
  updateBuzzer();
#if PROBE_M5_UI
  updateM5();
#endif
  emitSampleIfDue();
  delay(1);
}
