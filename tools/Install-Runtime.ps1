#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$RuntimeRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'runtime'),
    [ValidateRange(29, 36)]
    [int]$ApiLevel = 36,
    [string]$AvdName,
    [switch]$SkipAndroid,
    [switch]$SkipScrcpy,
    [switch]$NonInteractive,
    # Only specify after the user has read and agreed to the Android SDK license.
    [switch]$AcceptAndroidSdkLicense
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ([string]::IsNullOrWhiteSpace($AvdName)) { $AvdName = "AndroidOnDesktop_API$ApiLevel" }
if ($AvdName -notmatch '^[A-Za-z0-9._-]+$') { throw 'AVD 名称只能包含英文字母、数字、点、下划线与连字符。' }
$systemImage = "system-images;android-$ApiLevel;google_apis;x86_64"
$scrcpyVersion = '5.0.1'
$scrcpyHash = 'b12a2c4ee8be317422451fc7dcf8ee20a71b5ea7ef9ad73ddd825a25316227a5'
$RuntimeRoot = [IO.Path]::GetFullPath($RuntimeRoot)
$sdkRoot = Join-Path $RuntimeRoot 'android-sdk'
$avdHome = Join-Path $RuntimeRoot 'avd'
$scrcpyRoot = Join-Path $RuntimeRoot 'scrcpy'
$cacheRoot = Join-Path $RuntimeRoot 'downloads'
$imageDirectory = Join-Path $sdkRoot "system-images\android-$ApiLevel\google_apis\x86_64"

function Write-Step([string]$Message) {
    Write-Host $Message -ForegroundColor Cyan
}

function New-Directory([string]$Path) {
    [void][IO.Directory]::CreateDirectory($Path)
}

function Get-DownloadedFile([string]$Url, [string]$Path, [string]$Hash = '', [string]$Algorithm = 'SHA256') {
    if (Test-Path -LiteralPath $Path) {
        if (-not $Hash -or (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -eq $Hash) {
            return
        }
        throw "缓存文件校验失败，请移走该文件后重试：$Path"
    }
    Write-Step "下载 $Url"
    New-Directory (Split-Path -Parent $Path)
    $partialPath = $Path + '.partial'
    if ($Hash -and (Test-Path -LiteralPath $partialPath) -and (Get-FileHash -LiteralPath $partialPath -Algorithm $Algorithm).Hash -eq $Hash) {
        Move-Item -LiteralPath $partialPath -Destination $Path
        return
    }
    Invoke-WebRequest -Uri $Url -OutFile $partialPath -UseBasicParsing
    if ($Hash -and (Get-FileHash -LiteralPath $partialPath -Algorithm $Algorithm).Hash -ne $Hash) {
        throw "下载文件校验失败，未解压：$partialPath"
    }
    Move-Item -LiteralPath $partialPath -Destination $Path
}

function Expand-LocalArchive([string]$Archive, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Directory $Destination
    $resolvedDestination = [IO.Path]::GetFullPath($Destination).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            $target = [IO.Path]::GetFullPath((Join-Path $resolvedDestination $entry.FullName))
            if (-not $target.StartsWith($resolvedDestination, [StringComparison]::OrdinalIgnoreCase)) {
                throw "压缩包包含目标目录以外的路径：$($entry.FullName)"
            }
            if (-not $entry.Name) {
                New-Directory $target
                continue
            }
            New-Directory (Split-Path -Parent $target)
            $inputStream = $entry.Open()
            $outputStream = [IO.File]::Create($target)
            try { $inputStream.CopyTo($outputStream) }
            finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}

function Invoke-SdkTool([string]$File, [string[]]$ToolArguments, [switch]$CloseInput, [switch]$AcceptLicense) {
    if ($AcceptLicense) {
        # Accept only prompts for the explicitly requested package installation.
        1..10 | ForEach-Object { 'y' } | & $File @ToolArguments
    }
    elseif ($CloseInput) {
        # EOF means any unaccepted license remains unaccepted; this never sends "y".
        $null | & $File @ToolArguments
    }
    else {
        & $File @ToolArguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Android 工具执行失败，退出码 $LASTEXITCODE：$File"
    }
}

function Find-JavaHome {
    $candidates = @()
    if ($env:JAVA_HOME) { $candidates += $env:JAVA_HOME }
    $unityRoot = Join-Path $env:ProgramFiles 'Unity\Hub\Editor'
    if (Test-Path -LiteralPath $unityRoot) {
        $candidates += @(Get-ChildItem -LiteralPath $unityRoot -Directory | Sort-Object Name -Descending | ForEach-Object {
            Join-Path $_.FullName 'Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK'
        })
    }
    $javaCommand = Get-Command java.exe -ErrorAction SilentlyContinue
    if ($javaCommand) { $candidates += Split-Path -Parent (Split-Path -Parent $javaCommand.Source) }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'bin\java.exe')) { return $candidate }
    }
    throw '未找到 Java。请安装 JDK 17 或以上版本并设置 JAVA_HOME，或安装 Unity 的 Android Build Support/OpenJDK。'
}

