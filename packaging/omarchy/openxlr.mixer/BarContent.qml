pragma ComponentBehavior: Bound

import QtQuick
import "Mixer.js" as Mixer

Item {
    id: root
    required property var link
    required property color foreground
    required property color background
    required property string fontFamily
    required property int pixelSize
    readonly property var mixer: link.snapshot && link.snapshot.mixer ? link.snapshot.mixer : ({})
    readonly property var channels: Mixer.shownChannels(mixer.channels)
    readonly property var mixes: mixer.mixes || []
    readonly property var feeds: Mixer.monitorFeeds(mixer)
    readonly property var feed: feeds.length ? feeds[0] : null
    readonly property string feedLabel: {
        if (!link.ready)
            return "XLR off";
        if (!feed)
            return "No output";
        var names = feed.ids.map(function (id) {
            var mix = root.mixes.filter(function (entry) {
                return entry.id === id;
            })[0];
            return Mixer.barName(mix ? mix.name : id);
        });
        return (names.join(" + ") || feed.name) + (feeds.length > 1 ? " +" + (feeds.length - 1) : "");
    }
    implicitWidth: readouts.implicitWidth
    implicitHeight: metrics.height

    property var lastActive: ({})
    property var shownInputs: []

    function observe(signal: bool): void {
        release.stop();
        const now = Date.now();
        const seen = {};
        var next = Mixer.inputHoldMs;
        if (link.ready) {
            channels.forEach(function (channel) {
                if (!Mixer.mono(channel))
                    return;
                var time = root.lastActive[channel.id];
                if (signal && Mixer.pair(root.link.levels, "ch:" + channel.id, true)[0] > 0)
                    time = now;
                if (typeof time === "number" && now - time < Mixer.inputHoldMs) {
                    seen[channel.id] = time;
                    next = Math.min(next, time + Mixer.inputHoldMs - now);
                }
            });
        }
        lastActive = seen;
        shownInputs = Mixer.shownInputs(channels, seen, now);
        if (Object.keys(seen).length) {
            release.interval = Math.max(1, next);
            release.start();
        }
    }
    Timer {
        id: release
        objectName: "release"
        repeat: false
        onTriggered: root.observe(false)
    }
    Connections {
        target: root.link
        function onLevelsChanged(): void {
            root.observe(true);
        }
        function onReadyChanged(): void {
            root.observe(true);
        }
    }
    onChannelsChanged: observe(false)
    Component.onCompleted: observe(true)

    FontMetrics {
        id: metrics
        font.family: root.fontFamily
        font.pixelSize: root.pixelSize
    }
    component Readout: Item {
        id: readout
        required property string text
        property real level: 0
        property bool muted: false
        implicitWidth: Math.ceil(label.width + 6 + meter.implicitWidth)
        width: implicitWidth
        height: metrics.height
        // Match the bar's regular text. Mute dims both label and meter to 45%
        // instead of changing font weight or removing the input.
        opacity: muted ? 0.45 : 1
        Text {
            id: label
            objectName: "label"
            anchors.verticalCenter: parent.verticalCenter
            width: Math.min(metrics.advanceWidth(readout.text), root.pixelSize * 10)
            height: metrics.height
            text: readout.text
            textFormat: Text.PlainText
            font.family: root.fontFamily
            font.pixelSize: root.pixelSize
            font.bold: false
            color: root.foreground
            verticalAlignment: Text.AlignVCenter
            elide: Text.ElideRight
        }
        BlockMeter {
            id: meter
            objectName: "meter"
            x: label.width + 6
            anchors.verticalCenter: parent.verticalCenter
            foreground: root.foreground
            background: root.background
            fontFamily: root.fontFamily
            pixelSize: root.pixelSize
            leftLevel: readout.level
            emphasizeHot: false
        }
    }

    Row {
        id: readouts
        anchors.verticalCenter: parent.verticalCenter
        spacing: 10
        Repeater {
            model: root.mixer.channels || []
            Readout {
                required property var modelData
                objectName: "input-" + modelData.id
                visible: root.shownInputs.indexOf(modelData.id) >= 0
                text: Mixer.channelName(root.channels, modelData.id)
                level: Mixer.pair(root.link.levels, "ch:" + modelData.id, true)[0]
                muted: Mixer.hardwareMuted(root.link.snapshot, modelData.id)
            }
        }
        Readout {
            objectName: "feed"
            text: root.feedLabel
            // One lane shows the louder stereo channel. For a summed feed it
            // shows the loudest constituent mix, not a guessed sum of dB values.
            level: root.link.ready && root.feed ? Mixer.feedLevel(root.link.levels, root.feed.ids) : 0
            opacity: root.link.ready ? 1 : 0.45
        }
    }
}
