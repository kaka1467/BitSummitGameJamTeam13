#include <HX711.h>

const int LOADCELL_DOUT_PIN = 21;
const int LOADCELL_SCK_PIN  = 22;

HX711 scale;

void setup() {
    Serial.begin(115200);
    scale.begin(LOADCELL_DOUT_PIN, LOADCELL_SCK_PIN);
}

void loop() {
    if (scale.is_ready()) {
        long reading = scale.read();
        Serial.println(reading);
    } else {
        Serial.println(0);
    }
    delay(50);
}