function Find-SdkManager {
    $sdkCandidates = @($sdkRoot)
    if ($env:ANDROID_SDK_ROOT) { $sdkCandidates += $env:ANDROID_SDK_ROOT }
    if ($env:ANDROID_HOME) { $sdkCandidates += $env:ANDROID_HOME }
    $sdkCandidates += Join-Path $env:LOCALAPPDATA 'Android\Sdk'
    $unityRoot = Join-Path $env:ProgramFiles 'Unity\Hub\Editor'
    if (Test-Path -LiteralPath $unityRoot) {
        $sdkCandidates += @(Get-ChildItem -LiteralPath $unityRoot -Directory | Sort-Object Name -Descending | ForEach-Object {
            Join-Path $_.FullName 'Editor\Data\PlaybackEngines\AndroidPlayer\SDK'
        })
    }
    foreach ($candidate in ($sdkCandidates | Select-Object -Unique)) {
        $toolsRoot = Join-Path $candidate 'cmdline-tools'
        if (Test-Path -LiteralPath $toolsRoot) {
            $managers = @(Get-ChildItem -LiteralPath $toolsRoot -Filter sdkmanager.bat -File -Recurse)
            if ($managers.Count -gt 0) {
                return @{ Path = $managers[0].FullName; SourceSdk = $candidate }
            }
        }
    }
    return $null
}

function Install-CommandLineTools {
    Write-Step '获取 Google 官方命令行工具信息……'
    $repositoryUrl = 'https://dl.google.com/android/repository/repository2-1.xml'
    [xml]$repository = (Invoke-WebRequest -Uri $repositoryUrl -UseBasicParsing).Content
    $package = $repository.SelectSingleNode("//*[local-name()='remotePackage' and @path='cmdline-tools;latest']")
    if (-not $package) { throw 'Google 仓库没有返回 cmdline-tools;latest。' }
    $archive = $package.SelectSingleNode("*[local-name()='archives']/*[local-name()='archive'][*[local-name()='host-os']='windows']/*[local-name()='complete']")
    if (-not $archive) { throw 'Google 仓库没有返回 Windows 命令行工具。' }
    $url = $archive.SelectSingleNode("*[local-name()='url']").InnerText
    $checksumNode = $archive.SelectSingleNode("*[local-name()='checksum']")
    if (-not $checksumNode) { throw 'Google 仓库缺少命令行工具校验值。' }
    $algorithm = $checksumNode.GetAttribute('type').ToUpperInvariant().Replace('-', '')
    if (-not $algorithm) { $algorithm = 'SHA1' }
    if ($algorithm -notin @('SHA1', 'SHA256', 'SHA512')) { throw "不支持的校验算法：$algorithm" }
    $downloadUrl = if ([Uri]::IsWellFormedUriString($url, [UriKind]::Absolute)) { $url } else { 'https://dl.google.com/android/repository/' + $url }
    $zipPath = Join-Path $cacheRoot ([IO.Path]::GetFileName(([Uri]$downloadUrl).AbsolutePath))
    Get-DownloadedFile $downloadUrl $zipPath $checksumNode.InnerText.Trim() $algorithm
    $extractRoot = Join-Path $cacheRoot 'commandline-tools-extracted'
    Expand-LocalArchive $zipPath $extractRoot
    $destination = Join-Path $sdkRoot 'cmdline-tools\latest'
    New-Directory $destination
    Get-ChildItem -LiteralPath (Join-Path $extractRoot 'cmdline-tools') -Force | Copy-Item -Destination $destination -Recurse -Force
    return @{ Path = (Join-Path $destination 'bin\sdkmanager.bat'); SourceSdk = $sdkRoot }
}

