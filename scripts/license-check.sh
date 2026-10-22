#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
pwsh -NoProfile -File scripts/license-check.ps1
