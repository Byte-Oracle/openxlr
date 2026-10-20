# MK.2 equalizer findings

Recorded on 2026-10-20 on one XLR Dock MK.2 and one Wave XLR MK.2. The
question was whether these devices run the four-band equalizer Wave Link
shows for their microphone, so that OpenXLR could drive it. They do not.
Wave Link 3.5 runs that equalizer on the computer. Both devices keep four
bands of EQ values in their settings block, but writing them changes
nothing we could measure on the USB recording or hear on the
zero-latency headphone path.

The other onboard processing is real. Low cut, compressor, expander,
voice tune and ClipGuard act on the audio when their bits are written, as
[hardware support](hardware-support.md) already records.

The Wave XLR MK.2 was measured from Linux the same day (see
[the Wave XLR MK.2](#the-wave-xlr-mk2)); Wave Link itself was only
captured with the dock. Wave Link names its mode for this family "Wave
XLR MKII Host".

## The unit and the tools

| Item | Value |
|---|---|
| Device | XLR Dock MK.2, `0fd9:00c7`, `bcdDevice 2.10` |
| Firmware | 1.0.0 as Wave Link reports it; build string `2025-08-15-06:31-AG-Release` in block `0x0000`. Wave Link offers 1.0.15, which was not installed |
| Control bank | `0x0103` on this unit (some units answer at `0x0203` instead, see hardware support) |
| Windows side | Windows 11 in a virtual machine with the dock passed through over USB, Wave Link 3.5.0.4892, Wireshark 4.6.8 and USBPcap 1.5.4.0 |
| Linux side | the dock back on the host, PipeWire, control transfers through libusb, pink noise and recording with `pw-play` and `pw-record`, spectra with NumPy |
| Microphone | Elgato Wave DX (dynamic), phantom power off throughout |

## How Wave Link drives the dock

Captured on Windows, one action per capture, with Wave Link's own log on
the same clock.

- At connect Wave Link reads blocks `0x0000`, `0x0001`, `0x0002`,
  `0x0004`, `0x0005`, `0x0006` and `0x0016` every 100 ms for about 2.7 s,
  plus two reads addressed to interface 3 itself (`c1 00 0000 0003`, two
  bytes, and `c1 02 0000 0003`, four bytes, once). After that it sends no
  control transfer at all until it quits.
- About 3 s after its first read it selects alternate setting 1 of
  interface 3, the vendor interface. That setting has an interrupt IN
  endpoint `0x84` of 204 bytes and an interrupt OUT endpoint `0x02` of
  404 bytes. While Wave Link runs, `0x84` carries about 500 transfers a
  second of 816 bytes and `0x02` about 480 a second of 1615 bytes, nearly
  all different. The payloads look like sample data in both directions.
  It selects setting 0 again when it quits.
- Wave Link's log and plugin graph put the dock's input in a mode called
  "Wave XLR MKII Host". The input gets Wave Link's own WaveEQ,
  WaveCompressor, WaveExpander and VoiceFocus plugins as nodes in its
  plugin host, and the log reports software mixing
  (`Hardware mixing state changed to: False`). In its binaries, hardware
  mixing and settings sync to the device are named for the Wave XLR Pro
  only.

Block lengths as Wave Link requests them:

| Block | Bytes | What is known |
|---|---|---|
| `0x0000` | 117 | identity: serial, a firmware build string, version bytes not yet located |
| `0x0001` | 6 | crossfade at byte 0 |
| `0x0002` | 21 | not decoded |
| `0x0004` | 38 | input settings, including the stored EQ bands below |
| `0x0005` | 2 | headphones |
| `0x0006` | 17 | not decoded; byte 15 changes between reads with nothing touched, bytes 10 to 13 read `80 bb 00 00` (48000, perhaps the sample rate) |
| `0x0016` | 51 | not decoded, new |

The firmware answers whatever length is asked for, so these are the
lengths Wave Link uses, not proven block sizes.

## What Wave Link records about its own mode

Wave Link 3.5.0.4892 (Store package `Elgato.WaveLink_g54w8ztgkx496`) says
in its own files that it processes the MK.2 family on the computer. Paths
below are under `%LOCALAPPDATA%\Packages\Elgato.WaveLink_g54w8ztgkx496\LocalState`
unless they name the install folder.

### The log

`Logs\ElgatoWaveLink_<date-time>.log`, one file per launch.

- `ProSoftwareHardwareMixerTransitionHelper` writes the hardware mixing
  state on every start:

  ```
  CheckForXLRProInitialization: XLRProConnectionState=NoneConnected, XLRProInitState=NotStarted, IsHardwareMixingEnabled=False.
  Hardware mixing state changed to: False.
  ```

  The flag is `IsHardwareMixingEnabled`, and the state around it is named
  for the Wave XLR Pro only (`XLRProConnectionState`, `XLRProInitState`).
  Nothing comparable is logged for the MK.2 models.
- `EWLWLegacyThesyconBackend::open` opens Wave Link's virtual device
  (USB product id `0x96`) with `Use device with PID: 0x96 and handle: ...
  for software mixing`.
- `PluginHost` builds a plugin rack for the dock's input and names it:

  ```
  LEWITT plugin rack initialized <Wave XLR MKII Host>
  Added default plugin: <PCM_IN_01_C_00_SD1 - WaveEQ>, <Wave XLR MKII Host>
  ```

  The input `PCM_IN_01_C_00_SD1` gets ElgatoSampleRecorder, VoiceFocus,
  WaveCompressor, WaveEQ and WaveExpander as plugins on the computer.
- `DeviceSupportService` logs every parameter it hands to the device
  layer as `Set DeviceParameter: <name> to: <value>`. Through the whole
  session it named only `Band1_QFactor` to `Band4_QFactor`,
  `Comp_ThresholdDb`, `Exp_Ratio`, `HWOutputsSourceSelect`,
  `USBOutputsSourceSelect`, `IsSampleRecorderOn` and `IsVSTEnabled`, all
  at connect. None of them produced a vendor write on the wire, and no
  band gain, frequency, type or enable was ever handed down.

### The plugin graph

`AudioPluginCache\graph_visualization.md` (the same data in
`graph_visualization.json`) is the host's own description of its signal
flow:

```
# EWLWPluginHost - Audio Signal Flow
**Host Mode:** Wave XLR MKII Host
Audio Input -> ElgatoSampleRecorder -> ElgatoAiCoustics -> WaveCompressor -> WaveEQ -> WaveExpander -> Audio Output
```

`Host Mode: Wave XLR MKII Host` is the mode in which the MK.2 family's
processing runs in Wave Link's plugin host on the computer.

### The program files

In the install folder
(`C:\Program Files\WindowsApps\Elgato.WaveLink_3.5.0.4892_x64__g54w8ztgkx496`),
read with `strings` and `objdump` from a copy; nothing was run.

- `EWLWPluginManager.dll` is the JUCE plugin host with WaveEQ,
  WaveCompressor and WaveExpander built in. WaveEQ has a global
  `enabled` and four bands of `Band N Filter Enabled`, `Type`,
  `Frequency` (20 Hz to 20 kHz, 1 Hz steps), `Gain` (-12 to +12 dB,
  0.25 dB steps) and `Quality` (log10 of Q, -1 to +1, steps of 0.016).
  The six types are 0 high pass, 1 low shelf, 2 bell (named "Band Pass"),
  3 notch, 4 high shelf and 5 low pass.
- `waveapi.dll`, the device API, describes each model's parameter tree.
  EQ trees appear at the root, under `/mic/`, and once per XLR input of
  the Wave XLR Pro under `/xlr1/` and `/xlr2/`. Each has
  `equalizer_enabled` and, for bands 0 to 3, `type`, `enabled`, `gain_dB`,
  `frequency_Hz` and `quality_log10`; the band type enum is
  `ven_signal_dsp_fx_band_type_e`. The same file lists signal taps per
  input (`XLR1_PREFX`, `XLR1_POSTDSP`, `XLR1_POSTVST`) and an output source
  select "0 - XLR pre 1 - XLR post 2 - XLR VST 3 - Monitor Mix", which is
  what `HWOutputsSourceSelect` and `USBOutputsSourceSelect` set. Which
  models own the root and `/mic/` trees is not established; the bands the
  MK.2 models store but do not apply are a likely match.
- `Elgato.WaveLink.AppLogic.dll` names the same fields `Eq_Enabled`,
  `BandN_Type`, `BandN_IsEnabled`, `BandN_FrequencyHz`, `BandN_GainDb` and
  `BandN_QFactor` ("Band index must be between 1 and 4"). Hardware mixing
  and settings sync to the device are named for the Wave XLR Pro only.

### The local API

Wave Link 3's local JSON-RPC API, which Stream Deck style plugins use,
separates software `effects` (such as `"eq"`) from device `dspEffects`
(such as `"clipguard"`) and reports each only as
`{id, name, isEnabled, isSupported}`
([WaveLink.Client protocol notes](https://github.com/Agash/WaveLink.Client/blob/main/PROTOCOL.md)).
The EQ is in the software list.

## What Wave Link sends when the EQ changes

| Capture | Action in Wave Link | Vendor writes | Block `0x0004` afterwards |
|---|---|---|---|
| 01 | moved band 1 of the dock input's EQ for about 50 s | none | unchanged |
| 02 | toggled the dock input's EQ off and on, twice | none | unchanged |
| 03 | quit and relaunched Wave Link with the EQ on (41, 310, 2172 and 15049 Hz) | none | byte for byte as in the connect capture |
| 04 | quit and relaunched with the EQ at 130, 792, 3193 and 6238 Hz, ±9.25 dB, then enabled the EQ by hand | none | byte for byte as in the connect capture |

No `Set DeviceParameter` line appeared in Wave Link's log for any of these
actions, and nothing in the `0x02` stream behaved like a parameter: 815
of its byte offsets were constant zero padding and the other 800 changed
on nearly every transfer.

## The EQ values stored in block 0x0004

Bytes 18 to 37 hold four groups of five bytes, one per band:

| Bytes | Band | Stored value |
|---|---|---|
| 18 to 22 | 1 | `32 00 64 00 f6` |
| 23 to 27 | 2 | `34 00 f4 01 f6` |
| 28 to 32 | 3 | `34 00 cc 07 f6` |
| 33 to 37 | 4 | `28 00 e9 13 f6` |

Read as `[A] [gain] [frequency, 16-bit little endian, Hz] [Q]`, the four
frequencies are 100, 500, 1996 and 5097 Hz. Those match an EQ state
Wave Link held earlier the same morning, so something wrote Wave Link's
EQ into the dock at some point. None of the captures above shows Wave
Link 3.5 doing it, and the values stayed the same through every capture.
The Q byte `f6` (-10) fits log10(0.705) × 64, which would match Wave
Link's default Q of 0.705; that and the meaning of byte A (`32 34 34 28`
for band types 1, 2, 2 and 4) are guesses.

## Writing the bands from Linux

Wave Link never writes these bytes, so the only way to test them was to
write them ourselves and measure. Every write was a read-modify-write of
the 38-byte block at bank `0x0103`, read back after writing, and the
original bytes went back at the end of each run.

The signal path: pink noise from the dock's headphone output into an
earbud taped about 1 cm in front of the microphone, recorded from the
dock's input. Both streams were created with `node.autoconnect = false`
and linked by hand, because OpenXLR's application routing otherwise moves
an untargeted stream into its own channels. For the runs the dock's
compressor, expander, voice tune, low cut and ClipGuard were off and its
gain was 55 dB. Each value was compared with a flat run taken just before
it, so slow drift in the room cancels.

Above 3 kHz the earbud was about 15 dB above the room and two identical
runs agreed within about 0.1 dB. Below 3 kHz the room was as loud as the
earbud, so results there are not evidence either way.

| Write (block `0x0004`) | Measured effect, 3.8 to 9.7 kHz |
|---|---|
| band 4 gain (byte 34) `0x18`, processing bits off | within ±0.1 dB |
| band 4 gain `0x18`, compressor, expander, voice tune and low cut bits on | within ±0.2 dB |
| band 4 byte A (byte 33) with bit `0x10` set, gain `0x18` | within ±0.2 dB |
| band 4 byte A `0xff` | within ±0.3 dB |
| band 3 moved to 5000 Hz (bytes 30 and 31) with gain (byte 29) `0x18` | within ±0.4 dB |
| band 4 gain `0x18` with Q (byte 37) `0x40` | up to 1 dB, not consistent across frequency |

A screen inverted each of bytes 3 to 17 in turn with band 4 boosted.
Inverting byte 11 (`ff` to `00`) showed one rise of 6.8 dB around 4.8 kHz,
but a retest of byte 11 values (`00`, `f7`, `ef`, `fe`, `7f`, `f0`, `0f`)
with band 4 boosted did not reproduce it. The room had become louder by
then, so the retest's repeatability was only 2 to 5.6 dB. The retest did
not set byte A at the same time as the screen did; that combination was
covered by the listening test below. Bytes 4, 8, 9, 10, 12, 13, 14, 15
and 17 changed the overall level by 5 to 19 dB, so they are live
parameters, most likely for the compressor or expander. They are not
mapped.

Earlier, from the Windows side with Wave Link closed, writing the low cut
bit made rumble audible on the dock's USB capture and setting it again
removed it, which confirms that writes to this block reach the DSP. A
band 3 gain of `0x0c` was heard as brighter in that session; the
measurements above did not confirm it.

## Listening on the zero-latency path

The equalizer might act only on the dock's own headphone monitor, which
the USB recording cannot show. With the crossfade moved fully to the
microphone and the headphone level at its usual -20 dB, a timed sequence
switched every 5 s while the listener made a steady "ssss" into the
microphone: flat, band 4 moderate (byte 11 `00`, byte 33 `0x38`, byte 34
`0x0c`), flat, the same again, flat, band 4 large (byte 34 `0x18`), flat,
band 3 large (byte 11 `00`, byte 29 `0x18`). The listener heard no
difference at any step.

## The Wave XLR MK.2

| Item | Value |
|---|---|
| Device | Wave XLR MK.2, `0fd9:00b6`, `bcdDevice 4.00` |
| Firmware | build string `2026-02-16-09:46-ZP-Release` in block `0x0000`; `c1 00 0000 0003` answers `02 05` (the dock answers `01 0d`) |
| Control bank | `0x0203`; every block stalls at `0x0103` |
| Blocks | `0x0000`, `0x0001`, `0x0002`, `0x0004`, `0x0005`, `0x0006` and `0x0016` answer at the dock's lengths |

This unit holds an EQ that is not flat, which makes a clean test: if the
device applied it, zeroing its gains would have to change the sound.
Read with the dock's layout:

| Band | A | Gain byte | Frequency | Q byte |
|---|---|---|---|---|
| 1 | `01` | `00` | 60 Hz | `f6` |
| 2 | `03` | `0d` | 227 Hz | `00` |
| 3 | `05` | `f4` | 860 Hz | `f2` |
| 4 | `25` | `07` | 10237 Hz | `e8` |

Byte 11 reads `f4` here and `ff` on the dock.

The measurement used the same method as for the dock: pink noise from the
MK.2's headphone output into an earbud taped in front of a Wave DX on
its XLR input, processing off, gain 55 dB, the crossfade on the PC side
and the headphone level raised for the run. From 3.4 to 10 kHz the
earbud was 30 to 35 dB above the room and three repeats of each
comparison agreed within 0.1 dB. Below 1.7 kHz the room was within
10 dB of the earbud and the comparisons there are noise.

| Comparison (block `0x0004`) | Measured effect |
|---|---|
| stored EQ against all four gains (bytes 19, 24, 29, 34) at zero | -0.02 dB at 10237 Hz, where band 4 stores `07` |
| band 4 moved to 5000 Hz with gain `0x18`, against the same at gain 0 | 0.00 dB at 5000 Hz |
| the same with byte 11 at `00` | -0.01 dB at 5000 Hz |
| the same with byte 11 at `ff` | +0.01 dB at 5000 Hz |

On the zero-latency path the listener set gain, crossfade and headphone
level on the device until they heard themselves clearly (gain 47 dB,
crossfade 100, headphone byte `2d`). A timed sequence then changed only
the EQ bytes every 5 s: stored EQ, gains zeroed, stored, zeroed, band 4
at 5 kHz with gain `0x18` and byte 11 `00`, zeroed, the same boost,
stored. The listener heard absolutely no difference. With +13 raw steps
at 227 Hz and -12 at 860 Hz stored, an applied EQ would have changed the
voice audibly.

## What this means for OpenXLR

- On these two devices and firmware builds there is no hardware equalizer
  for OpenXLR to drive. A microphone EQ belongs in software, as Wave Link does it.
  OpenXLR's built-in four-band [microphone EQ](api.md#microphone-eq)
  runs in software for that reason.
- The Wave XLR Pro may still run its equalizer on the device. Elgato's
  feature guide describes it that way, Wave Link has hardware mixing
  and settings sync for the Pro only, and `waveapi.dll` carries an EQ tree
  per Pro XLR input with the same fields as WaveEQ. Its protocol has not
  been captured for the EQ; those trees and the per-input taps are the
  leads for that capture.
- Still open: dock firmware 1.0.15, a Wave Link capture with the Wave
  XLR MK.2 attached, bytes 3 to 17 of block `0x0004`, and a quiet-room
  rerun of byte 11 together with byte A on the dock's USB recording.
