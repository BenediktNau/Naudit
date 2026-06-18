#!/usr/bin/env bash
# Bestimmt die naechste Release-Version:
#   - letzter vX.Y.Z-Tag mit Patch+1
#   - oder v0.1.0, wenn noch kein passender Tag existiert
# Gibt ausschliesslich "vX.Y.Z" auf stdout aus. Annahme: Tags sind wohlgeformt vX.Y.Z.
set -euo pipefail

last="$(git describe --tags --abbrev=0 --match 'v*' 2>/dev/null || true)"

if [ -z "$last" ]; then
  echo "v0.1.0"
  exit 0
fi

version="${last#v}"            # vX.Y.Z -> X.Y.Z
IFS='.' read -r major minor patch <<< "$version"
echo "v${major}.${minor}.$((patch + 1))"
