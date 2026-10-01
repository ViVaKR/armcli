internal static class PwshTemplates
{
    public static string HunBuildPs1() => """
#!/usr/bin/env pwsh
#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$NoRun,   # 빌드만 하고 실행하지 않음
    [switch]$Clean    # bin/ 삭제 후 종료
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Set-Location $PSScriptRoot

if (-not $IsMacOS) { throw "이 오케스트레이터는 macOS(arm64) 전용입니다." }

function Invoke-Native {
    param([Parameter(Mandatory)][string]$Exe, [string[]]$ArgList = @())
    & $Exe @ArgList
    if ($LASTEXITCODE -ne 0) { throw "'$Exe' 실패 (exit $LASTEXITCODE)" }
}

$binDir = Join-Path $PSScriptRoot 'bin'
if ($Clean) { Remove-Item $binDir -Recurse -Force -ErrorAction SilentlyContinue; return }
New-Item -ItemType Directory -Force -Path $binDir | Out-Null

# 1. 어셈블리 파일 탐색
$exclude = @('bin','build','obj','out','target','artifacts','assets','app','node_modules','.git','.vscode')
$asmFiles = Get-ChildItem -Path . -Include *.s, *.S -Recurse -File | Where-Object {
    $parts = (Resolve-Path -Relative $_.FullName) -split '[/\\]'
    -not ($exclude | Where-Object { $parts -contains $_ })
}
if (-not $asmFiles) { Write-Warning "어셈블리 파일(.s/.S)이 없습니다."; exit 1 }

# 2. 진입점(main) 파일 감지 -> 실행 파일 이름
$targetName = (Split-Path $PSScriptRoot -Leaf).ToLower()
foreach ($f in $asmFiles) {
    if (Select-String -Path $f.FullName -Pattern '^\s*_?main\s*:' -Quiet) {
        $targetName = $f.BaseName.ToLower(); break
    }
}
$outputFile = Join-Path 'bin' $targetName

# 3. 다국어 라이브러리 빌드
$extraLibs = @()
$linkFlags = @()

$rustManifest = 'app/RustLibs/rust_core/Cargo.toml'
if (Test-Path $rustManifest) {
    Write-Host "▶ Rust 빌드" -ForegroundColor Yellow
    Invoke-Native cargo @('build','--release','--manifest-path',$rustManifest)
    $extraLibs += 'app/RustLibs/rust_core/target/release/librust_core.a'
}

if (Test-Path 'app/GoLibs/go.mod') {
    Write-Host "▶ Go 빌드" -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path 'app/GoLibs/out' | Out-Null
    Push-Location 'app/GoLibs'
    try { Invoke-Native go @('build','-buildmode=c-archive','-o','out/libgolibs.a','.') }
    finally { Pop-Location }
    $extraLibs += 'app/GoLibs/out/libgolibs.a'
    $linkFlags += @('-framework','CoreFoundation','-framework','Security','-lresolv')
}

$dotnetProj = 'app/DotnetLibs/DotnetLibs.csproj'
if (Test-Path $dotnetProj) {
    Write-Host "▶ .NET NativeAOT 빌드" -ForegroundColor Yellow
    Invoke-Native dotnet @('publish',$dotnetProj,'-c','Release','-r','osx-arm64')
    $srcDylib = Get-ChildItem 'app/DotnetLibs/bin/Release' -Recurse -Filter 'DotnetLibs.dylib' |
        Where-Object { $_.FullName -match 'osx-arm64[/\\]publish' } | Select-Object -First 1
    if (-not $srcDylib) { throw "DotnetLibs.dylib 를 찾지 못했습니다." }
    $targetDylib = Join-Path $binDir 'DotnetLibs.dylib'
    Copy-Item $srcDylib.FullName $targetDylib -Force
    Invoke-Native install_name_tool @('-id','@rpath/DotnetLibs.dylib',$targetDylib)
    $extraLibs += $targetDylib
    $linkFlags += @('-rpath','@executable_path')
}

# 4. SDK 정보
$sdkPath = (& xcrun --show-sdk-path).Trim()
$sdkVer  = (& xcrun --show-sdk-version).Trim()

# 5. 어셈블 (전처리기 포함) + 링크
$objFiles = @()
foreach ($src in $asmFiles) {
    $rel = (Resolve-Path -Relative $src.FullName) -replace '^[./\\]+','' -replace '[/\\.]','_'
    $obj = Join-Path $binDir "$rel.o"
    $objFiles += $obj
    Invoke-Native clang @('-arch','arm64','-mmacosx-version-min=12.0','-g','-c','-x','assembler','-o',$obj,$src.FullName)
}

Write-Host "▶ 링킹" -ForegroundColor Green

Invoke-Native ld (@('-arch','arm64','-syslibroot',$sdkPath,
                    '-platform_version','macos','12.0',$sdkVer,'-lSystem',
                    '-o',$outputFile) + $objFiles + $extraLibs + $linkFlags)

$objFiles | Remove-Item -Force
Write-Host "✔ 빌드 성공 -> $outputFile" -ForegroundColor Cyan

if (-not $NoRun) {
    & "./$outputFile"
    exit $LASTEXITCODE
}
""";
}
