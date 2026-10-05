<#
.SYNOPSIS
    플러그인을 빌드(dotnet publish)해 GitHub 릴리스와 같은 이름의 zip(<id>-<version>.zip)을 만들고 내용을 검증한다.

.DESCRIPTION
    .github/workflows/build.yml 의 publish → zip 단계를 로컬에서 그대로 한다.
      1. src\*\plugin.json(하나여야 함)에서 id·version을 읽는다.
      2. -Test면 단위 테스트(tests\MigrationStudio.Tests)를 먼저 돌린다.
      3. dotnet publish → bin\<구성>\net8.0-windows\win-x64\<어셈블리>.zip (이전 zip은 지우고 새로 만든다)
      4. <OutDir>(기본 release)\<id>-<version>.zip 으로 복사한다.
      5. zip 내용을 검증한다: 있어야 할 파일(플러그인·Core·Oracle 드라이버·에이전트 일체), 없어야 할 파일(pdb·계약 DLL·시험 도구),
         plugin.json 버전, 그리고 zip을 풀어 agent\MigrationAgent.exe --version 이 plugin.json 버전으로 시작하는지.
    Windows PowerShell 5.1과 PowerShell 7 모두에서 돈다.

.PARAMETER Configuration
    빌드 구성. 기본 Release.

.PARAMETER OutDir
    zip을 둘 폴더. 기본은 저장소의 release 폴더(.gitignore에 있음).

.PARAMETER Test
    publish 전에 단위 테스트를 돌린다(실패하면 멈춘다).

.EXAMPLE
    .\scripts\pack.ps1

.EXAMPLE
    .\scripts\pack.ps1 -Test -OutDir D:\release

.NOTES
    실행 정책 때문에 막히면: powershell -ExecutionPolicy Bypass -File scripts\pack.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$OutDir,

    [switch]$Test
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# dotnet 등 하위 프로세스의 한글 출력이 깨지지 않게 콘솔을 UTF-8로
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = [Text.Encoding]::UTF8

function Write-Step([string]$Text) {
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Stop-Pack([string]$Text) {
    Write-Host "실패: $Text" -ForegroundColor Red
    exit 1
}

$root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) {
    $OutDir = Join-Path $root 'release'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Stop-Pack '.NET SDK(dotnet)를 찾지 못했습니다. https://dotnet.microsoft.com/download/dotnet/8.0'
}

# ---- 1. plugin.json ----
$manifests = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Directory |
    ForEach-Object { Join-Path $_.FullName 'plugin.json' } |
    Where-Object { Test-Path -LiteralPath $_ })
if ($manifests.Count -ne 1) {
    Stop-Pack "src\*\plugin.json 이 정확히 하나여야 합니다(찾은 개수: $($manifests.Count))"
}
$manifestPath = $manifests[0]
$pluginDir = Split-Path -Parent $manifestPath
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$id = $manifest.id
$version = $manifest.version
if (-not $id -or -not $version) {
    Stop-Pack "$manifestPath 에 id·version이 없습니다"
}
Write-Step "플러그인 $id $version ($pluginDir)"

# ---- 2. 단위 테스트 ----
if ($Test) {
    Write-Step '단위 테스트'
    dotnet test (Join-Path $root 'tests\MigrationStudio.Tests') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        Stop-Pack '단위 테스트가 실패했습니다'
    }
}

# ---- 3. publish ----
Write-Step "dotnet publish ($Configuration)"
$publishOut = Join-Path $pluginDir "bin\$Configuration\net8.0-windows\win-x64"
if (Test-Path -LiteralPath $publishOut) {
    # 이전에 만든 zip이 남아 있으면 어느 것이 새것인지 모른다 — 지우고 새로 만든다
    Get-ChildItem -LiteralPath $publishOut -Filter *.zip | Remove-Item -Force
}
dotnet publish $pluginDir -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    Stop-Pack 'dotnet publish가 실패했습니다'
}
$zips = @(Get-ChildItem -LiteralPath $publishOut -Filter *.zip -ErrorAction SilentlyContinue)
if ($zips.Count -ne 1) {
    Stop-Pack "플러그인 zip을 찾지 못했습니다: $publishOut"
}

