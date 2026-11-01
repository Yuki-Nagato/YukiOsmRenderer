#!/usr/bin/env bash
# YukiOsmRenderer - runs test-runtime.sh in a clean Debian 11 container (no build dependencies).
# The runtime is copied to a different path than the one it was built in and tested as an
# unprivileged user, because PostgreSQL refuses to run as root.
#
# Expected mounts (see native/build-linux.sh):
#   /src     native/ directory of the repository (read-only)
#   /native  artifacts/native/linux-<arch> containing runtime/ and tools/ (read-only)
set -euo pipefail

useradd --create-home --shell /bin/bash yukiosm
target=/home/yukiosm/relocated/somewhere-else
mkdir -p "$target"
cp -r /native/runtime /native/tools "$target/"
chown -R yukiosm:yukiosm /home/yukiosm/relocated
su yukiosm -c "bash /src/test-runtime.sh '$target/runtime' --tools '$target/tools'"
