// SPDX-License-Identifier: GPL-3.0-only
// Mono and stereo gain fixtures for capture insert routing.
#include <lv2/core/lv2.h>
#include <stdlib.h>
#include <string.h>

typedef struct {
  float *ports[5];
  uint32_t channels;
} Gain;

static LV2_Handle instantiate(const LV2_Descriptor *descriptor, double rate,
                              const char *bundle,
                              const LV2_Feature *const *features) {
  Gain *gain = calloc(1, sizeof(Gain));
  if (gain)
    gain->channels = strcmp(descriptor->URI, "urn:openxlr:test:gain:mono") == 0 ? 1 : 2;
  return gain;
}

static void connect(LV2_Handle handle, uint32_t port, void *data) {
  if (port < 5)
    ((Gain *)handle)->ports[port] = data;
}

static void run(LV2_Handle handle, uint32_t frames) {
  Gain *gain = handle;
  for (uint32_t frame = 0; frame < frames; frame++)
    for (uint32_t channel = 0; channel < gain->channels; channel++)
      gain->ports[channel + gain->channels][frame] =
          gain->ports[channel][frame] * *gain->ports[2 * gain->channels];
}

static const LV2_Descriptor descriptors[] = {
    {"urn:openxlr:test:gain:mono", instantiate, connect, NULL, run, NULL, free, NULL},
    {"urn:openxlr:test:gain", instantiate, connect, NULL, run, NULL, free, NULL},
};

LV2_SYMBOL_EXPORT const LV2_Descriptor *lv2_descriptor(uint32_t index) {
  return index < 2 ? &descriptors[index] : NULL;
}
