# Containerfile — reproducible build of the Sonarr gdrive-native fork.
#
# PURPOSE: this image exists to REPRODUCE the build (CI + wipe-recovery), not as the
# default runtime. On the seedbox we run the native binary under ~/ with a cron-watchdog
# (no user systemd; integrates with the Whatbox reverse-proxy). The runtime stage below
# is a working drop-in for rootless podman if we ever want it — and because the fork reads
# Google Drive NATIVELY, the container needs NO rclone/FUSE mount inside it: only a
# bind-mount of the local union branch (/home/theomg/plexmedia) + the download dirs.
#
# Build:   podman build -f Containerfile -t sonarr-gdrive:4.0.19 --target runtime .
# Artifact-only (for native deploy):
#          podman build -f Containerfile -t sonarr-gdrive:build --target build .
#          podman create --name x sonarr-gdrive:build; podman cp x:/src/_artifacts ./_artifacts
#
# Pinned to the fork base (upstream v4.0.19.2979 == Whatbox build 4ff1b78). Bump alongside
# each release rebase (see docs/FORK_GDRIVE.md).

# ─────────────────────────────────────────────────────────────────────────────
# Stage 1 — build backend (.NET 6) + frontend (React/webpack), reusing upstream build.sh
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build

# Node 20 + yarn 1.x, pinned to package.json "node": "20.11.1" / "yarn": "1.22.19".
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates gnupg \
 && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && npm install -g yarn@1.22.19 \
 && apt-get clean && rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY . .

# Target runtime identifier for the container. Override for arm64/musl:
#   --build-arg RID=linux-musl-x64   /   --build-arg RID=linux-arm64
ARG RID=linux-x64
ARG FRAMEWORK=net6.0
ARG SONARR_VERSION=4.0.19.10001
ARG BRANCH=gdrive-native

# Reproduce upstream build.sh: backend publish (-> _output/$FRAMEWORK/$RID/publish),
# frontend webpack (-> _output/UI), then package (-> _artifacts/$RID/$FRAMEWORK/Sonarr).
RUN chmod +x build.sh \
 && SONARR_VERSION="${SONARR_VERSION}" BRANCH="${BRANCH}" \
    ./build.sh --backend --frontend --packages -r "${RID}" -f "${FRAMEWORK}"

# ─────────────────────────────────────────────────────────────────────────────
# Stage 2 — runtime (optional drop-in; native+watchdog is the box default)
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS runtime

# SelfContained=false -> needs the ASP.NET 6 runtime (provided by this base image).
# sqlite3 is handy for poking the gdrive index; ffprobe ships bundled in the artifact
# (openur.ffprobestatic), so no system ffmpeg needed.
RUN apt-get update \
 && apt-get install -y --no-install-recommends sqlite3 ca-certificates \
 && apt-get clean && rm -rf /var/lib/apt/lists/*

ARG RID=linux-x64
ARG FRAMEWORK=net6.0
WORKDIR /app
COPY --from=build /src/_artifacts/${RID}/${FRAMEWORK}/Sonarr/ ./

# Persistent app data + the per-drive gdrive index live outside the image.
VOLUME ["/config"]
# Default Sonarr port; the real listen port is read from /config/config.xml at runtime.
EXPOSE 8989

# No FUSE/rclone inside: Drive branch is served via the Drive API + sqlite index.
# At `podman run`, bind-mount the local union branch and download dirs, e.g.:
#   -v /home/theomg/plexmedia:/home/theomg/plexmedia \
#   -v /home/theomg/.config/Sonarr-gdrive:/config
ENTRYPOINT ["./Sonarr", "-nobrowser", "-data=/config"]
