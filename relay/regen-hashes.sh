#!/bin/bash
# Regenerate sha256 sidecars after editing relay.py or config.yaml.example.
# install.sh on a fresh VPS verifies these against the downloaded files
# before chmod+x — a stale sidecar will abort the install. Run this whenever
# you change either source file, before pushing/deploying.
set -euo pipefail
cd "$(dirname "$0")"
sha256sum relay.py | awk '{print $1}' > relay.py.sha256
sha256sum config.yaml.example | awk '{print $1}' > config.yaml.example.sha256
echo "regenerated:"
echo "  relay.py             -> $(cat relay.py.sha256)"
echo "  config.yaml.example  -> $(cat config.yaml.example.sha256)"
