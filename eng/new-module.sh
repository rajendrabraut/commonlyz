#!/usr/bin/env bash
# Scaffolds a module project: ./eng/new-module.sh ShortLink [ExtraPackage ...]
set -euo pipefail
name="$1"; shift
root="$(cd "$(dirname "$0")/.." && pwd)"
dir="$root/src/Modules/Commonlyz.Modules.$name"
mkdir -p "$dir/Data" "$dir/Pages" "$dir/Public"
refs=""
for pkg in "$@"; do refs+="    <PackageReference Include=\"$pkg\" />"$'\n'; done
cat > "$dir/Commonlyz.Modules.$name.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="..\..\Commonlyz.Core\Commonlyz.Core.csproj" />
  </ItemGroup>

${refs:+  <ItemGroup>
${refs}  </ItemGroup>

}  <ItemGroup>
    <InternalsVisibleTo Include="Commonlyz.Tests" />
  </ItemGroup>

</Project>
XML
cat > "$dir/_Imports.razor" <<RAZOR
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using Commonlyz.Core
@using Commonlyz.Core.Components
@using Commonlyz.Modules.$name
@using Commonlyz.Modules.$name.Data
RAZOR
printf '@* Admin pages: signed-in users only. *@\n@attribute [Authorize]\n' > "$dir/Pages/_Imports.razor"
printf '@* Public pages: anonymous, minimal layout. *@\n@attribute [AllowAnonymous]\n@layout PublicLayout\n' > "$dir/Public/_Imports.razor"
echo "created $dir"
