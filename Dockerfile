# syntax=docker/dockerfile:1
# ---- build stage: compiles inside the SDK image (no local SDK needed on the server) ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Restore first (layer-cached) using just the csproj.
COPY src/DVarr/DVarr.csproj src/DVarr/
RUN dotnet restore src/DVarr/DVarr.csproj
# Then the rest of the source and publish.
COPY . .
RUN dotnet publish src/DVarr/DVarr.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0
# ffmpeg/ffprobe come from a BtbN static build, NOT Debian apt. Debian's ffmpeg is 5.1.x (years old);
# 8.x is materially more robust on reconnect and on corrupt 4K HEVC NALUs (the flaky-line failures we hit
# this session). The "linux64-gpl" static build also bundles h264_nvenc/hevc_nvenc + scale_cuda + cuda, so
# the live-preview NVENC path works against the host driver provided by `runtime: nvidia` (it dlopens the
# driver — no CUDA toolkit in the image needed).
#
# Pin: the n8.1 release-branch asset under BtbN's permanent `latest` tag. We deliberately do NOT pin a dated
# `autobuild-YYYY-MM-DD-HH-MM` tag — BtbN prunes those after a few weeks, which would 404 every future rebuild.
# The `n8.1-latest` asset is version-pinned to the 8.1 line (reports `ffmpeg version n8.1-...` on /api/health)
# and is durable, so it's the reproducible-AND-buildable choice. Bump to n8.2/n9.x here when ready.
# wget+ca-certificates are also used by the HEALTHCHECK; xz-utils unpacks the .tar.xz.
ARG FFMPEG_ASSET=ffmpeg-n8.1-latest-linux64-gpl-8.1
ARG FFMPEG_URL=https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/${FFMPEG_ASSET}.tar.xz
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates wget xz-utils tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /opt/ffmpeg \
    && wget -qO /tmp/ffmpeg.tar.xz "${FFMPEG_URL}" \
    && tar -xJf /tmp/ffmpeg.tar.xz -C /opt/ffmpeg --strip-components=1 \
    && rm /tmp/ffmpeg.tar.xz \
    && ln -sf /opt/ffmpeg/bin/ffmpeg  /usr/local/bin/ffmpeg \
    && ln -sf /opt/ffmpeg/bin/ffprobe /usr/local/bin/ffprobe \
    && /usr/local/bin/ffmpeg -hide_banner -version | head -n1
WORKDIR /app
COPY --from=build /app/publish .

# Optional bundled TheSportsDB v2 key, passed as a BuildKit SECRET (never an ARG/ENV — those leak via image
# history / `docker inspect`). The GHCR publish workflow supplies it from the repo secret THESPORTSDB_API_KEY;
# it lands base64-encoded in /app/tsdb.key, which TheSportsDbClient reads as the fallback when no key is entered
# in Settings. Building without the secret just skips the file — source builds then need a key in Settings.
RUN --mount=type=secret,id=tsdb_api_key \
    if [ -s /run/secrets/tsdb_api_key ]; then base64 -w0 /run/secrets/tsdb_api_key > /app/tsdb.key; fi

# Point the locator straight at the static build (deterministic — no reliance on PATH ordering).
ENV DVarr__FfmpegPath=/usr/local/bin/ffmpeg \
    DVarr__FfprobePath=/usr/local/bin/ffprobe \
    DVarr__ConfigDir=/config \
    DVarr__MediaDir=/media \
    DVarr__SegmentDir=/segments \
    DVarr__Urls=http://0.0.0.0:1867 \
    DOTNET_EnableDiagnostics=0

VOLUME ["/config", "/media", "/segments"]
EXPOSE 1867

# Lightweight container healthcheck hits the app's own readiness endpoint.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["/bin/sh", "-c", "wget -qO- http://localhost:1867/api/health >/dev/null 2>&1 || exit 1"]

ENTRYPOINT ["dotnet", "DVarr.dll"]
