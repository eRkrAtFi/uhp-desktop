#!/bin/sh
# Fails loudly if any option from a Kconfig-style fragment did not
# survive, unchanged, into the actual built .config - used for both the
# kernel's own config (linux-riscv-emu.config) and busybox's
# (busybox-riscv-emu.config), same mechanism either way. A Kconfig
# dependency that silently was not met (the option's own `depends on`
# unsatisfied, not a typo in this fragment) otherwise drops it with no
# warning at all - this is the difference between finding that out right
# here, by name, and finding it out three steps later as "boots, but no
# virtio-net" (or "mkfs.ext2: command not found") with nothing pointing
# at why.
#
# Usage: verify-config-fragment.sh <fragment-file> <built-.config>
set -e

FRAGMENT="$1"
ACTUAL="$2"

if [ -z "$FRAGMENT" ] || [ -z "$ACTUAL" ]; then
    echo "usage: $0 <fragment-file> <built-.config>" >&2
    exit 2
fi
if [ ! -f "$FRAGMENT" ] || [ ! -f "$ACTUAL" ]; then
    echo "verify-config-fragment: '$FRAGMENT' or '$ACTUAL' does not exist" >&2
    exit 2
fi

fail=0
while IFS= read -r line; do
    case "$line" in
        ""|"#"*[!' ']*"is not set") ;; # handled below
        "#"*) continue ;;              # a plain comment line - skip
        *) ;;
    esac
    [ -z "$line" ] && continue

    # Extract the bare symbol name (CONFIG_FOO) from either form:
    #   CONFIG_FOO=y            -> CONFIG_FOO
    #   # CONFIG_FOO is not set -> CONFIG_FOO
    case "$line" in
        "# "*" is not set")
            symbol=$(echo "$line" | sed -e 's/^# //' -e 's/ is not set$//')
            ;;
        CONFIG_*=*)
            symbol=${line%%=*}
            ;;
        *)
            echo "verify-config-fragment: unrecognized fragment line, skipping: $line" >&2
            continue
            ;;
    esac

    if ! grep -qxF "$line" "$ACTUAL"; then
        actual_line=$(grep -E "^(# )?${symbol}([= ]|$)" "$ACTUAL" || true)
        echo "MISMATCH: $symbol" >&2
        echo "  fragment wants: $line" >&2
        echo "  actual .config: ${actual_line:-(absent entirely)}" >&2
        fail=1
    fi
done < "$FRAGMENT"

if [ "$fail" -eq 0 ]; then
    echo "verify-config-fragment: OK - every fragment option survived into the built .config unchanged"
else
    echo "verify-config-fragment: FAIL - see mismatches above" >&2
fi
exit $fail
