# The Linux build image for MethodCache CI: the .NET 10 SDK, plus the .NET 8 and 9 SDKs and runtimes the
# tests target, clang for Native AOT, Python for docs/script checks, and jq/lsof for script tests. Tags are pinned by digest, so the image
# only changes when this file does; update a digest with `docker pull <image>:<tag>` and copy RepoDigests.
FROM mcr.microsoft.com/dotnet/sdk:8.0@sha256:ec9c0a0dc5f60adc2065762050638dd5ed5facfc53716d4580a42d86027c8e80 AS sdk8
FROM mcr.microsoft.com/dotnet/sdk:9.0@sha256:1330d3c4144aec2695036a125d9a3b49a1fc555c9024c981248fa83d40fa8b23 AS sdk9

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317

# Only the versioned folders, so the .NET 10 host (dotnet, hostfxr) stays the one that runs everything
COPY --from=sdk8 /usr/share/dotnet/sdk /usr/share/dotnet/sdk
COPY --from=sdk8 /usr/share/dotnet/sdk-manifests /usr/share/dotnet/sdk-manifests
COPY --from=sdk8 /usr/share/dotnet/shared /usr/share/dotnet/shared
COPY --from=sdk8 /usr/share/dotnet/packs /usr/share/dotnet/packs
COPY --from=sdk9 /usr/share/dotnet/sdk /usr/share/dotnet/sdk
COPY --from=sdk9 /usr/share/dotnet/sdk-manifests /usr/share/dotnet/sdk-manifests
COPY --from=sdk9 /usr/share/dotnet/shared /usr/share/dotnet/shared
COPY --from=sdk9 /usr/share/dotnet/packs /usr/share/dotnet/packs

RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev python3 jq lsof \
 && rm -rf /var/lib/apt/lists/*

# The checkout is mounted from the host and owned by another user; NBGV and the docs check read its history
RUN git config --system --add safe.directory '*'

# Jobs run as the build server's user, which has no account in the image: give it a writable home and cache
RUN mkdir -m 1777 /tmp/home /nuget

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NUGET_PACKAGES=/nuget \
    HOME=/tmp/home
