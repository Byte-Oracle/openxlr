pragma ComponentBehavior: Bound

import QtQuick
import QtTest
import "../openxlr.mixer" as Plugin
import "../../../tools/omarchy-qml-imports/qs/Commons" as Commons

TestCase {
    id: testCase
    name: "Omarchy bar"
    when: windowShown
    visible: true
    width: 800
    height: 100
    property var bar: null

    QtObject {
        id: fakeLink
        property bool ready: true
        property var levels: ({})
        property var snapshot: null
    }
    Component {
        id: barComponent
        Plugin.BarContent {
            width: implicitWidth
            height: 26
            link: fakeLink
            foreground: Commons.Color.foreground
            background: Commons.Color.background
            fontFamily: Commons.Style.font.family
            pixelSize: Commons.Style.font.body
        }
    }
    function snapshotFor(muted: bool): var {
        return {
            state: {
                mute: muted,
                mute2: false
            },
            mixer: {
                channels: [
                    {
                        id: "xlr1",
                        name: "XLR 1",
                        hardware: true
                    },
                    {
                        id: "xlr2",
                        name: "XLR 2",
                        hardware: true
                    }
                ],
                mixes: [
                    {
                        id: "monitor",
                        name: "Monitor A",
                        kind: "monitor"
                    },
                    {
                        id: "monitor2",
                        name: "Monitor B",
                        kind: "monitor"
                    }
                ],
                monitorOutputs: ["headset"]
            }
        };
    }
    function init(): void {
        fakeLink.ready = true;
        fakeLink.levels = ({});
        fakeLink.snapshot = snapshotFor(false);
        bar = createTemporaryObject(barComponent, testCase);
        verify(bar !== null);
        tryCompare(bar, "implicitWidth", findChild(bar, "feed").width);
        failOnWarning(/TypeError|Cannot assign/);
    }
    function test_onlyInputsWithSignalOccupySpace(): void {
        var first = findChild(bar, "input-xlr1");
        var second = findChild(bar, "input-xlr2");
        compare(first.visible, false);
        compare(second.visible, false);
        var feedOnlyWidth = bar.implicitWidth;
        fakeLink.levels = {
            "ch:xlr2": [0.3, 0.3]
        };
        wait(0);
        compare(first.visible, false);
        compare(second.visible, true);
        compare(findChild(second, "label").text, "XLR 2");
        tryVerify(function () {
            return bar.implicitWidth > feedOnlyWidth;
        });
    }
    function test_fourMicrophonesFollowLayoutOrderAndKeepTheHold(): void {
        var snapshot = snapshotFor(false);
        snapshot.mixer.channels = [
            { id: "guest", name: "Guest", microphone: true },
            { id: "xlr2", name: "XLR 2" },
            { id: "line", name: "Line", microphone: false },
            { id: "usb", name: "USB mic", microphone: true },
            { id: "xlr1", name: "XLR 1" },
            { id: "fifth", name: "Fifth", microphone: true },
            { id: "old", name: "Old capture", captureSource: "usb" }
        ];
        fakeLink.snapshot = snapshot;
        fakeLink.levels = {
            "ch:guest": [0.2, 0.7], "ch:xlr2": [0.3, 0.3], "ch:line": [0.8, 0.4],
            "ch:usb": [0.5, 0.5], "ch:xlr1": [0.6, 0.6], "ch:fifth": [0.4, 0.4], "ch:old": [0.8, 0.8]
        };
        wait(0);
        compare(bar.shownInputs.join(","), "guest,xlr2,usb,xlr1");
        var previous = -1;
        for (const id of bar.shownInputs) {
            const input = findChild(bar, "input-" + id);
            compare(input.visible, true);
            tryVerify(function () { return input.x > previous; });
            previous = input.x;
            compare(findChild(input, "meter").lanes, 1);
        }
        compare(findChild(bar, "input-guest").level, 0.7);
        for (const id of ["line", "fifth", "old"])
            compare(findChild(bar, "input-" + id).visible, false);
        fakeLink.levels = ({});
        compare(bar.shownInputs.join(","), "guest,xlr2,usb,xlr1");
        // Expire the first microphone without making the other three wait.
        var seen = Object.assign({}, bar.lastActive);
        seen.guest = Date.now() - 10000;
        bar.lastActive = seen;
        bar.observe(false);
        compare(bar.shownInputs.join(","), "xlr2,usb,xlr1,fifth");
        fakeLink.ready = false;
        compare(bar.shownInputs.length, 0);
        compare(findChild(bar, "release").running, false);
    }
    function test_anInputTheDeviceCannotFeedNeverAppears(): void {
        // Only the Wave XLR Pro has a second jack, so the daemon marks XLR 2
        // absent on every other model. Signal on an absent channel must not
        // put it on the bar.
        var snapshot = snapshotFor(false);
        snapshot.mixer.channels[1].present = false;
        fakeLink.snapshot = snapshot;
        fakeLink.levels = {
            "ch:xlr2": [0.4, 0.4]
        };
        wait(0);
        compare(findChild(bar, "input-xlr2").visible, false);
        fakeLink.levels = {
            "ch:xlr1": [0.4, 0.4]
        };
        wait(0);
        compare(findChild(bar, "input-xlr1").visible, true);
    }

    function test_labelsAndMetersShareTheirVerticalCentre(): void {
        fakeLink.levels = {
            "ch:xlr1": [0.4, 0.4],
            "mix:monitor": [0.2, 0.7]
        };
        wait(0);
        for (const name of ["input-xlr1", "feed"]) {
            const readout = findChild(bar, name);
            const label = findChild(readout, "label");
            const meter = findChild(readout, "meter");
            verify(Math.abs(label.y + label.height / 2 - meter.y - meter.height / 2) <= 1);
            compare(label.font.pixelSize, Commons.Style.font.body);
            compare(meter.pixelSize, label.font.pixelSize);
            compare(label.font.bold, false);
            compare(meter.meterPalette, null);
            for (const child of meter.children) {
                if (!("glyph" in child))
                    continue;
                compare(child.verticalAlignment, Text.AlignVCenter);
                compare(child.font.bold, false);
                verify(Math.abs(child.y + child.height / 2 - meter.height / 2) <= 1);
            }
        }
    }
    function test_muteDimsTheInputWithoutChangingItsWidth(): void {
        fakeLink.levels = {
            "ch:xlr1": [0.5, 0.5]
        };
        wait(0);
        tryCompare(bar, "implicitWidth", findChild(bar, "input-xlr1").width + 10 + findChild(bar, "feed").width);
        var width = bar.implicitWidth;
        fakeLink.snapshot = snapshotFor(true);
        fakeLink.levels = {
            "ch:xlr1": [0, 0]
        };
        wait(0);
        var input = findChild(bar, "input-xlr1");
        compare(input.visible, true);
        compare(input.opacity, 0.45);
        compare(findChild(input, "label").text, "XLR 1");
        compare(findChild(input, "label").font.bold, false);
        compare(bar.implicitWidth, width);
    }
    function test_signalOffExpiresAfterTenSeconds(): void {
        var input = findChild(bar, "input-xlr1");
        fakeLink.levels = {
            "ch:xlr1": [0.5, 0.5]
        };
        var firstSeen = bar.lastActive.xlr1;
        wait(40);
        // Even an unchanged positive reading rearms the single release timer.
        fakeLink.levels = {
            "ch:xlr1": [0.5, 0.5]
        };
        verify(bar.lastActive.xlr1 > firstSeen);
        var release = findChild(bar, "release");
        compare(release.interval, 10000);
        compare(release.repeat, false);
        fakeLink.levels = ({});
        wait(9500);
        compare(input.visible, true);
        tryCompare(input, "visible", false, 1500);
        compare(release.running, false);
    }
    function test_monitorLabelAndMeterFollowTheSelectedFeed(): void {
        fakeLink.levels = {
            "mix:monitor": [0.2, 0.8],
            "mix:monitor2": [0.9, 0.1]
        };
        var feed = findChild(bar, "feed");
        compare(findChild(feed, "label").text, "Mon A");
        compare(findChild(feed, "meter").leftLevel, 0.8);
        var next = snapshotFor(false);
        next.mixer.monitorOutputs.push("speakers");
        next.mixer.monitorFeeds = {
            headset: "monitor2"
        };
        fakeLink.snapshot = next;
        compare(findChild(feed, "label").text, "Mon B +1");
        compare(findChild(feed, "meter").leftLevel, 0.9);
    }
    function test_disconnectClearsInputsAndToleratesANullSnapshot(): void {
        fakeLink.levels = {
            "ch:xlr1": [0.5, 0.5]
        };
        fakeLink.snapshot = null;
        fakeLink.ready = false;
        compare(bar.shownInputs.length, 0);
        compare(findChild(bar, "release").running, false);
        fakeLink.ready = true;
        fakeLink.levels = ({});
        compare(bar.shownInputs.length, 0);
    }
    function test_labelsAreTheDaemonsChannelNames(): void {
        // A Wave:3's capsule, or a strip bound to another microphone, is Mic 1.
        var snapshot = snapshotFor(false);
        snapshot.mixer.channels[0].name = "Mic 1";
        fakeLink.snapshot = snapshot;
        fakeLink.levels = {
            "ch:xlr1": [0.4, 0.4]
        };
        wait(0);
        compare(findChild(findChild(bar, "input-xlr1"), "label").text, "Mic 1");
    }
    function test_theInterfacesMuteDimsOnlyTheStripThatCarriesItsInput(): void {
        // XLR 1 is bound to another microphone and the interface's first input
        // plays on a capture channel: its mute leaves XLR 1 undimmed. XLR 2
        // still carries input 2 and dims with mute2.
        var snapshot = snapshotFor(true);
        snapshot.state.mute2 = true;
        snapshot.mixer.channels[0].name = "Mic 1";
        snapshot.mixer.channels[0].binding = {
            kind: "source",
            source: "alsa_input.usb-Yeti",
            pair: 0,
            monoChannel: 0
        };
        snapshot.mixer.channels[1].unitInput = {
            unit: "0fd9:00b4@0123456789abcdef",
            input: 2,
            primary: true
        };
        snapshot.mixer.channels.push({
            id: "cap1",
            name: "Pro mic",
            captureSource: "alsa_input.usb-Elgato_Wave_XLR_Pro",
            unitInput: {
                unit: "0fd9:00b4@0123456789abcdef",
                input: 1,
                primary: true
            }
        });
        fakeLink.snapshot = snapshot;
        fakeLink.levels = {
            "ch:xlr1": [0.4, 0.4],
            "ch:xlr2": [0.4, 0.4]
        };
        wait(0);
        compare(findChild(bar, "input-xlr1").muted, false);
        compare(findChild(bar, "input-xlr1").opacity, 1);
        compare(findChild(bar, "input-xlr2").muted, true);
        // Back on the jack, XLR 1 carries input 1 and its mute dims it again.
        var unbound = snapshotFor(true);
        unbound.mixer.channels[0].unitInput = {
            unit: "0fd9:00b4@0123456789abcdef",
            input: 1,
            primary: true
        };
        fakeLink.snapshot = unbound;
        compare(findChild(bar, "input-xlr1").muted, true);
    }
}
