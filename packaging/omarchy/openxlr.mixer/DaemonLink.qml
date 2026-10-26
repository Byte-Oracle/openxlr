pragma ComponentBehavior: Bound

import QtQuick
import Quickshell.Io
import "Mixer.js" as Mixer

QtObject {
    id: root
    property bool ready: false
    property var snapshot: null
    property var levels: ({})
    property string error: ""
    property string offline: ""
    property bool pending: false
    property var session: null

    function send(command: var): void {
        if (session)
            session.send(command);
    }

    function sync(): void {
        ready = session.ready;
        snapshot = session.snapshot;
        levels = session.levels;
        error = session.error;
        offline = session.offline;
        pending = session.pending !== "";
    }

    function arm(timer: Timer, ms: int): void {
        timer.stop();
        if (ms > 0) {
            timer.interval = ms;
            timer.start();
        }
    }

    property Process bridge: null
    property Component bridgeFactory: Component {
        Process {
            id: process
            command: ["openxlr-control"]
            stdinEnabled: true
            stdout: SplitParser {
                onRead: data => {
                    if (root.bridge !== process || !root.session)
                        return;
                    let message;
                    try { message = JSON.parse(data); } catch (_) { root.session.lost(); return; }
                    if (message.type === "controlReady")
                        root.session.opened();
                    else if (message.type === "controlError")
                        root.session.refused(message.message);
                    else
                        root.session.receive(data);
                }
            }
            onRunningChanged: if (!running && root.bridge === process && root.session)
                root.session.lost()
        }
    }
    property Timer retry: Timer {
        onTriggered: root.session.start()
    }
    property Timer deadline: Timer {
        onTriggered: root.session.lost()
    }
    property Timer meterDeadline: Timer {
        onTriggered: root.session.metersExpired()
    }
    property Timer commandDeadline: Timer {
        onTriggered: root.session.commandExpired()
    }

    Component.onCompleted: {
        session = new Mixer.Session({
            connect: function () {
                root.bridge = root.bridgeFactory.createObject(root);
                root.bridge.running = true;
            },
            send: function (value) {
                if (root.bridge)
                    root.bridge.write(JSON.stringify(value) + "\n");
            },
            close: function () {
                const previous = root.bridge;
                root.bridge = null;
                if (previous) {
                    previous.running = false;
                    previous.destroy();
                }
            },
            changed: function () {
                root.sync();
            },
            retry: function (ms) {
                root.arm(root.retry, ms);
            },
            deadline: function (ms) {
                root.arm(root.deadline, ms);
            },
            meterDeadline: function (ms) {
                root.arm(root.meterDeadline, ms);
            },
            commandDeadline: function (ms) {
                root.arm(root.commandDeadline, ms);
            },
            cancelTimers: function () {
                root.retry.stop();
                root.deadline.stop();
                root.meterDeadline.stop();
                root.commandDeadline.stop();
            }
        });
        session.start();
    }
    Component.onDestruction: if (session)
        session.stop()
}
