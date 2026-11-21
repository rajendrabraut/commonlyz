# PROFILE

Detected 2026-11-21 from manifests (`Commonlyz.slnx`, `*.csproj`, `Directory.Packages.props`, `engines/imgcore/Cargo.toml`,
`engines/docengine/requirements.txt`, `.github/workflows/*.yml`).

| Component | Stack | Version |
|---|---|---|
| Commonlyz platform (existing) | ASP.NET Core 10 Blazor, EF Core 10, SQL Server 2022 | net10.0 |
| Commonlyz Files API | ASP.NET Core 10 minimal API | net10.0 |
| Commonlyz Files Web | ASP.NET Core 10 MVC | net10.0 |
| imgcore engine | Rust 2021 edition, stable toolchain (local: x86_64-pc-windows-gnu) | image 0.25.10, fast_image_resize 6.1.0 |
| docengine engine | Python | 3.13 (venv, hash-pinned) |
| Word conversion | LibreOffice headless | distro/installed version |

- Package management: NuGet central versions (`Directory.Packages.props`, transitive pinning on), Cargo with `Cargo.lock`, pip `--require-hashes`.
- Analyzers: `TreatWarningsAsErrors`, `AnalysisLevel=latest-default`, `EnforceCodeStyleInBuild` (Directory.Build.props). Baseline warnings: 0.
- CI: GitHub Actions `ci.yml` (build + all tests + Docker image), `iis.yml` (IIS on Windows), `files.yml` (Files with real engines).
- Profiles: no `.agent/profiles/` exist in this repo; Sections 7-10 of CLAUDE.md applied generically.
- Spec: no `.agent/SPEC.md` exists. Requirements come from the owner's chat requests (see PLAN.md).

## Commands
| Gate | Command |
|---|---|
| Restore/build | `dotnet build Commonlyz.slnx -c Release` |
| Tests (.NET, Files) | `dotnet test tests/Commonlyz.Files.Tests -c Release` with `FILES_IMGCORE`, `FILES_PYTHON`, `FILES_DOCENGINE`, `FILES_MODEL`, `FILES_SOFFICE` |
| Tests (.NET, platform) | `dotnet test tests/Commonlyz.Tests` (needs Docker or `COMMONLYZ_TEST_SQL`) |
| Rust build/test | `cargo build --release --locked`, `cargo test --locked` in `engines/imgcore` |
| Python install | `pip install --require-hashes --no-deps -r engines/docengine/requirements.txt` |
| Vulnerable packages | `dotnet list package --vulnerable --include-transitive` |

Local note: the Claude desktop app virtualizes `%LOCALAPPDATA%`; local engines live under
`%LOCALAPPDATA%\commonlyz-tools` (imgcore target dirs, docengine venv, model).
