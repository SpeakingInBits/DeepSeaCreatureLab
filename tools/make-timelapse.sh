#!/usr/bin/env sh
# Turns the tank's timed snapshots into an MP4 time-lapse. Needs ffmpeg
# (brew install ffmpeg / apt install ffmpeg). Optional: the gallery zip already has a slideshow.
#
#   ./tools/make-timelapse.sh [snapshots-folder] [output.mp4] [frames-per-second]
set -eu

snapshots="${1:-data/snapshots}"
output="${2:-timelapse.mp4}"
fps="${3:-2.5}"

command -v ffmpeg >/dev/null || { echo "ffmpeg isn't installed." >&2; exit 1; }
ls "$snapshots"/*.png >/dev/null 2>&1 || { echo "No snapshots found in $snapshots" >&2; exit 1; }

ffmpeg -y -framerate "$fps" -pattern_type glob -i "$snapshots/*.png" \
  -vf "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p" -r 30 "$output"
echo "Wrote $output"