# ---- 4. 릴리스 이름으로 복사 ----
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$target = Join-Path (Resolve-Path -LiteralPath $OutDir).Path "$id-$version.zip"
Copy-Item -LiteralPath $zips[0].FullName -Destination $target -Force

# ---- 5. zip 내용 검증 ----
Write-Step 'zip 내용 검증'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$problems = New-Object System.Collections.Generic.List[string]
$names = @()
$zip = [IO.Compression.ZipFile]::OpenRead($target)
try {
    $names = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    $entry = $zip.GetEntry('plugin.json')
    if (-not $entry) {
        $problems.Add('zip 안에 plugin.json이 없습니다')
    }
    else {
        $reader = New-Object IO.StreamReader($entry.Open(), [Text.Encoding]::UTF8)
        try {
            $inner = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }
        if ($inner.version -ne $version) {
            $problems.Add("zip 안의 version($($inner.version))이 plugin.json($version)과 다릅니다")
        }
        elseif ($inner.assembly -and ($names -notcontains $inner.assembly)) {
            $problems.Add("zip 안에 $($inner.assembly)이(가) 없습니다")
        }
    }
}
finally {
    $zip.Dispose()
}

$required = @(
    'plugin.json', 'MigrationStudio.dll', 'MigrationStudio.Core.dll', 'MigrationStudio.deps.json', 'Oracle.ManagedDataAccess.dll',
    'agent/MigrationAgent.exe', 'agent/MigrationAgent.dll', 'agent/MigrationAgent.runtimeconfig.json', 'agent/MigrationAgent.deps.json',
    'agent/MigrationStudio.Core.dll', 'agent/Oracle.ManagedDataAccess.dll'
)
foreach ($r in $required) {
    if ($names -notcontains $r) { $problems.Add("zip에 있어야 할 파일이 없습니다: $r") }
}
# 없어야 할 것: 디버그 기호, Folderss 본체의 계약 DLL, 시험·검수 도구
$forbidden = @('*.pdb', 'Folderss.PluginContract.dll', 'MigrationStudio.Testing*', 'DevHost*', 'AgentCli*', 'xunit*', 'Microsoft.TestPlatform*')
foreach ($n in $names) {
    $leaf = ($n -split '/')[-1]
    foreach ($pattern in $forbidden) {
        if ($leaf -like $pattern) { $problems.Add("zip에 들어가면 안 되는 파일: $n"); break }
    }
}

# zip을 풀어 에이전트가 실제로 실행되고 plugin.json과 같은 버전을 말하는지 확인
if ($problems.Count -eq 0) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('pack-check-' + [guid]::NewGuid().ToString('N'))
    try {
        [IO.Compression.ZipFile]::ExtractToDirectory($target, $tmp)
        $agent = Join-Path $tmp 'agent\MigrationAgent.exe'
        $out = (& $agent --version) 2>&1 | Out-String
        $code = $LASTEXITCODE
        if ($code -ne 0) {
            $problems.Add("agent\MigrationAgent.exe --version 종료 코드 $code")
        }
        elseif ($out -notmatch ("MigrationAgent\s+" + [regex]::Escape($version))) {
            $problems.Add("에이전트 버전이 plugin.json($version)과 다릅니다: $($out.Trim())")
        }
        else {
            Write-Host ('  에이전트: ' + $out.Trim())
        }
    }
    finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Stop-Pack 'zip 내용 검증에 실패했습니다'
}

$size = (Get-Item -LiteralPath $target).Length
Write-Host ''
Write-Host "완료: $target" -ForegroundColor Green
Write-Host ('  {0:N0} 바이트, 파일 {1}개' -f $size, $names.Count)
Write-Host ('  ' + (($names | Where-Object { $_ -notlike 'agent/*' }) -join ', '))
Write-Host ('  agent/: ' + (($names | Where-Object { $_ -like 'agent/*' } | ForEach-Object { $_.Substring(6) }) -join ', '))
Write-Host '  Folderss: 설정 > 플러그인 > 플러그인 찾기…로 이 zip을 등록한 뒤 Folderss를 다시 시작하세요.'
