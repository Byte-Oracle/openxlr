// SPDX-License-Identifier: GPL-3.0-only
#define main host_program_main
#include "../host.c"
#undef main
#include "../sound-check.c"
#include "../level.c"
#include <assert.h>

static bool near(float a, float b) { return fabsf(a - b) < 1e-4f; }

// Raw and processed: the signal moves from the main output to the tap with a
// crossfade of RAW_CROSSFADE seconds, the two always summing to the signal,
// and settles exactly on one side.
static void check_raw_switch(void) {
  Host h = {.rate = 8000, .channels = 1};
  assert(sound_check_load(&h, NULL));
  SoundCheck *s = h.impl;
  float input[512], output[512], tap[512];
  float *in[] = {input}, *out[] = {output};
  h.tap = tap;
  for (unsigned i = 0; i < 512; i++) input[i] = 0.5f;
  sound_check_process(&h, 512, in, out);
  for (unsigned i = 0; i < 512; i++) assert(output[i] == 0.5f && tap[i] == 0);
  atomic_store(&s->raw->desired, 1);
  sound_check_process(&h, 512, in, out);
  unsigned ramp = (unsigned)(RAW_CROSSFADE * 8000);  // 160 samples
  for (unsigned i = 0; i < 512; i++) {
    assert(near(output[i] + tap[i], 0.5f));
    if (i + 1 < ramp) assert(output[i] > 0 && tap[i] < 0.5f);
    if (i + 1 >= ramp + 1) assert(output[i] == 0 && tap[i] == 0.5f);
  }
  assert(tap[0] > 0 && tap[0] < 0.01f);  // the first step, not a jump
  // A loop takes the switch the same way: the take on the tap alone.
  atomic_store(&s->command->desired, RECORD);
  sound_check_process(&h, 64, in, out);
  atomic_store(&s->command->desired, LOOP);
  for (unsigned i = 0; i < 512; i++) input[i] = -1;
  sound_check_process(&h, 512, in, out);
  for (unsigned i = 0; i < 512; i++) assert(output[i] == 0 && tap[i] == 0.5f);
  // Back to processed, and without a tap buffer the main output still works.
  atomic_store(&s->raw->desired, 0);
  h.tap = NULL;
  for (unsigned i = 0; i < 4; i++) sound_check_process(&h, 512, in, out);
  for (unsigned i = 0; i < 512; i++) assert(output[i] == 0.5f);
  sound_check_unload(&h);
  puts("PASS: raw and processed crossfade onto the tap and back");
}

// The take's waveform: PEAK_POINTS peaks over the recorded length, counted
// once per take and only when the recording held samples.
static void check_peaks(void) {
  Host h = {.rate = 8000, .channels = 1};
  assert(sound_check_load(&h, NULL));
  SoundCheck *s = h.impl;
  float input[800], output[800];
  float *in[] = {input}, *out[] = {output};
  // One second: the first half at 0.25, then a single 0.9 sample, then 0.1.
  atomic_store(&s->command->desired, RECORD);
  for (unsigned block = 0; block < 10; block++) {
    for (unsigned i = 0; i < 800; i++) {
      unsigned n = block * 800 + i;
      input[i] = n < 4000 ? (n % 2 ? -0.25f : 0.25f) : n == 6000 ? 0.9f : 0.1f;
    }
    sound_check_process(&h, 800, in, out);
  }
  sound_check_tick(&h);
  assert(atomic_load(&s->take->observed) == 0);  // still recording
  atomic_store(&s->command->desired, LIVE);
  sound_check_process(&h, 800, in, out);
  assert(atomic_load(&s->takes) == 1 && atomic_load(&s->take_length) == 8000);
  sound_check_tick(&h);
  assert(atomic_load(&s->take->observed) == 1);
  for (unsigned j = 0; j < PEAK_POINTS; j++) {
    float peak = atomic_load(&s->peaks[j]->observed);
    if (j < PEAK_POINTS / 2) assert(near(peak, 0.25f));
    else if (j == PEAK_POINTS * 6000 / 8000) assert(near(peak, 0.9f));
    else assert(near(peak, 0.1f));
  }
  // The meters come after the peaks, so a reader that sees the new take
  // holds its peaks.
  assert(s->take > s->peaks[PEAK_POINTS - 1] && s->peaks[0] > s->playhead);
  // A second tick publishes nothing new; an empty recording counts no take.
  sound_check_tick(&h);
  assert(atomic_load(&s->take->observed) == 1);
  atomic_store(&s->command->desired, RECORD);
  sound_check_process(&h, 0, in, out);
  atomic_store(&s->command->desired, LIVE);
  sound_check_process(&h, 800, in, out);
  assert(atomic_load(&s->takes) == 1);
  // A full buffer is a take too, and a short take spreads over every point.
  atomic_store(&s->command->desired, RECORD);
  for (unsigned i = 0; i < 800; i++) input[i] = 0.5f;
  for (unsigned block = 0; block < 100; block++) sound_check_process(&h, 800, in, out);
  assert(atomic_load(&s->takes) == 2 && atomic_load(&s->command->desired) == LIVE);
  atomic_store(&s->command->desired, RECORD);
  sound_check_process(&h, 80, in, out);
  atomic_store(&s->command->desired, LOOP);
  sound_check_process(&h, 40, in, out);
  assert(atomic_load(&s->takes) == 3);
  assert(atomic_load(&s->playhead->observed) == 40);
  sound_check_tick(&h);
  assert(atomic_load(&s->take->observed) == 3);
  for (unsigned j = 0; j < PEAK_POINTS; j++) assert(near(atomic_load(&s->peaks[j]->observed), 0.5f));
  atomic_store(&s->command->desired, LIVE);
  sound_check_process(&h, 40, in, out);
  assert(atomic_load(&s->playhead->observed) == 0);
  sound_check_unload(&h);
  puts("PASS: take peaks over the recorded length, one count per take");
}

