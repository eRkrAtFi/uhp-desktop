#!/bin/sh
# Publishes QemuManager to publish/ the same way package-portable.ps1's own
# first step does, with a hard guarantee around it: publish/data (the
# user's real portable VM configs/settings/disks, once portable.flag or an
# existing data\ folder opts this bundle into portable mode - see
# AppPaths.ResolveAppDataDir) must come out byte-for-byte identical to how
# it went in. `dotnet publish` has never been observed to touch it (it only
# copies its own known build-output files into the target directory), but
# "never observed to" is not the same guarantee as this script actually
# checking every time - which is the whole point of adding it, rather than
# continuing to trust that behavior implicitly.
set -e
cd "$(dirname "$0")"

PUBLISH_DIR="publish"
DATA_DIR="$PUBLISH_DIR/data"

snapshot() {
    if [ -d "$DATA_DIR" ]; then
        find "$DATA_DIR" -type f -exec sh -c 'printf "%s " "$1"; md5sum "$1" | cut -d" " -f1' _ {} \; | sort
    fi
}

BEFORE=$(snapshot)

echo "Publishing QemuManager (Release) to '$PUBLISH_DIR'..."
dotnet publish QemuManager/QemuManager.csproj -c Release -o "$PUBLISH_DIR"

AFTER=$(snapshot)

if [ "$BEFORE" != "$AFTER" ]; then
    echo ""
    echo "FAIL: $DATA_DIR changed during publish - this should never happen." >&2
    echo "--- before ---" >&2
    echo "$BEFORE" >&2
    echo "--- after ---" >&2
    echo "$AFTER" >&2
    exit 1
fi

echo ""
echo "OK: $DATA_DIR is unchanged ($(echo "$BEFORE" | grep -c . 2>/dev/null || echo 0) files checksummed before and after)."
