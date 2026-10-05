<#
.SYNOPSIS
    DevHost 자동 레이아웃 검사기를 모든 화면 x 테마 x 크기에서 돌려 요약 표를 만든다. 문제가 하나라도 있으면 종료 코드 1.

.DESCRIPTION
    잘림(ClippedRight/Bottom, TextClipped)·□로 나오는 아이콘(IconBox)·빈 필수 콤보(EmptyChoice)·요소 겹침(Overlap)·
    크기 0(ZeroSized)·테마 미적용(Unthemed)을 기계적으로 잡는다. 검사기 자체의 시험(--layout-selftest)이 먼저 돈다.
    화면 하나가 60초 안에 끝나지 않으면(모달 대화상자에서 멈춤 등) 프로세스를 끄고 실패로 센다.

.PARAMETER Configuration
    DevHost 빌드 구성. 기본 Release.

.PARAMETER NoBuild
    DevHost를 다시 빌드하지 않는다.

.EXAMPLE
    .\scripts\layout-check.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$NoBuild,

    # 다른 빌드(예: 다른 에이전트의 솔루션 빌드)가 DevHost 출력 폴더를 덮어쓰지 않도록 별도 폴더에 빌드해 쓴다.
    [string]$OutputDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tools\DevHost\DevHost.csproj'

if (-not $NoBuild) {
    Write-Host '==> DevHost 빌드' -ForegroundColor Cyan
    if ($OutputDir) {
        & dotnet build $project -c $Configuration --nologo -v q -o $OutputDir | Out-Host
    } else {
        & dotnet build $project -c $Configuration --nologo -v q | Out-Host
    }
    if ($LASTEXITCODE -ne 0) { Write-Host '실패: 빌드' -ForegroundColor Red; exit 1 }
}

if ($OutputDir) {
    $exe = Join-Path $OutputDir 'DevHost.exe'
} else {
    $exe = Join-Path $root "tools\DevHost\bin\$Configuration\net8.0-windows\win-x64\DevHost.exe"
}
if (-not (Test-Path $exe)) { Write-Host "실패: $exe 가 없습니다" -ForegroundColor Red; exit 1 }

# 이전 실행이 남긴 DevHost가 DLL을 잡고 있지 않게
Get-Process DevHost -ErrorAction SilentlyContinue | Stop-Process -Force

function Invoke-DevHost([string[]]$ArgList, [int]$TimeoutSeconds = 60) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = ($ArgList -join ' ')
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $proc = [System.Diagnostics.Process]::Start($psi)
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
        try { $proc.Kill() } catch { }
        return @{ Timeout = $true; Code = -1; Out = '' }
    }
    return @{ Timeout = $false; Code = $proc.ExitCode; Out = $outTask.Result }
}

Write-Host '==> 검사기 자체 시험' -ForegroundColor Cyan
$self = Invoke-DevHost @('--layout-selftest') 60
if ($self.Timeout -or $self.Out -notmatch '"ok":true') {
    Write-Host '실패: 검사기 자체 시험' -ForegroundColor Red
    exit 1
}