// The level meter: silence passes nothing, a steady signal reads its peak and
// RMS, the peak falls back slowly, and a block at full scale counts a clip.
static void check_level(void) {
  assert(backend_named("level") == &level_backend);
  Host h = {.rate = 8000, .channels = 1};
  assert(level_load(&h, NULL));
  Level *l = h.impl;
  float input[400], output[400];
  float *in[] = {input}, *out[] = {output};
  for (unsigned i = 0; i < 400; i++) input[i] = i % 2 ? -0.5f : 0.5f;
  for (unsigned block = 0; block < 100; block++) level_process(&h, 400, in, out);
  for (unsigned i = 0; i < 400; i++) assert(output[i] == 0);
  float peak = atomic_load(&l->peak->observed), rms = atomic_load(&l->rms->observed);
  assert(fabsf(peak - -6.0206f) < 0.01f && fabsf(rms - -6.0206f) < 0.05f);
  assert(atomic_load(&l->clips->observed) == 0);
  for (unsigned i = 0; i < 400; i++) input[i] = 0;
  level_process(&h, 400, in, out);  // 50 ms of silence: the peak falls 1 dB
  assert(fabsf(atomic_load(&l->peak->observed) - -7.0206f) < 0.01f);
  for (unsigned block = 0; block < 400; block++) level_process(&h, 400, in, out);
  assert(atomic_load(&l->peak->observed) == LEVEL_FLOOR_DB);
  assert(atomic_load(&l->rms->observed) == LEVEL_FLOOR_DB);
  input[17] = -1;
  level_process(&h, 400, in, out);
  input[17] = 0.98f;  // just under the clip level
  level_process(&h, 400, in, out);
  assert(atomic_load(&l->clips->observed) == 1);
  assert(atomic_load(&l->peak->observed) > -0.2f);
  level_unload(&h);
  assert(h.impl == NULL);
  Host stereo = {.rate = 8000, .channels = 2};
  assert(!level_load(&stereo, NULL));
  puts("PASS: level meter peak, RMS, fall-back and clip count");
}

int main(void) {
  assert(backend_named("soundcheck") == &sound_check_backend);
  assert(sound_check_backend.tap_outputs == 1);
  Host h = {.rate = 8000, .channels = 1};
  assert(sound_check_load(&h, NULL));
  SoundCheck *s = h.impl;
  float input[512], output[512];
  float *in[] = {input}, *out[] = {output};
  for (unsigned i = 0; i < 512; i++) input[i] = (float)i / 512;
  sound_check_process(&h, 512, in, out);
  assert(memcmp(input, output, sizeof(input)) == 0 && s->length == 0);
  atomic_store(&s->command->desired, RECORD);
  sound_check_process(&h, 128, in, out);
  assert(s->length == 128 && memcmp(input, output, 128 * sizeof(float)) == 0);
  assert(atomic_load(&s->mode->observed) == RECORD);
  atomic_store(&s->command->desired, LOOP);
  for (unsigned i = 0; i < 512; i++) input[i] = -1;
  sound_check_process(&h, 512, in, out);
  for (unsigned i = 0; i < 512; i++) assert(output[i] == (float)(i % 128) / 512);
  assert(s->length == 128 && atomic_load(&s->mode->observed) == LOOP);
  atomic_store(&s->command->desired, LIVE);
  sound_check_process(&h, 512, in, out);
  assert(memcmp(input, output, sizeof(input)) == 0 && s->length == 128);
  atomic_store(&s->command->desired, RECORD);
  for (unsigned i = 0; i < 200; i++) sound_check_process(&h, 512, in, out);
  assert(s->length == 80000 && atomic_load(&s->command->desired) == LIVE);
  assert(atomic_load(&s->mode->observed) == LIVE);
  atomic_store(&s->command->desired, RECORD);
  sound_check_process(&h, 10, in, out);
  assert(s->length == 10);
  atomic_store(&s->command->desired, NAN);
  sound_check_process(&h, 512, in, out);
  assert(memcmp(input, output, sizeof(input)) == 0);
  assert(atomic_load(&s->mode->observed) == LIVE);

  // The tick never reports a mode: with the audio thread idle (a suspended
  // node), a recording request stays unreported until a cycle serves it.
  atomic_store(&s->command->desired, RECORD);
  sound_check_tick(&h);
  assert(s->timed_record && atomic_load(&s->mode->observed) == LIVE);
  assert(atomic_load(&s->frames->observed) == 10);
  // Unserved for the whole bound, the request goes back to live, and the
  // next cycle passes the microphone through instead of recording.
  s->record_started.tv_sec -= 11;
  sound_check_tick(&h);
  assert(atomic_load(&s->command->desired) == LIVE);
  assert(atomic_load(&s->mode->observed) == LIVE);
  sound_check_process(&h, 512, in, out);
  assert(memcmp(input, output, sizeof(input)) == 0 && s->length == 10);
  sound_check_tick(&h);
  assert(!s->timed_record);
  sound_check_unload(&h);
  assert(h.impl == NULL);
  puts("PASS: dry passthrough, replay, live restore, record replacement, "
       "ten-second bound and one writer for the reported mode");
  check_raw_switch();
  check_peaks();
  check_level();
  return 0;
}
