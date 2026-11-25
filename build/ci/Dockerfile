# The Linux build image for MethodCache CI: .NET 10 SDK, clang for Native AOT, Python, jq and lsof, and Node.js with
# Playwright's Chromium for the Blazor WebAssembly smoke test (jobs.sh wasm).
# The SDK base is pinned by digest; a Dockerfile change selects a new owned build image.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b

RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev python3 jq lsof xz-utils \
 && rm -rf /var/lib/apt/lists/*

# Node.js from nodejs.org (the distribution's is too old for Playwright), and Playwright's Chromium with the libraries
# it needs, where every user of the image can run it
ARG NODE_VERSION=22.12.0
ARG PLAYWRIGHT_VERSION=1.63.0
ENV PLAYWRIGHT_BROWSERS_PATH=/opt/pw-browsers \
    NODE_PATH=/usr/local/lib/node_modules
RUN arch=$(dpkg --print-architecture | sed -e 's/amd64/x64/') \
 && curl -fsSL "https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-linux-${arch}.tar.xz" \
    | tar -xJ -C /usr/local --strip-components=1 \
 && npm install --global "playwright@${PLAYWRIGHT_VERSION}" \
 && playwright install --with-deps chromium \
 && chmod -R a+rX "$PLAYWRIGHT_BROWSERS_PATH" \
 && rm -rf /var/lib/apt/lists/* /root/.npm

# The checkout is mounted from the host and owned by another user; NBGV and the docs check read its history
RUN git config --system --add safe.directory '*'

# Jobs run as the build server's user, which has no account in the image: give it a writable home and cache
RUN mkdir -m 1777 /tmp/home /nuget

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NUGET_PACKAGES=/nuget \
    HOME=/tmp/home
