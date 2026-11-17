#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Hosts a published Commonlyz build in IIS (in-process, ASP.NET Core Module V2).

.DESCRIPTION
    Enables IIS (with WebSockets, which Blazor Server needs), installs the .NET Hosting Bundle if the
    ASP.NET Core Module is missing, copies the published app, writes its settings into web.config and
    creates a dedicated app pool + site. Safe to re-run: the site, pool and files are recreated.
    Used by .github/workflows/iis.yml and for local installs.

.EXAMPLE
    dotnet publish src/Commonlyz.Web -c Release -o artifacts/iis
    .\eng\iis\setup-iis.ps1 -Source artifacts\iis -AdminPassword 'Change-me-1!'
#>
param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $AdminPassword,
    [string] $SiteName = 'Commonlyz',
    [int]    $Port = 8090,
    [string] $SitePath = 'C:\inetpub\commonlyz',
    [string] $ConnectionString = 'Server=(localdb)\MSSQLLocalDB;Database=Commonlyz;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=60',
    [string] $Environment = 'Production',
    # 'latest' resolves the newest .NET 10 Hosting Bundle from Microsoft's release metadata.
    [string] $BundleVersion = 'latest'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$AppCmd = "$env:windir\System32\inetsrv\appcmd.exe"

$Source = (Resolve-Path $Source).Path
if (-not (Test-Path (Join-Path $Source 'Commonlyz.Web.dll'))) { throw "Published app not found at $Source" }

Write-Host '== 1/5 IIS features'
$features = 'IIS-WebServerRole', 'IIS-WebServer', 'IIS-CommonHttpFeatures', 'IIS-DefaultDocument', 'IIS-HttpErrors',
            'IIS-StaticContent', 'IIS-RequestFiltering', 'IIS-HttpLogging', 'IIS-WebSockets', 'IIS-ManagementConsole'
$result = Enable-WindowsOptionalFeature -Online -FeatureName $features -All -NoRestart
if ($result.RestartNeeded) { Write-Warning 'Windows asks for a restart to finish IIS; reboot and re-run if the site fails.' }

Write-Host '== 2/5 .NET Hosting Bundle'
if (Test-Path "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll") {
    Write-Host 'ASP.NET Core Module already installed'
} else {
    if ($BundleVersion -eq 'latest') {
        $meta = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
        $BundleVersion = $meta.'latest-runtime'
    }
    $url = "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/$BundleVersion/dotnet-hosting-$BundleVersion-win.exe"
    $exe = Join-Path $env:TEMP "dotnet-hosting-$BundleVersion-win.exe"
    Write-Host "Downloading $url"
    Invoke-WebRequest $url -OutFile $exe -UseBasicParsing
    $sig = Get-AuthenticodeSignature $exe
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "Hosting Bundle signature check failed: $($sig.Status) $($sig.SignerCertificate.Subject)"
    }
    $p = Start-Process $exe -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) { throw "Hosting Bundle installer exited with $($p.ExitCode)" }
    if (-not (Test-Path "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll")) { throw 'ASP.NET Core Module still missing after install' }
    # Restart IIS so the worker picks up the new module and the PATH change for dotnet.exe.
    net stop was /y | Out-Null
    net start w3svc | Out-Null
}

Write-Host '== 3/5 Copy published app'
& $AppCmd stop site /site.name:$SiteName 2>$null | Out-Null
& $AppCmd stop apppool /apppool.name:$SiteName 2>$null | Out-Null
New-Item -ItemType Directory -Force $SitePath, "$SitePath\logs" | Out-Null
robocopy $Source $SitePath /MIR /XD logs /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }

# Settings become environment variables of the IIS worker process, scoped to this site only.
$configPath = Join-Path $SitePath 'web.config'
[xml] $config = Get-Content $configPath
$aspNetCore = $config.SelectSingleNode('//aspNetCore')
$aspNetCore.SetAttribute('stdoutLogEnabled', 'true')
$aspNetCore.SetAttribute('stdoutLogFile', '.\logs\stdout')
$aspNetCore.SetAttribute('startupTimeLimit', '300')   # first start runs every module's migrations
$old = $aspNetCore.SelectSingleNode('environmentVariables')
if ($old) { [void] $aspNetCore.RemoveChild($old) }
$envVars = $config.CreateElement('environmentVariables')
$settings = [ordered] @{
    'ASPNETCORE_ENVIRONMENT'     = $Environment
    'ConnectionStrings__Default' = $ConnectionString
    'Admin__Password'            = $AdminPassword
}
$settings.GetEnumerator() | ForEach-Object {
    $e = $config.CreateElement('environmentVariable')
    $e.SetAttribute('name', $_.Key); $e.SetAttribute('value', $_.Value)
    [void] $envVars.AppendChild($e)
}
[void] $aspNetCore.AppendChild($envVars)
$config.Save($configPath)

Write-Host '== 4/5 App pool and site'
& $AppCmd delete site $SiteName 2>$null | Out-Null
& $AppCmd delete apppool $SiteName 2>$null | Out-Null
# No managed runtime (ANCM hosts .NET itself). Loading the profile lets LocalDB create an instance for the pool identity.
& $AppCmd add apppool /name:$SiteName /managedRuntimeVersion:"" /processModel.loadUserProfile:true /processModel.setProfileEnvironment:true | Out-Null
& $AppCmd add site /name:$SiteName /bindings:"http/*:${Port}:" /physicalPath:$SitePath | Out-Null
& $AppCmd set app "$SiteName/" /applicationPool:$SiteName | Out-Null
icacls $SitePath /grant "IIS AppPool\${SiteName}:(OI)(CI)RX" /T /Q | Out-Null
icacls "$SitePath\logs" /grant "IIS AppPool\${SiteName}:(OI)(CI)M" /T /Q | Out-Null

Write-Host '== 5/5 Start'
& $AppCmd start apppool /apppool.name:$SiteName | Out-Null
& $AppCmd start site /site.name:$SiteName | Out-Null
$ok = $false
for ($i = 0; $i -lt 60 -and -not $ok; $i++) {
    try { $ok = (Invoke-WebRequest "http://localhost:$Port/login" -UseBasicParsing -TimeoutSec 30).StatusCode -eq 200 } catch { Start-Sleep 5 }
}
if (-not $ok) { throw "Site did not answer on http://localhost:$Port. Check $SitePath\logs\stdout_*.log" }
Write-Host "OK: http://localhost:$Port is up" -ForegroundColor Green
