#!/usr/bin/env bash
# Adds an EF Core migration to a module: ./eng/add-migration.sh ShortLink InitialCreate
# The module's DbContext must be named <Module>Db. Use "Identity" for the host's IdentityDb.
set -euo pipefail
module="$1"; name="${2:-InitialCreate}"
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
dotnet tool restore >/dev/null
if [ "$module" = "Identity" ]; then
  dotnet ef migrations add "$name" --project src/Commonlyz.Web --context IdentityDb --output-dir Identity/Migrations
else
  dotnet ef migrations add "$name" \
    --project "src/Modules/Commonlyz.Modules.$module" \
    --startup-project src/Commonlyz.Web \
    --context "${module}Db" \
    --output-dir Data/Migrations
fi
