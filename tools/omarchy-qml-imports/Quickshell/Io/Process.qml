import QtQml

// External Quickshell type declaration for linting, not a process runtime.
QtObject {
    property list<string> command: []
    property bool running: false
    property bool stdinEnabled: false
    property QtObject stdout: null
    function write(data: string): void {}
}
