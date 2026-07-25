#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
pack_inputs=(src README.md LICENSE Directory.Build.props Directory.Packages.props global.json)
if [[ -n "$(git -C "$root" status --porcelain --untracked-files=all -- "${pack_inputs[@]}")" ]]; then
  echo "Refusing to pack a dirty worktree: package provenance would identify the wrong source commit." >&2
  echo "Commit the intended release sources, or pass -p:AllowDirtyPack=true for a local non-release diagnostic." >&2
  exit 1
fi