# 화면 목록: 이름 | 인자 | 크기 목록
$themes = @('black', 'light')
$main = @('1100x700', '900x600')
$scenarios = @(
    @{ Name = 'step1 접속';          Args = @('--step', '1');                                   Sizes = $main },
    @{ Name = 'step2 테이블 매핑';   Args = @('--step', '2');                                   Sizes = $main },
    @{ Name = 'step3 컬럼 매핑';     Args = @('--step', '3');                                   Sizes = $main },
    @{ Name = 'step4 검증(전)';      Args = @('--step', '4');                                   Sizes = $main },
    @{ Name = 'step4 검증 결과';     Args = @('--step', '4', '--run-validation');               Sizes = $main },
    @{ Name = 'step4 검증 진행 중';  Args = @('--step', '4', '--freeze-validation', '12');      Sizes = $main },
    @{ Name = 'step5 실행(대기)';    Args = @('--step', '5');                                   Sizes = $main },
    @{ Name = 'step5 실행 중';       Args = @('--step', '5', '--fake-run', 'running');          Sizes = $main },
    @{ Name = 'step5 일시정지';      Args = @('--step', '5', '--fake-run', 'paused');           Sizes = $main },
    @{ Name = 'step5 완료';          Args = @('--step', '5', '--fake-run', 'done');             Sizes = $main },
    @{ Name = 'step5 중지';          Args = @('--step', '5', '--fake-run', 'stopped');          Sizes = $main },
    @{ Name = 'step5 실패';          Args = @('--step', '5', '--fake-run', 'failed');           Sizes = $main },
    @{ Name = '실행 팝업 파괴적';    Args = @('--step', '5', '--open', 'run-popup-destructive'); Sizes = $main },
    @{ Name = '실행 팝업 실행불가';  Args = @('--step', '5', '--open', 'run-popup-blocked'); Sizes = $main },
    @{ Name = '실행 팝업 검증먼저';  Args = @('--step', '5', '--open', 'run-popup-validate-first'); Sizes = $main },
    @{ Name = '실행 팝업 검증뒤변경'; Args = @('--step', '5', '--open', 'run-popup-stale'); Sizes = $main },
    @{ Name = '매핑 추가(테이블)';   Args = @('--step', '2', '--open', 'add-mapping');          Sizes = $main },
    @{ Name = '자동 매칭';           Args = @('--step', '2', '--open', 'auto-match');           Sizes = $main },
    @{ Name = '템플릿 내보내기';     Args = @('--step', '2', '--open', 'template-export');      Sizes = $main },
    @{ Name = 'SQL 편집기 검증';     Args = @('--step', '3', '--open', 'sql-editor');           Sizes = @('1080x720', '900x620') },
    @{ Name = 'SQL 편집기 Alias';    Args = @('--step', '3', '--open', 'sql-editor-alias');     Sizes = @('1080x720', '900x620') },
    @{ Name = 'SQL 편집기 오류';     Args = @('--step', '3', '--open', 'sql-editor-error');     Sizes = @('1080x720', '900x620') },
    @{ Name = '설정 접속';           Args = @('--settings', 'connections');                     Sizes = @('900x620', '760x560') },
    @{ Name = '설정 기본값';         Args = @('--settings', 'defaults');                        Sizes = @('900x620', '760x560') },
    @{ Name = '설정 에이전트';       Args = @('--settings', 'agent');                           Sizes = @('900x620', '760x560') }
)

$rows = @()
$failed = 0
foreach ($sc in $scenarios) {
    foreach ($theme in $themes) {
        foreach ($size in $sc.Sizes) {
            $a = @('--seed', '--fake-oracle', '--theme', $theme, '--layout-check', '--size', $size) + $sc.Args
            $r = Invoke-DevHost $a 60
            $summary = ''
            $issues = -1
            if ($r.Timeout) {
                $summary = '시간 초과(60초) — 모달 대화상자에서 멈춤?'
            } else {
                $line = ($r.Out -split "`n" | Where-Object { $_ -match '"type":"summary"' } | Select-Object -Last 1)
                if ($line) {
                    $json = $line | ConvertFrom-Json
                    $issues = [int]$json.issues
                    $summary = if ($issues -eq 0) { 'OK' } else { ($json.byCode.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', ' }
                } else {
                    $summary = "요약 없음(종료 코드 $($r.Code))"
                }
            }
            if ($issues -ne 0) { $failed++ }
            $rows += [pscustomobject]@{ 화면 = $sc.Name; 테마 = $theme; 크기 = $size; 결과 = $summary }
        }
    }
}

Get-Process DevHost -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host ($rows | Format-Table -AutoSize | Out-String -Width 200)
$total = $rows.Count
if ($failed -gt 0) {
    Write-Host "실패: $failed / $total 건에서 문제가 있습니다." -ForegroundColor Red
    exit 1
}

Write-Host "통과: $total 건 모두 문제 없음" -ForegroundColor Green
exit 0
