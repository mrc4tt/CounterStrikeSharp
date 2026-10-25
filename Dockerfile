FROM registry.gitlab.steamos.cloud/steamrt/sniper/sdk:latest

WORKDIR /workspace

# Debian 11 (bullseye), which the sniper SDK is based on, is end-of-life: its
# bullseye-security Release files are no longer refreshed, so their Valid-Until
# has passed and `apt update` fails hard with "Release file ... is expired".
# The packages are still served, so accept the stale metadata rather than
# dropping the security suite.
RUN printf 'Acquire::Check-Valid-Until "false";\n' > /etc/apt/apt.conf.d/99no-check-valid-until

RUN apt-get update && apt-get install -y --no-install-recommends \
    clang-16 \
    cmake \
    ninja-build \
    git \
    zlib1g-dev \
    libssl-dev \
    libprotobuf-dev \
    protobuf-compiler \
    pkg-config \
    curl && \
    rm -rf /var/lib/apt/lists/* && \
    ln -sf /usr/bin/clang-16 /usr/bin/clang && \
    ln -sf /usr/bin/clang++-16 /usr/bin/clang++

# The source tree is bind-mounted from the host and owned by the host user, not
# by root in here, so git refuses it ("detected dubious ownership"): submodule
# commands fail and CMake's submodule check cannot run. This image only builds
# the mounted checkout, so trust every path.
RUN git config --system --add safe.directory '*'
