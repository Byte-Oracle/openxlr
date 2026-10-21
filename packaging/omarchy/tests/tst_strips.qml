pragma ComponentBehavior: Bound

import QtQuick
import QtTest
import "../openxlr.mixer" as Plugin

TestCase {
    id: testCase
    name: "Omarchy strips"
    when: windowShown
    visible: true
    width: 200
    height: 320
    property var sent: []
    property var strip: null
    property var channel: ({
            id: "xlr1",
            name: "XLR 1",
            hardware: true,
            levels: {
                monitor: 0.4
            },
            mutedIn: []
        })

    QtObject {
        id: fakeLink
        property bool ready: true
        property bool pending: false
        property var levels: ({
                "ch:xlr1": [0.4, 0.4]
            })
        property var snapshot: testCase.unboundSnapshot()
        function send(command: var): void {
            testCase.sent.push(command);
        }
    }
    Component {
        id: stripComponent
        Plugin.MixerStrip {
            entry: testCase.channel
            mix: "monitor"
            link: fakeLink
            foreground: "#eeeeee"
            face: "#111111"
            family: "monospace"
        }
    }
    // XLR 1 on the interface's jack, carrying its first input.
    function unboundSnapshot(): var {
        return {
            connected: true,
            state: {
                mute: false
            },
            capabilities: {
                mute: true
            },
            mixer: {
                channels: [
                    {
                        id: "xlr1",
                        name: "XLR 1",
                        hardware: true,
                        binding: {
                            kind: "unit",
                            pair: 0
                        },
                        unitInput: {
                            unit: "0fd9:00b4@0123456789abcdef",
                            input: 1,
                            primary: true
                        }
                    }
                ]
            }
        };
    }
    // XLR 1 bound to a Yeti Nano; the interface's first input plays on a capture channel.
    function boundSnapshot(): var {
        return {
            connected: true,
            state: {
                mute: true
            },
            capabilities: {
                mute: true
            },
            devices: [
                {
                    name: "alsa_input.usb-Yeti",
                    description: "Yeti Nano",
                    kind: 1
                }
            ],
            mixer: {
                channels: [
                    {
                        id: "xlr1",
                        name: "Mic 1",
                        hardware: true,
                        binding: {
                            kind: "source",
                            source: "alsa_input.usb-Yeti",
                            pair: 0,
                            monoChannel: 1
                        },
                        bindingConnected: true
                    },
                    {
                        id: "cap1",
                        name: "Pro mic",
                        captureSource: "alsa_input.usb-Elgato_Wave_XLR_Pro",
                        captureConnected: true,
                        binding: {
                            kind: "source",
                            source: "alsa_input.usb-Elgato_Wave_XLR_Pro",
                            pair: 0,
                            monoChannel: 0
                        },
                        unitInput: {
                            unit: "0fd9:00b4@0123456789abcdef",
                            input: 1,
                            primary: true
                        }
                    }
                ]
            }
        };
    }
    function init(): void {
        sent = [];
        fakeLink.ready = true;
        fakeLink.pending = false;
        fakeLink.snapshot = unboundSnapshot();
        strip = createTemporaryObject(stripComponent, testCase);
        verify(strip !== null);
    }
    function test_releaseSendsOneValueAndExternalStateRestoresTheFader(): void {
        var fader = findChild(strip, "volume");
        compare(fader.value, 0.4);
        mousePress(fader, 16, 95);
        mouseMove(fader, 16, 25, 20);
        compare(sent.length, 0);
        mouseRelease(fader, 16, 25);
        compare(sent.length, 1);
        compare(sent[0].cmd, "setLevel");
        compare(sent[0].channel, "xlr1");
        compare(sent[0].mix, "monitor");
        verify(sent[0].value > 0.6);
        strip.entry = {
            id: "xlr1",
            name: "XLR 1",
            levels: {
                monitor: 0.2
            },
            mutedIn: []
        };
        compare(fader.value, 0.2);
    }
    function test_keyboardChangesOnePercentagePoint(): void {
        var fader = findChild(strip, "volume");
        fader.forceActiveFocus();
        keyClick(Qt.Key_Up);
        compare(sent.length, 1);
        fuzzyCompare(sent[0].value, 0.41, 0.0001);
    }
    function test_sendMuteAndHardwareMuteAreSeparateCommands(): void {
        mouseClick(findChild(strip, "sendMute"));
        mouseClick(findChild(strip, "hardwareMute"));
        compare(sent.length, 2);
        compare(sent[0].cmd, "setChannelMuted");
        compare(sent[0].mix, "monitor");
        compare(sent[1].cmd, "set");
        compare(sent[1].control, "mute");
        compare(sent[1].value, true);
    }
    function test_aBoundStripNamesItsSourceAndHasOnlyItsSendMutes(): void {
        var bound = boundSnapshot();
        fakeLink.snapshot = bound;
        strip.entry = bound.mixer.channels[0];
        var hardware = findChild(strip, "hardwareMute");
        compare(findChild(strip, "kind").text, "Yeti Nano");
        compare(strip.hardwareControl, "");
        compare(hardware.visible, false);
        verify(!hardware.enabled);
        mouseClick(findChild(strip, "sendMute"));
        compare(sent.length, 1);
        compare(sent[0].cmd, "setChannelMuted");
        compare(sent[0].channel, "xlr1");
        bound.mixer.channels[0].bindingConnected = false;
        fakeLink.snapshot = JSON.parse(JSON.stringify(bound));
        strip.entry = fakeLink.snapshot.mixer.channels[0];
        compare(findChild(strip, "kind").text, "Yeti Nano OFF");
    }
    function test_theInterfacesMuteFollowsItsInputToTheCaptureChannel(): void {
        var bound = boundSnapshot();
        fakeLink.snapshot = bound;
        strip.entry = bound.mixer.channels[1];
        var hardware = findChild(strip, "hardwareMute");
        compare(strip.hardwareControl, "mute");
        compare(hardware.visible, true);
        verify(hardware.enabled);
        compare(hardware.text, "Mic muted");
        compare(findChild(strip, "kind").text, "CAPTURE");
        mouseClick(hardware);
        compare(sent.length, 1);
        compare(sent[0].cmd, "set");
        compare(sent[0].control, "mute");
        compare(sent[0].value, false);
        // No interface connected: the button stays, disabled, and sends nothing.
        bound.connected = false;
        fakeLink.snapshot = JSON.parse(JSON.stringify(bound));
        verify(!hardware.enabled);
        mouseClick(hardware);
        compare(sent.length, 1);
    }
    function test_aStripOnADrivenAdditionalUnitMutesThatUnit(): void {
        var snapshot = unboundSnapshot();
        snapshot.waveInterfaces = [
            {
                id: "0fd9:0070@fedcba9876543210",
                active: false,
                enabled: true,
                connected: true,
                capabilities: {
                    mute: true
                },
                state: {
                    mute: false
                }
            }
        ];
        var mic2 = {
            id: "xlr2",
            name: "Mic 2",
            hardware: true,
            binding: {
                kind: "source",
                source: "alsa_input.usb-Elgato_Wave_3",
                pair: 0,
                monoChannel: 0,
                unit: "0fd9:0070@fedcba9876543210"
            },
            bindingConnected: true,
            unitInput: {
                unit: "0fd9:0070@fedcba9876543210",
                input: 1,
                primary: false
            }
        };
        snapshot.mixer.channels.push(mic2);
        fakeLink.snapshot = snapshot;
        strip.entry = mic2;
        var hardware = findChild(strip, "hardwareMute");
        compare(hardware.visible, true);
        verify(hardware.enabled);
        compare(hardware.text, "Mic on");
        mouseClick(hardware);
        compare(sent.length, 1);
        compare(sent[0].cmd, "setWaveControl");
        compare(sent[0].device, "0fd9:0070@fedcba9876543210");
        compare(sent[0].control, "mute");
        compare(sent[0].value, true);
    }
    function test_offlineAndPendingStripsCannotSend(): void {
        fakeLink.ready = false;
        mouseClick(findChild(strip, "sendMute"));
        mouseClick(findChild(strip, "hardwareMute"));
        compare(sent.length, 0);
        fakeLink.ready = true;
        fakeLink.pending = true;
        mouseClick(findChild(strip, "sendMute"));
        compare(sent.length, 0);
    }
    function test_captureMetersUseTheMicrophoneFlagAndOlderStateStaysStereo(): void {
        fakeLink.levels = { "ch:guest": [0.2, 0.7] };
        strip.entry = { id: "guest", name: "Guest", captureSource: "usb", microphone: true };
        compare(strip.mono, true);
        compare(strip.meter[0], 0.7);
        compare(strip.meter[1], 0.7);
        strip.entry = { id: "guest", name: "Guest", captureSource: "usb", microphone: false };
        compare(strip.mono, false);
        compare(strip.meter[0], 0.2);
        compare(strip.meter[1], 0.7);
        strip.entry = { id: "guest", name: "Guest", captureSource: "usb", captureMonoChannel: 0 };
        compare(strip.mono, false);
        strip.entry = { id: "xlr1", name: "XLR 1" };
        compare(strip.mono, true);
    }
    function test_monitorMasterReaches150Percent(): void {
        strip.master = true;
        strip.entry = {
            id: "monitor",
            name: "Monitor A",
            kind: "monitor",
            volume: 1.25,
            muted: false
        };
        var fader = findChild(strip, "volume");
        compare(fader.to, 1.5);
        compare(fader.value, 1.25);
        mouseClick(findChild(strip, "sendMute"));
        compare(sent[0].cmd, "setMixMuted");
        compare(sent[0].mix, "monitor");
    }
    function test_layoutChangeDuringADragDoesNotEditTheReplacementChannel(): void {
        var fader = findChild(strip, "volume");
        mousePress(fader, 16, 95);
        mouseMove(fader, 16, 25, 20);
        strip.entry = {
            id: "music",
            name: "Music",
            levels: {
                monitor: 0.2
            },
            mutedIn: []
        };
        mouseRelease(fader, 16, 25);
        compare(sent.length, 0);
        compare(fader.value, 0.2);
    }
}
