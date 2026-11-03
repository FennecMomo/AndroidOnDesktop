[CmdletBinding()]
param(
    [string]$SdkRoot,
    [string]$JdkRoot,
    [string]$AndroidJar,
    [string]$OutputApk
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceRoot = Join-Path $projectRoot 'samples\SmokeApp'
$artifactsRoot = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

if (-not $SdkRoot) {
    $candidates = @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME)
    $adb = Get-Command adb.exe -ErrorAction SilentlyContinue
    if ($adb) { $candidates += Split-Path (Split-Path $adb.Source -Parent) -Parent }
    $candidates += Join-Path $env:LOCALAPPDATA 'Android\Sdk'
    $SdkRoot = $candidates | Where-Object {
        $_ -and (Test-Path -LiteralPath (Join-Path $_ 'build-tools'))
    } | Select-Object -First 1
}
if (-not $SdkRoot) { throw 'Android SDK not found. Supply -SdkRoot with build-tools and platforms installed.' }
$SdkRoot = [IO.Path]::GetFullPath($SdkRoot)

if (-not $JdkRoot) {
    $candidates = @($env:JAVA_HOME, (Join-Path (Split-Path $SdkRoot -Parent) 'OpenJDK'))
    $javac = Get-Command javac.exe -ErrorAction SilentlyContinue
    if ($javac) { $candidates += Split-Path (Split-Path $javac.Source -Parent) -Parent }
    $JdkRoot = $candidates | Where-Object {
        $_ -and (Test-Path -LiteralPath (Join-Path $_ 'bin\javac.exe'))
    } | Select-Object -First 1
}
if (-not $JdkRoot) { throw 'JDK not found. Supply -JdkRoot, for example the Unity AndroidPlayer OpenJDK directory.' }
$JdkRoot = [IO.Path]::GetFullPath($JdkRoot)

$buildTools = Get-ChildItem -LiteralPath (Join-Path $SdkRoot 'build-tools') -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $buildTools) { throw 'No stable Android SDK build-tools directory found.' }
if (-not $AndroidJar) {
    $preferredJar = Join-Path $SdkRoot 'platforms\android-34\android.jar'
    if (Test-Path -LiteralPath $preferredJar) {
        $AndroidJar = $preferredJar
    } else {
        $platform = Get-ChildItem -LiteralPath (Join-Path $SdkRoot 'platforms') -Directory |
            Where-Object { $_.Name -match '^android-\d+$' } |
            Sort-Object { [int]($_.Name -replace '^android-', '') } -Descending |
            Select-Object -First 1
        if ($platform) { $AndroidJar = Join-Path $platform.FullName 'android.jar' }
    }
}
if (-not $AndroidJar -or -not (Test-Path -LiteralPath $AndroidJar)) {
    throw 'Android platform android.jar not found. Supply -AndroidJar.'
}
if (-not $OutputApk) { $OutputApk = Join-Path $artifactsRoot 'SmokeApp.apk' }
$OutputApk = [IO.Path]::GetFullPath($OutputApk)
New-Item -ItemType Directory -Path (Split-Path $OutputApk -Parent) -Force | Out-Null

# Build only in this project. The supplied SDK and JDK are read without modification.
$workRoot = Join-Path $artifactsRoot ('smoke-build-' + [Guid]::NewGuid().ToString('N'))
$classRoot = Join-Path $workRoot 'classes'
$dexRoot = Join-Path $workRoot 'dex'
New-Item -ItemType Directory -Path $classRoot, $dexRoot -Force | Out-Null
$javaExe = Join-Path $JdkRoot 'bin\java.exe'
$javacExe = Join-Path $JdkRoot 'bin\javac.exe'
$jarExe = Join-Path $JdkRoot 'bin\jar.exe'
$keytoolExe = Join-Path $JdkRoot 'bin\keytool.exe'
$aapt2Exe = Join-Path $buildTools.FullName 'aapt2.exe'
$zipalignExe = Join-Path $buildTools.FullName 'zipalign.exe'
$d8Jar = Join-Path $buildTools.FullName 'lib\d8.jar'
$signerJar = Join-Path $buildTools.FullName 'lib\apksigner.jar'
foreach ($file in @($javaExe, $javacExe, $jarExe, $keytoolExe, $aapt2Exe, $zipalignExe, $d8Jar, $signerJar)) {
    if (-not (Test-Path -LiteralPath $file)) { throw "Required build tool not found: $file" }
}

function Invoke-BuildTool {
    param([string]$Executable, [string[]]$ToolArguments)
    & $Executable @ToolArguments
    if ($LASTEXITCODE -ne 0) { throw "Build tool failed with exit code ${LASTEXITCODE}: $Executable" }
}

$sources = @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'src') -Filter '*.java' -Recurse |
    ForEach-Object { $_.FullName })
Invoke-BuildTool $javacExe (@('--release', '8', '-encoding', 'UTF-8', '-classpath', $AndroidJar, '-d', $classRoot) + $sources)
$classesJar = Join-Path $workRoot 'classes.jar'
Invoke-BuildTool $jarExe @('cf', $classesJar, '-C', $classRoot, '.')
Invoke-BuildTool $javaExe @('-cp', $d8Jar, 'com.android.tools.r8.D8', '--release', '--min-api', '29', '--lib', $AndroidJar, '--output', $dexRoot, $classesJar)

$unsignedApk = Join-Path $workRoot 'unsigned.apk'
$alignedApk = Join-Path $workRoot 'aligned.apk'
Invoke-BuildTool $aapt2Exe @('link', '-o', $unsignedApk, '--manifest', (Join-Path $sourceRoot 'AndroidManifest.xml'), '-I', $AndroidJar)
Invoke-BuildTool $jarExe @('uf', $unsignedApk, '-C', $dexRoot, 'classes.dex')
Invoke-BuildTool $zipalignExe @('-f', '-p', '4', $unsignedApk, $alignedApk)

# This local debug certificate is solely for the sample APK, never for production releases.
$keystore = Join-Path $artifactsRoot 'smoke-debug.keystore'
if (-not (Test-Path -LiteralPath $keystore)) {
    Invoke-BuildTool $keytoolExe @('-genkeypair', '-keystore', $keystore, '-storepass', 'android', '-keypass', 'android', '-alias', 'androiddebugkey', '-keyalg', 'RSA', '-keysize', '2048', '-validity', '36500', '-dname', 'CN=AndroidOnDesktop Smoke, O=Local Development, C=CN')
}
Invoke-BuildTool $javaExe @('-jar', $signerJar, 'sign', '--ks', $keystore, '--ks-key-alias', 'androiddebugkey', '--ks-pass', 'pass:android', '--key-pass', 'pass:android', '--out', $OutputApk, $alignedApk)
Invoke-BuildTool $javaExe @('-jar', $signerJar, 'verify', '--verbose', $OutputApk)
Invoke-BuildTool $zipalignExe @('-c', '-p', '4', $OutputApk)

$result = [ordered]@{
    apk = $OutputApk
    package = 'org.androidondesktop.smoke'
    minSdk = 29
    targetSdk = 34
    sha256 = (Get-FileHash -LiteralPath $OutputApk -Algorithm SHA256).Hash.ToLowerInvariant()
    sdkRoot = $SdkRoot
    buildTools = $buildTools.Name
    androidJar = $AndroidJar
    jdkRoot = $JdkRoot
    builtAt = [DateTimeOffset]::Now.ToString('o')
    deviceRunStatus = 'not-tested'
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifactsRoot 'SmokeApp.build.json') -Encoding utf8
Write-Output "Built sample APK: $OutputApk"
