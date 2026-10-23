#!/usr/bin/env bash
# Dev helper: stop any running host, build, start it in the background and wait for /health.
set -uo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
log="${LOG:-/tmp/commonlyz-web.log}"
pkill -f "net10.0/Commonlyz.We[b]" || true
pkill -f "run --project src/Commonlyz.We[b]" || true
cd "$root"
dotnet build src/Commonlyz.Web 2>&1 | grep -E " error |Build succeeded" | sort -u
(dotnet run --project src/Commonlyz.Web --no-build > "$log" 2>&1 &)
for i in $(seq 1 90); do
  curl -sf localhost:5100/health >/dev/null && { echo "up after ${i}s"; exit 0; }
  sleep 1
done
echo "host did not start; last log lines:"; tail -20 "$log"; exit 1
