// SPDX-License-Identifier: GPL-3.0-only
// A bounded dry-microphone loop upstream of the live insert chain.
//
// Who writes what. The audio thread owns the samples, the cursor, the
// waveform bins and the output meters `frames`, `mode` and `cursor`: the mode
// it reports is the one it ran. The command pipe and the main-thread tick
// write only `command` and `raw`, the requests. The audio thread writes
// `command` once, back to live, when the buffer is full; the tick does the
// same when a recording request has gone unserved for the whole bound, so a
// suspended input cannot start recording minutes after the click.
//
// Two outputs. The main one feeds the channel's chain; the tap goes straight
// to the channel, past the chain. `raw` moves the signal from one to the
// other with a short linear crossfade, so the same words can be heard with
// and without the processing at once, with no chain rebuilt.
//
// The take's waveform. While recording, the audio thread keeps the peak of
// each of PEAK_BINS stretches of the buffer. When a recording ends with
// samples in it, it publishes the length and counts the take; the tick then
// folds the bins that hold the take into PEAK_POINTS peaks and sets the
// meters p0 to p199, then `take`. The meters are printed in that order, so a
// reader that sees the new `take` already holds its peaks.
#include "host.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

enum {
  SOUND_CHECK_SECONDS = 10,
  LIVE = 0,
  RECORD = 1,
  LOOP = 2,
  PEAK_POINTS = 200,
  PEAK_BINS = 2000,
};
// The crossfade between the processed and the raw path, in seconds.
#define RAW_CROSSFADE 0.02

typedef struct {
  float *samples;
  uint32_t capacity, length, cursor;  // audio thread only
  int previous;                       // audio thread only
  float raw_gain, raw_step;           // audio thread only: 0 processed, 1 raw
  _Atomic float bins[PEAK_BINS];      // audio thread writes, tick reads
  _Atomic uint32_t take_length;       // the last finished take, then...
  _Atomic uint32_t takes;             // ...counted, by the audio thread
  uint32_t published;                 // tick only: the take now in p0..p199
  bool timed_record;                  // tick only
  struct timespec record_started;     // tick only
  Control *command, *raw, *frames, *mode, *playhead, *take;
  Control *peaks[PEAK_POINTS];
  char names[PEAK_POINTS][8];
} SoundCheck;

static bool sound_check_load(Host *h, char **arguments) {
  if (h->channels != 1) return false;
  SoundCheck *s = calloc(1, sizeof(*s));
  if (!s) return false;
  h->impl = s;
  s->capacity = h->rate * SOUND_CHECK_SECONDS;
  s->samples = calloc(s->capacity, sizeof(float));
  s->raw_step = (float)(1.0 / (RAW_CROSSFADE * h->rate));
  s->command = host_add_control(h, "command", LIVE, LOOP, LIVE, false, NULL);
  s->raw = host_add_control(h, "raw", 0, 1, 0, false, NULL);
  s->frames = host_add_control(h, "frames", 0, (float)s->capacity, 0, true, NULL);
  s->mode = host_add_control(h, "mode", LIVE, LOOP, LIVE, true, NULL);
  s->playhead = host_add_control(h, "cursor", 0, (float)s->capacity, 0, true, NULL);
  bool ok = s->samples && s->command && s->raw && s->frames && s->mode && s->playhead;
  for (unsigned i = 0; ok && i < PEAK_POINTS; i++) {
    snprintf(s->names[i], sizeof(s->names[i]), "p%u", i);
    s->peaks[i] = host_add_control(h, s->names[i], 0, 1, 0, true, NULL);
    ok = s->peaks[i] != NULL;
  }
  // After the peaks, so it is printed after them.
  s->take = ok ? host_add_control(h, "take", 0, 1e9f, 0, true, NULL) : NULL;
  return ok && s->take;
}
static bool sound_check_activate(Host *h) { return true; }
static void sound_check_deactivate(Host *h) {}
static void sound_check_unload(Host *h) {
  SoundCheck *s = h->impl;
  if (!s) return;
  free(s->samples);
  free(s);
  h->impl = NULL;
}

// A recording ended: publish its length, then count it.
static void finish_take(SoundCheck *s) {
  if (s->length == 0) return;
  atomic_store(&s->take_length, s->length);
  atomic_fetch_add(&s->takes, 1);
}

