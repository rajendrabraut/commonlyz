#!/usr/bin/env python3
"""Registers a module in the host: project reference, ModuleCatalog entry, solution. Usage: register-module.py TimeTrack"""
import os, re, subprocess, sys

name = sys.argv[1]
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
proj = f"src/Modules/Commonlyz.Modules.{name}/Commonlyz.Modules.{name}.csproj"

csproj = os.path.join(root, "src/Commonlyz.Web/Commonlyz.Web.csproj")
s = open(csproj).read()
ref = f'    <ProjectReference Include="..\\Modules\\Commonlyz.Modules.{name}\\Commonlyz.Modules.{name}.csproj" />\n'
if ref not in s:
    s = s.replace('    <ProjectReference Include="..\\Commonlyz.Core\\Commonlyz.Core.csproj" />\n',
                  '    <ProjectReference Include="..\\Commonlyz.Core\\Commonlyz.Core.csproj" />\n' + ref)
    open(csproj, "w").write(s)

catalog = os.path.join(root, "src/Commonlyz.Web/ModuleCatalog.cs")
s = open(catalog).read()
entry = f"        new Modules.{name}.{name}Module(),\n"
if entry not in s:
    s = s.replace("    ];\n", entry + "    ];\n", 1)
    open(catalog, "w").write(s)

subprocess.run(["dotnet", "sln", "add", proj], cwd=root, check=True, stdout=subprocess.DEVNULL)
print(f"registered {name}")
