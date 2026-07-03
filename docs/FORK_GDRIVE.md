# Sonarr — native Google Drive read fork (`gdrive-native`)

A long-lived fork of [Sonarr/Sonarr](https://github.com/Sonarr/Sonarr) that serves the
**Drive-backed portion of the library directly via the Google Drive API** instead of
through an rclone FUSE mount. It mirrors the approach used by the native-Drive
[stash fork](https://github.com/The-OMG/stash).

## Why

The library lives under `~/cloud`, an rclone **union** (`gdsa-tdplex_union`):

- upstream `/home/theomg/plexmedia` — local RW branch (first, `search_policy=ff`)
- ~22 Google Team Drives appended with `::nc` (no-create)

So new imports always land on the **local branch**; the Drive branches are an effectively
**read-only archive**. Over FUSE, Sonarr's read/scan/stat and per-file ffprobe are slow.
This fork makes those reads native and cached, while **leaving all writes untouched**.

## Design

Read-only, decorator-based — the risky write path is never touched:

- **`DriveDispatchDiskProvider`** decorates the real `IDiskProvider` (`UnixDiskProvider`).
  It is the ONLY insertion into an upstream file: one registration in the DryIoc
  composition (`RegisterMany` in `src/NzbDrone.Common/Composition/Extensions.cs`) via
  `IfAlreadyRegistered.Replace`. Everything else is new files → near-zero rebase surface.
- **Writes and local-branch reads** pass straight through to the inner provider. The
  rclone union stays mounted and continues to route creates to the local branch — proven,
  unchanged.
- **Drive-only reads** (list/stat/size/exists/last-write, mediainfo) are served from a
  **per-drive sqlite index** (path↔ID, size, md5, modifiedTime, videoMediaMetadata).
  FUSE never touches the hot read path. Directory listings are an in-process merge of the
  local branch (real FS) + the Drive index.

### Metadata straight from Drive (no byte reads)

The Drive API returns `size`, `md5Checksum`, `modifiedTime`, and
`videoMediaMetadata { width, height, durationMillis }` — i.e. **resolution + runtime for
free**. Sonarr's remaining MediaInfo fields (VideoCodec / AudioCodec / AudioChannels /
VideoBitDepth) are obtained by a **one-time ffprobe range-read**, then cached in the index
keyed by the Drive file ID. Because the Drive branch is immutable/RO, that cache is valid
forever and never re-probed — turning full rescans into index lookups.

New feature code (never conflicts): `src/NzbDrone.Core/Drive/*` (planned).

## Base / version pinning

Pinned to the upstream **stable release tag** that matches the running Whatbox binary, not
`develop`. Current base: **`v4.0.19.2979`** (commit `4ff1b78`, == Whatbox `v4-4ff1b78`;
DB migration **217**, .NET runtime 6.0.25). Rebase onto each new `v4.0.x` stable tag; keep
this doc's base line and the `Containerfile` `SONARR_VERSION` in sync.

## Build

Native (the verified path; toolchain: dotnet SDK 6.0.4xx, node 20, yarn 1.22):

```sh
./build.sh --backend --frontend --packages -r linux-x64 -f net6.0
# runnable output: _artifacts/linux-x64/net6.0/Sonarr/
```

Reproducible / CI / wipe-recovery — see `Containerfile` (build stage produces the same
artifact; runtime stage is an optional rootless-podman drop-in, no FUSE needed inside).

## Run (seedbox)

Native binary under `~/` with a **cron-watchdog** (no user systemd), on its own data dir —
**never** depends on `/usr/bin/sonarr` (Whatbox owns and overwrites that). Details land in
the deploy phase.

## Maintenance / handle the next update

`scripts/sync-upstream.sh` (planned) rebases `gdrive-native` onto the latest stable tag with
`git rerere`, regenerates, builds, and tests — stopping on conflicts. Conflict surface is
tiny by design (one composition-root line + all-new files).

Remotes: `origin` = Sonarr/Sonarr (upstream, RO), `fork` = The-OMG/Sonarr. Commit author
`14554607+The-OMG@users.noreply.github.com`.
