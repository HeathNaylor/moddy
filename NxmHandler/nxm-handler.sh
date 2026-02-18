#!/bin/sh
[ -z "$1" ] && exit 0
QUEUE_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/Moddy/nxm_queue"
mkdir -p "$QUEUE_DIR"
printf '%s' "$1" > "$QUEUE_DIR/${$}_$(date +%s).nxmurl"
