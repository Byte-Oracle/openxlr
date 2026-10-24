# Reading an audio diagnostics archive

Collect diagnostics in Options, SUPPORT while the problem is happening, if
possible before restarting the daemon. Keep the affected application's audio
stream active during collection. Collection itself records no microphone or
output samples, opens no plugin and changes no routing, level or service.
Review the archive before sharing it. The [manual](manual.md#510-reporting-a-problem)
describes its privacy rules and all collected files.

## Start with the source outcomes

`collection.json` records each helper's time, exit status and interruption
flags. A missing program, timeout, truncated output or incomplete pipe is not
a successful empty graph. JSON source errors are in a separate `*-errors.txt`
file. `validJson` describes syntax, not whether audio is working.

`pw-dump` can append update arrays while the graph changes. The archive folds
complete batches with the same registry replacement and removal rules the
daemon uses, then redacts the resulting JSON. A broken or interrupted update
stream stays available as redacted text with its error sidecar. It is not
silently replaced by the last successful batch.

## Inspect the graph

`audio-graph.txt` is the readable inventory; `audio-graph.json` holds the same
facts for tools. Both are derived from the archived, redacted `pw-dump.json`.
There is no second live query, background sweep or repair. Registry ids refer
to that dump's objects, not to Linux processes.

| Field | Meaning |
| --- | --- |
| `available`, `note` | Whether inspection could read the graph. Unavailable counts are null, not zero. A valid empty graph has zero counts. |
| Object, node, port and link counts | Inventory after registry updates and removals have been applied. |
| `defaultClock` | The core's advertised rate and quantum defaults and bounds. |
| `clockSettings` | Global entries from the settings metadata, including forced rate and quantum. A forced value of zero means automatic selection. |
| `nodes` | Name, media class, state, reported error, incoming and outgoing link counts, and selected audio/rate/latency properties. |
| `links` | Both node and port endpoints, state, reported error and the format fields PipeWire provided. |
| `observations` | Reported node/link errors, unresolved, incomplete or inconsistent endpoints, isolated OpenXLR-named nodes and running streams without a link in their audio direction. |

Link counts include link objects in every state. An active link does not prove
that samples are reaching an application. A report without observations is
not proof of clean audio. Idle and suspended nodes, paused links and unused
virtual sinks can be normal. A missing endpoint can be a snapshot taken during a
change. The report describes those cases without declaring a permanent fault.

Node properties express requests or constraints. A different `audio.rate`,
`node.rate` or latency on two nodes can be handled by resampling and is not
classified as a defect. Link format fields are absent when PipeWire did not
provide them. For current per-node rate, quantum and error counters, compare
`pw-top.txt`; the advertised defaults alone are not a measurement of every
node's operating format.

Inspection is bounded to 16 MiB and 16,384 objects. A larger dump remains in
the archive but the derived report says inspection was unavailable. Displayed
property values are capped at 512 characters; the dump keeps their full
redacted values. Nested objects and arrays of more than 32 elements are not
copied as property values. Only the listed audio, format and clock fields
are copied into the derived report. Text output quotes names and errors so
control characters cannot create fake lines or terminal escape sequences.

## Correlate, rather than guess

For disappearing microphone audio, find the input, its processing nodes and
the application's capture stream in the node and link inventories. Check
whether an endpoint is absent, whether PipeWire reported an error, and whether
the application actually had a capture stream during collection. A dump taken
after recovery cannot show the previous broken path.

For glitches after an application opens its microphone, compare the stream
formats and `pw-top.txt` with `audio-journal.txt`. The three statistics batches
are short observations; cumulative errors alone do not identify a cause.

`audio-services.txt` describes loaded unit settings. `audio-processes.json`
describes the actual same-user processes and their file and locked-memory
limits. Reloading a unit does not raise an already running process's limits.
An unavailable process field is unknown; it is not evidence that the limit is
zero. Flatpak does not read host units or host process resources.

`journal.txt` or the Flatpak daemon logs include native host failures and the
last nonblank helper message. Plugin scan failures have separate saved logs
and discovery information described in the manual. A scan failure and a
running insert failure are different events.

If recovery is needed, collect first and note the action and time. A second
archive after recovery can help compare the graphs. No automatic causal
conclusion or automatic repair is made from either archive.
