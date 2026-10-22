#!/bin/sh
# Install Ubuntu packages on a CI runner. The runner's mirror sometimes
# stalls: apt then waits on every package before falling back to
# archive.ubuntu.com, or an update hangs outright. Each attempt is bounded
# so a stalled one is retried instead of using up the job's time.
set -u
attempt=1
while :; do
    if timeout 120 sudo apt-get -o Acquire::http::Timeout=5 update &&
        timeout 300 sudo apt-get -o Acquire::http::Timeout=5 install -y --no-install-recommends "$@"; then
        exit 0
    fi
    [ "$attempt" -lt 3 ] || exit 1
    echo "Package install attempt $attempt failed or stalled; retrying." >&2
    # An install stopped while unpacking leaves dpkg half done.
    sudo dpkg --configure -a || true
    attempt=$((attempt + 1))
done
