// SPDX-License-Identifier: GPL-3.0-only
// The microphone's level before any processing, for Mic setup.
//
// It is linked beside the channel's own route, from the same capture port,
// and passes nothing on: its output is silence. Only the audio thread writes
// its state and its three meters, once per block: `peak` and `rms` in dBFS,
// and `clips`, a count of the blocks that held a sample at or above the clip
// level, which a reader compares with the last count it saw.
#include "host.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

// The quietest figure reported, in dBFS, and the level a sample counts as
// clipped at (-0.1 dBFS): an interface's converter tops out just under 1.
#define LEVEL_FLOOR_DB -120.0
#define LEVEL_CLIP 0.98855309
// The peak falls by this many dB a second once the signal drops, and the
// mean square follows the signal with this time constant in seconds.
#define LEVEL_PEAK_FALL_DB 20.0
#define LEVEL_RMS_SECONDS 0.3

typedef struct {
  Control *peak, *rms, *clips;
  double held, mean_square, clipped;
} Level;

static bool level_load(Host *h, char **arguments) {
  if (h->channels != 1) return false;
  Level *l = calloc(1, sizeof(*l));
  if (!l) return false;
  h->impl = l;
  l->peak = host_add_control(h, "peak", LEVEL_FLOOR_DB, 12, LEVEL_FLOOR_DB, true, NULL);
  l->rms = host_add_control(h, "rms", LEVEL_FLOOR_DB, 12, LEVEL_FLOOR_DB, true, NULL);
  l->clips = host_add_control(h, "clips", 0, 1e9f, 0, true, NULL);
  return l->peak && l->rms && l->clips;
}

static bool level_activate(Host *h) { return true; }
static void level_deactivate(Host *h) {}

static void level_unload(Host *h) {
  free(h->impl);
  h->impl = NULL;
}

static double decibels(double linear) {
  return linear > 1e-6 ? fmax(LEVEL_FLOOR_DB, 20 * log10(linear)) : LEVEL_FLOOR_DB;
}

static void level_process(Host *h, uint32_t frames, float *const *in, float *const *out) {
  Level *l = h->impl;
  double peak = 0, sum = 0;
  bool clipped = false;
  for (uint32_t n = 0; n < frames; n++) {
    double v = fabs(in[0][n]);
    if (v > peak) peak = v;
    if (v >= LEVEL_CLIP) clipped = true;
    sum += v * v;
  }
  memset(out[0], 0, sizeof(float) * frames);
  if (frames == 0) return;
  double seconds = (double)frames / h->rate;
  l->held = fmax(peak, l->held * pow(10, -LEVEL_PEAK_FALL_DB * seconds / 20));
  l->mean_square += (sum / frames - l->mean_square) * (1 - exp(-seconds / LEVEL_RMS_SECONDS));
  // Below the floor a decaying value goes to zero rather than on into
  // subnormals, which are slow on the audio thread.
  if (l->held < 1e-7) l->held = 0;
  if (l->mean_square < 1e-14) l->mean_square = 0;
  if (clipped) l->clipped += 1;
  atomic_store(&l->peak->observed, (float)decibels(l->held));
  atomic_store(&l->rms->observed, (float)decibels(sqrt(l->mean_square)));
  atomic_store(&l->clips->observed, (float)l->clipped);
}

const Backend level_backend = {
  .name = "Level", .argument_count = 0,
  .load = level_load, .activate = level_activate,
  .deactivate = level_deactivate, .process = level_process,
  .unload = level_unload,
};