static void sound_check_process(Host *h, uint32_t frames, float *const *in, float *const *out) {
  SoundCheck *s = h->impl;
  float requested = atomic_load(&s->command->desired);
  int mode = requested == RECORD ? RECORD : requested == LOOP ? LOOP : LIVE;
  if (mode != s->previous) {
    if (s->previous == RECORD) finish_take(s);
    if (mode == RECORD) {
      s->length = 0;
      for (unsigned b = 0; b < PEAK_BINS; b++) atomic_store_explicit(&s->bins[b], 0, memory_order_relaxed);
    }
    if (mode == LOOP) s->cursor = 0;
    s->previous = mode;
  }
  if (mode == LOOP && s->length == 0) mode = LIVE;
  float target = atomic_load(&s->raw->desired) >= 0.5f ? 1.0f : 0.0f;
  float *tap = h->tap;
  for (uint32_t i = 0; i < frames; i++) {
    float x = in[0][i];
    if (mode == RECORD) {
      unsigned bin = (unsigned)((uint64_t)s->length * PEAK_BINS / s->capacity);
      float level = fabsf(x);
      if (level > atomic_load_explicit(&s->bins[bin], memory_order_relaxed))
        atomic_store_explicit(&s->bins[bin], level, memory_order_relaxed);
      s->samples[s->length++] = x;
      if (s->length == s->capacity) {
        mode = LIVE;
        s->previous = LIVE;
        finish_take(s);
        host_control_moved_rt(h, s->command, LIVE);
      }
    } else if (mode == LOOP) {
      x = s->samples[s->cursor++];
      if (s->cursor == s->length) s->cursor = 0;
    }
    if (s->raw_gain < target) s->raw_gain = fminf(target, s->raw_gain + s->raw_step);
    else if (s->raw_gain > target) s->raw_gain = fmaxf(target, s->raw_gain - s->raw_step);
    out[0][i] = x * (1 - s->raw_gain);
    if (tap) tap[i] = x * s->raw_gain;
  }
  atomic_store(&s->frames->observed, (float)s->length);
  atomic_store(&s->mode->observed, (float)mode);
  atomic_store(&s->playhead->observed, mode == LOOP ? (float)s->cursor : 0);
}

// Fold the bins a take of `length` samples covers into the peak meters.
static void publish_peaks(SoundCheck *s, uint32_t length) {
  unsigned used = (unsigned)(((uint64_t)length * PEAK_BINS + s->capacity - 1) / s->capacity);
  if (used == 0) used = 1;
  if (used > PEAK_BINS) used = PEAK_BINS;
  for (unsigned j = 0; j < PEAK_POINTS; j++) {
    unsigned from = j * used / PEAK_POINTS, to = (j + 1) * used / PEAK_POINTS;
    if (to <= from) to = from + 1;
    float peak = 0;
    for (unsigned b = from; b < to; b++)
      peak = fmaxf(peak, atomic_load_explicit(&s->bins[b], memory_order_relaxed));
    atomic_store(&s->peaks[j]->observed, fminf(peak, 1));
  }
}

static void sound_check_tick(Host *h) {
  SoundCheck *s = h->impl;
  uint32_t takes = atomic_load(&s->takes);
  if (takes != s->published) {
    publish_peaks(s, atomic_load(&s->take_length));
    s->published = takes;
    atomic_store(&s->take->observed, (float)takes);
  }
  if (atomic_load(&s->command->desired) != RECORD) {
    s->timed_record = false;
    return;
  }
  struct timespec now;
  clock_gettime(CLOCK_MONOTONIC, &now);
  if (!s->timed_record) { s->record_started = now; s->timed_record = true; }
  double elapsed = (double)(now.tv_sec - s->record_started.tv_sec)
      + (now.tv_nsec - s->record_started.tv_nsec) / 1e9;
  if (elapsed >= SOUND_CHECK_SECONDS) host_control_moved(h, s->command, LIVE);
}

const Backend sound_check_backend = {
  .name = "Sound Check", .argument_count = 0,
  .load = sound_check_load, .activate = sound_check_activate,
  .deactivate = sound_check_deactivate, .process = sound_check_process,
  .main_thread = sound_check_tick, .unload = sound_check_unload,
  .tap_outputs = 1,
};