function Set-AvdProperty([string]$Path, [string]$Key, [string]$Value) {
    $lines = @(Get-Content -LiteralPath $Path)
    $pattern = '^' + [regex]::Escape($Key) + '='
    $lines = @($lines | Where-Object { $_ -notmatch $pattern }) + ($Key + '=' + $Value)
    [IO.File]::WriteAllLines($Path, $lines, [Text.UTF8Encoding]::new($false))
}

function Repair-SystemImageDownload {
    if (Test-Path -LiteralPath (Join-Path $imageDirectory 'system.img')) { return $false }
    $installData = Join-Path $imageDirectory '.installer\.installData'
    if (-not (Test-Path -LiteralPath $installData)) { return $false }
    $pathProperty = Get-Content -LiteralPath $installData | Where-Object { $_ -match '^path=' } | Select-Object -First 1
    if (-not $pathProperty) { return $false }
    # Decode the Windows path written by java.util.Properties, keeping this
    # recovery confined to the SDK installer's own temporary directory.
    $installTemp = [IO.Path]::GetFullPath($pathProperty.Substring(5).Replace('\:', ':').Replace('\\', '\'))
    $allowedTemp = [IO.Path]::GetFullPath((Join-Path $sdkRoot '.temp')).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $installTemp.StartsWith($allowedTemp, [StringComparison]::OrdinalIgnoreCase)) {
        throw "系统镜像临时目录不在当前 SDK 内，未执行下载恢复：$installTemp"
    }
    Write-Step 'SDK 的 Java 下载失败，改用 Windows 下载同一份官方镜像并校验，随后交回 SDK 完成安装……'
    $repositoryUrl = 'https://dl.google.com/android/repository/sys-img/google_apis/sys-img2-3.xml'
    [xml]$repository = (Invoke-WebRequest -Uri $repositoryUrl -UseBasicParsing -TimeoutSec 30).Content
    $package = $repository.SelectSingleNode("//*[local-name()='remotePackage' and @path='$systemImage']")
    if (-not $package) { throw "Google 官方仓库没有返回 Android API $ApiLevel 系统镜像。" }
    $archive = $package.SelectSingleNode("*[local-name()='archives']/*[local-name()='archive'][not(*[local-name()='host-os']) or *[local-name()='host-os']='windows']/*[local-name()='complete']")
    if (-not $archive) { throw 'Google 官方仓库没有返回适用于 Windows 的系统镜像文件。' }
    $urlNode = $archive.SelectSingleNode("*[local-name()='url']")
    $checksumNode = $archive.SelectSingleNode("*[local-name()='checksum' and @type='sha1']")
    $sizeNode = $archive.SelectSingleNode("*[local-name()='size']")
    if (-not $urlNode -or -not $checksumNode -or -not $sizeNode) { throw 'Google 系统镜像信息缺少地址、SHA1 或大小。' }
    $expectedSize = [long]::Parse($sizeNode.InnerText.Trim(), [Globalization.CultureInfo]::InvariantCulture)
    $expectedHash = $checksumNode.InnerText.Trim()
    if ($expectedSize -le 0 -or $expectedHash -notmatch '^[a-fA-F0-9]{40}$') { throw 'Google 系统镜像的校验信息无效。' }
    $downloadUrl = [Uri]::new([Uri]'https://dl.google.com/android/repository/sys-img/google_apis/', $urlNode.InnerText.Trim()).AbsoluteUri
    $archiveName = [IO.Path]::GetFileName(([Uri]$downloadUrl).AbsolutePath)
    $cachedArchive = Join-Path $cacheRoot $archiveName
    Get-DownloadedFile $downloadUrl $cachedArchive $expectedHash 'SHA1'
    if ((Get-Item -LiteralPath $cachedArchive).Length -ne $expectedSize) { throw "系统镜像大小不符合 Google 仓库记录：$cachedArchive" }
    New-Directory $installTemp
    Copy-Item -LiteralPath $cachedArchive -Destination (Join-Path $installTemp $archiveName) -Force
    # LegacyDownloader verifies this cached archive and returns before making
    # another network request. sdkmanager still owns extraction/registration.
    Write-Step '官方镜像校验完成，已恢复 SDK 安装缓存。重新执行 SDK 安装……'
    return $true
}

if (-not [Environment]::Is64BitOperatingSystem) { throw '第一版运行环境需要 64 位 Windows。' }
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$oldJavaHome = $env:JAVA_HOME
$oldAndroidHome = $env:ANDROID_HOME
$oldAndroidSdkRoot = $env:ANDROID_SDK_ROOT
$oldAvdHome = $env:ANDROID_AVD_HOME
$oldAndroidUserHome = $env:ANDROID_USER_HOME
$oldSdkManagerOpts = $env:SDKMANAGER_OPTS
$installationLock = $null
try {
    New-Directory $RuntimeRoot
    $lockPath = Join-Path $RuntimeRoot 'install.lock'
    try {
        # The stream, not the presence of this persistent file, owns the lock.
        # Hold it across installation and recovery so a second installer cannot
        # race SDK cleanup, extraction or AVD configuration.
        $installationLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        $lockFailure = $_.Exception.GetBaseException()
        if (($lockFailure.HResult -band 0xFFFF) -in @(32, 33)) {
            throw "此运行目录已有安装正在进行，请等待原安装结束后重试：$RuntimeRoot"
        }
        throw "无法打开运行环境安装锁：$lockPath。$($lockFailure.Message)"
    }
    New-Directory $cacheRoot
    if (-not $SkipAndroid) {
        New-Directory $sdkRoot
        New-Directory $avdHome
        $javaHome = Find-JavaHome
        $manager = Find-SdkManager
        if (-not $manager) { $manager = Install-CommandLineTools }
        if ($manager.SourceSdk -ne $sdkRoot) {
            # avdmanager locates system-images relative to its own tools directory.
            # Reuse the complete toolset locally so it sees this SDK, not Unity's SDK.
            $sourceTools = Split-Path -Parent (Split-Path -Parent $manager.Path)
            $localTools = Join-Path $sdkRoot 'cmdline-tools\latest'
            Write-Step '复制现有 Android 命令行工具至独立 SDK……'
            New-Directory $localTools
            Get-ChildItem -LiteralPath $sourceTools -Force | Copy-Item -Destination $localTools -Recurse -Force
            $manager.Path = Join-Path $localTools 'bin\sdkmanager.bat'
        }
        $sdkManager = $manager.Path
        $avdManager = Join-Path (Split-Path -Parent $sdkManager) 'avdmanager.bat'
        if (-not (Test-Path -LiteralPath $avdManager)) { throw "缺少 avdmanager：$avdManager" }
        $env:JAVA_HOME = $javaHome
        $env:ANDROID_HOME = $sdkRoot
        $env:ANDROID_SDK_ROOT = $sdkRoot
        $env:ANDROID_AVD_HOME = $avdHome
        $env:ANDROID_USER_HOME = Join-Path $RuntimeRoot 'android-user'
        $env:SDKMANAGER_OPTS = (@($oldSdkManagerOpts, '-Dsun.net.client.defaultConnectTimeout=20000', '-Dsun.net.client.defaultReadTimeout=60000') | Where-Object { $_ }) -join ' '
        New-Directory $env:ANDROID_USER_HOME
        Write-Step "Java：$javaHome"
        Write-Step "独立 Android SDK：$sdkRoot"
        # Reuse only license files the user has already accepted in an existing SDK.
        $existingLicenses = Join-Path $manager.SourceSdk 'licenses'
        $localLicenses = Join-Path $sdkRoot 'licenses'
        if ($manager.SourceSdk -ne $sdkRoot -and (Test-Path -LiteralPath $existingLicenses)) {
            New-Directory $localLicenses
            Get-ChildItem -LiteralPath $existingLicenses -File | ForEach-Object {
                $target = Join-Path $localLicenses $_.Name
                if (-not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $_.FullName -Destination $target }
            }
        }
        if (-not $NonInteractive -and -not $AcceptAndroidSdkLicense) {
            Write-Step '如 Google 显示新的许可条款，请阅读后自行选择是否接受。'
            Invoke-SdkTool $sdkManager @("--sdk_root=$sdkRoot", '--licenses')
        }
        Write-Step "安装 Android Emulator、ADB 与 Android API $ApiLevel 系统镜像（首次需要下载数 GB）……"
        $emulatorPath = Join-Path $sdkRoot 'emulator\emulator.exe'
        $adbPath = Join-Path $sdkRoot 'platform-tools\adb.exe'
        $imagePath = Join-Path $imageDirectory 'system.img'
        $installArguments = @("--sdk_root=$sdkRoot", 'platform-tools', 'emulator', $systemImage)
        try {
            Invoke-SdkTool $sdkManager $installArguments -CloseInput:$NonInteractive -AcceptLicense:$AcceptAndroidSdkLicense
            if (-not (Test-Path -LiteralPath $imagePath)) { throw "SDK 安装结束，但 Android API $ApiLevel 系统镜像尚未完成。" }
        }
        catch {
            $installFailure = $_
            try { $recovered = Repair-SystemImageDownload }
            catch { throw "SDK 安装失败：$($installFailure.Exception.Message)；Windows 下载恢复也失败：$($_.Exception.Message)" }
            if (-not $recovered) { throw $installFailure }
            try {
                Invoke-SdkTool $sdkManager $installArguments -CloseInput:$NonInteractive -AcceptLicense:$AcceptAndroidSdkLicense
            }
            catch { throw "已恢复校验通过的系统镜像缓存，但 SDK 再次安装失败：$($_.Exception.Message)" }
        }
        foreach ($required in @($emulatorPath, $adbPath, $imagePath)) {
            if (-not (Test-Path -LiteralPath $required)) {
                throw "Android 组件尚未安装完成：$required。若存在未接受的许可，请在普通控制台运行本脚本且不加 -NonInteractive。"
            }
        }
        # A present system.img alone does not prove a completed SDK extraction.
        # In particular, missing advancedFeatures.ini omits the encryption
        # metadata partition and leaves Android waiting for it during boot.
        $requiredImageFiles = @('package.xml', 'source.properties', 'kernel-ranchu', 'ramdisk.img', 'system.img', 'vendor.img')
        if ($ApiLevel -in @(30, 35, 36)) { $requiredImageFiles += @('advancedFeatures.ini', 'build.prop', 'encryptionkey.img') }
        $incompleteImageFiles = @($requiredImageFiles | Where-Object {
            $requiredImagePath = Join-Path $imageDirectory $_
            -not (Test-Path -LiteralPath $requiredImagePath -PathType Leaf) -or (Get-Item -LiteralPath $requiredImagePath).Length -le 0
        })
        if ($incompleteImageFiles.Count -gt 0) {
            throw "Android API $ApiLevel 系统镜像安装不完整，文件缺少或为空：$($incompleteImageFiles -join '、')。请结束其他安装后修复官方系统镜像，再重试。现有 AVD 数据未更改。"
        }
        $avdPath = Join-Path $avdHome ($avdName + '.avd')
        $avdConfig = Join-Path $avdPath 'config.ini'
        if (Test-Path -LiteralPath $avdConfig) {
            $existingImage = Get-Content -LiteralPath $avdConfig | Where-Object { $_ -match '^image\.sysdir\.1\s*=' } | Select-Object -First 1
            if (-not $existingImage -or $existingImage -notmatch ("android-" + [regex]::Escape([string]$ApiLevel) + "[\\/]")) {
                throw "已有 AVD $AvdName 使用其他系统镜像，未更改其数据。请用 -AvdName 指定一个新名称。"
            }
            Write-Step "保留已有虚拟设备及配置：$AvdName"
        }
        else {
            Write-Step "创建独立虚拟设备：$avdName"
            # This answers only the optional custom hardware-profile question, never a license.
            'no' | & $avdManager create avd --name $avdName --package $systemImage --path $avdPath
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $avdConfig)) { throw '创建 Android 虚拟设备失败。' }
            # Hardware defaults belong only to a new AVD. Never rewrite an
            # existing device's userdata or configuration during an OS change.
            Set-AvdProperty $avdConfig 'hw.keyboard' 'yes'
            Set-AvdProperty $avdConfig 'hw.gpu.enabled' 'yes'
            Set-AvdProperty $avdConfig 'hw.gpu.mode' 'auto'
            Set-AvdProperty $avdConfig 'hw.ramSize' '3072'
            Set-AvdProperty $avdConfig 'hw.lcd.width' '720'
            Set-AvdProperty $avdConfig 'hw.lcd.height' '1280'
            Set-AvdProperty $avdConfig 'hw.lcd.density' '240'
            Set-AvdProperty $avdConfig 'disk.dataPartition.size' '4G'
        }
        Write-Step 'Android 运行环境已准备好。尚未启动虚拟设备。'
    }
    if (-not $SkipScrcpy) {
        $scrcpyExe = Join-Path $scrcpyRoot 'scrcpy.exe'
        $scrcpyMarker = Join-Path $scrcpyRoot 'installed-version.txt'
        $installedVersion = if (Test-Path -LiteralPath $scrcpyMarker) { (Get-Content -LiteralPath $scrcpyMarker -Raw).Trim() } else { '' }
        if (-not (Test-Path -LiteralPath $scrcpyExe) -or $installedVersion -ne $scrcpyVersion) {
            $zipName = "scrcpy-win64-v$scrcpyVersion.zip"
            $zipPath = Join-Path $cacheRoot $zipName
            Get-DownloadedFile "https://github.com/Genymobile/scrcpy/releases/download/v$scrcpyVersion/$zipName" $zipPath $scrcpyHash
            $extractRoot = Join-Path $cacheRoot ('scrcpy-v' + $scrcpyVersion)
            Expand-LocalArchive $zipPath $extractRoot
            $source = Join-Path $extractRoot ('scrcpy-win64-v' + $scrcpyVersion)
            if (-not (Test-Path -LiteralPath (Join-Path $source 'scrcpy.exe'))) { throw 'scrcpy 压缩包中没有找到 scrcpy.exe。' }
            New-Directory $scrcpyRoot
            Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $scrcpyRoot -Recurse -Force
            [IO.File]::WriteAllText($scrcpyMarker, $scrcpyVersion, [Text.UTF8Encoding]::new($false))
        }
        Write-Step "scrcpy $scrcpyVersion 已准备好。"
    }
    $info = [ordered]@{
        sdkRoot = $sdkRoot
        adbPath = (Join-Path $sdkRoot 'platform-tools\adb.exe')
        emulatorPath = (Join-Path $sdkRoot 'emulator\emulator.exe')
        scrcpyPath = (Join-Path $scrcpyRoot 'scrcpy.exe')
        avdHome = $avdHome
        androidUserHome = (Join-Path $RuntimeRoot 'android-user')
        avdName = $avdName
        systemImage = $systemImage
        scrcpyVersion = $scrcpyVersion
    }
    $infoPath = Join-Path $RuntimeRoot 'runtime-info.json'
    [IO.File]::WriteAllText($infoPath, ($info | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Write-Step "运行环境路径已记录：$infoPath"
    Write-Host '接下来打开 Windows 管理程序，检测环境并启动 Android。若提示缺少硬件加速，请在 Windows 功能中启用 Windows 虚拟机监控程序平台并重启。'
}
finally {
    $env:JAVA_HOME = $oldJavaHome
    $env:ANDROID_HOME = $oldAndroidHome
    $env:ANDROID_SDK_ROOT = $oldAndroidSdkRoot
    $env:ANDROID_AVD_HOME = $oldAvdHome
    $env:ANDROID_USER_HOME = $oldAndroidUserHome
    $env:SDKMANAGER_OPTS = $oldSdkManagerOpts
    if ($null -ne $installationLock) { $installationLock.Dispose() }
}
