#!/usr/bin/env bash
# Bestimmt die naechste Release-Version:
#   - hoechster vX.Y.Z-Tag mit Patch+1
#   - oder v0.1.0, wenn noch kein passender Tag existiert
# Gibt ausschliesslich "vX.Y.Z" auf stdout aus. Nicht-SemVer-Tags (z. B. "vnext")
# werden ignoriert; ein kaputtes vX.Y.Z fuehrt zu Abbruch (fail-closed).
set -euo pipefail

# Nur strikte vX.Y.Z-Tags beruecksichtigen, hoechster zuerst (-v:refname sortiert SemVer korrekt).
# || true faengt ein moegliches SIGPIPE von git (head schliesst die Pipe frueh) unter pipefail ab.
last="$(git tag --list 'v[0-9]*.[0-9]*.[0-9]*' --sort=-v:refname | head -n1 || true)"

if [ -z "$last" ]; then
  echo "v0.1.0"
  exit 0
fi

version="${last#v}"            # vX.Y.Z -> X.Y.Z
IFS='.' read -r major minor patch <<< "$version"
[[ "$major" =~ ^[0-9]+$ && "$minor" =~ ^[0-9]+$ && "$patch" =~ ^[0-9]+$ ]] || {
  echo "Ungueltiger SemVer-Tag: $last" >&2
  exit 1
}
echo "v${major}.${minor}.$((patch + 1))"
