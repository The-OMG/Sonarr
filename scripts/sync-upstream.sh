#!/usr/bin/env bash
# Sync the gdrive-native fork onto the latest upstream STABLE release tag.
# Works for both the Sonarr (.NET 6) and Radarr (.NET 8) forks — auto-detected.
# Our change surface is tiny (all-new files under src/NzbDrone.Core/Drive/ +
# src/NzbDrone.Host/DriveDispatchExtensions.cs, plus a one-line Bootstrap hook and a
# one-line PackageReference), so conflicts are rare; git rerere replays them.
#
# Usage: scripts/sync-upstream.sh [<ref>]   (default: latest matching stable tag)
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

if [ -f src/Sonarr.sln ]; then
    APP=Sonarr; SLN=src/Sonarr.sln; DOTNET=$(command -v dotnet)
    TAGRE='^v4\.[0-9]+\.[0-9]+\.[0-9]+$'
elif [ -f src/Radarr.sln ]; then
    APP=Radarr; SLN=src/Radarr.sln
    export DOTNET_ROOT=/home/theomg/.dotnet8; DOTNET=/home/theomg/.dotnet8/dotnet
    TAGRE='^v[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$'
else
    echo "Not a Sonarr/Radarr checkout" >&2; exit 1
fi

BRANCH=gdrive-native
git config rerere.enabled true

echo ">> [$APP] fetching upstream (origin)"
git fetch --tags --prune origin
if [ -f "$(git rev-parse --git-dir)/shallow" ]; then
    echo ">> unshallowing"; git fetch --unshallow origin || git fetch origin || true
fi

TARGET="${1:-$(git tag -l 'v*' --sort=-v:refname | grep -E "$TAGRE" | head -1)}"
[ -n "$TARGET" ] || { echo "No stable tag found" >&2; exit 1; }

echo ">> rebasing $BRANCH onto $TARGET (backup: backup/${BRANCH}-presync)"
git branch -f "backup/${BRANCH}-presync" "$BRANCH"
git checkout "$BRANCH"
if ! git rebase "$TARGET"; then
    echo "!! CONFLICTS. Resolve, 'git rebase --continue' (rerere may have staged them)." >&2
    echo "   Our conflict-prone files: src/NzbDrone.Host/Bootstrap.cs (the .AddDriveDispatch() line)," >&2
    echo "   src/NzbDrone.Core/*.Core.csproj (Google.Apis.Drive.v3 ref). Rollback: git reset --hard backup/${BRANCH}-presync" >&2
    exit 2
fi

echo ">> building $SLN"
$DOTNET build "$SLN" -c Release -nodeReuse:false -maxcpucount:2

echo ">> OK. Smoke-test the dev instance, then: git push --force-with-lease fork $BRANCH"
