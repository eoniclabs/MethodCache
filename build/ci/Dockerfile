# The Linux build image for MethodCache CI: .NET 10 SDK, clang for Native AOT, Python, jq and lsof.
# The SDK base is pinned by digest; a Dockerfile change selects a new owned build image.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317

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
