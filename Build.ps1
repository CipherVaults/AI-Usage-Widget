param(
    [switch]$Portable,
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
$appSource = Join-Path $PSScriptRoot 'app'
$manifestPath = Join-Path $appSource 'app.manifest'
$manifest = [xml](Get-Content -LiteralPath $manifestPath -Raw)
$identity = $manifest.DocumentElement.SelectSingleNode("*[local-name()='assemblyIdentity']")
$assemblyVersion = [version]$identity.GetAttribute('version')
$releaseVersion = if ($assemblyVersion.Revision -gt 0) { $assemblyVersion.ToString(4) } else { $assemblyVersion.ToString(3) }

$appName = "AI Usage Widget v$releaseVersion.exe"
$portableName = "AI Usage Widget Portable v$releaseVersion.exe"
$setupName = "AI Usage Widget Setup v$releaseVersion.exe"
$outputs = @(
    if ($Portable) { $portableName }
    if ($Installer) { $setupName }
    if (!$Portable -and !$Installer) { $appName }
)

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework 'WPF'
$compiler = Join-Path $framework 'csc.exe'
$references = @(
    (Join-Path $wpf 'WindowsBase.dll')
    (Join-Path $wpf 'PresentationCore.dll')
    (Join-Path $wpf 'PresentationFramework.dll')
    (Join-Path $wpf 'UIAutomationTypes.dll')
    (Join-Path $framework 'System.Xaml.dll')
    (Join-Path $framework 'System.Web.Extensions.dll')
    (Join-Path $framework 'System.Drawing.dll')
    (Join-Path $framework 'System.Windows.Forms.dll')
) | ForEach-Object { "/reference:$_" }

function Build-Widget($outputPath, $assemblyInfoPath, [switch]$PortableBuild) {
    $defines = if ($PortableBuild) { @('/define:PORTABLE') } else { @() }
    & $compiler /nologo /target:winexe /platform:anycpu /optimize+ /checked+ /highentropyva+ /warnaserror+ "/out:$outputPath" $defines `
        "/resource:$appSource\AIUsageWidget.xaml,AIUsageWidget.Layout" "/win32manifest:$manifestPath" `
        "/resource:$appSource\AIUsageWidget.ico,AIUsageWidget.Icon" "/win32icon:$appSource\AIUsageWidget.ico" `
        $references "$appSource\QuotaSources.cs" "$appSource\ClaudeQuota.cs" "$appSource\QuotaUpdates.cs" `
        "$appSource\AIUsageWidget.cs" $assemblyInfoPath
    if ($LASTEXITCODE -ne 0) { throw 'AI Usage Widget build failed.' }
}

function Resolve-SetupCompiler {
    $candidates = @(
        if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
        if ($env:ProgramFiles) { Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe' }
        if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe' }
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    $setupCompiler = $candidates | Select-Object -First 1
    if (!$setupCompiler) {
        $setupCompiler = (Get-Command ISCC.exe -CommandType Application -ErrorAction Stop).Source
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $setupCompiler
    if ($signature.Status -ne 'Valid') {
        throw "Refusing to invoke unsigned or invalid Inno Setup compiler: $setupCompiler ($($signature.Status))"
    }
    return $setupCompiler
}

$release = [IO.Directory]::CreateDirectory((Join-Path $PSScriptRoot 'release')).FullName
$lockPath = Join-Path $release ".v$releaseVersion.lock"
$releaseLock = [IO.FileStream]::new($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
    [IO.FileShare]::None, 4096, [IO.FileOptions]::DeleteOnClose)
$stageName = '.build-' + [guid]::NewGuid().ToString('N')
$stage = Join-Path $release $stageName
try {
    foreach ($name in $outputs) {
        if (Test-Path -LiteralPath (Join-Path $release $name)) {
            throw "Release file '$name' already exists. Bump the version in app\app.manifest for a new release."
        }
    }

    [void][IO.Directory]::CreateDirectory($stage)
    $assemblyInfo = Join-Path $stage 'AssemblyVersion.cs'
    $assemblyMeta = @(
        ('[assembly: System.Reflection.AssemblyVersion("{0}")]' -f $assemblyVersion)
        '[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]'
    ) -join [Environment]::NewLine
    [IO.File]::WriteAllText($assemblyInfo, $assemblyMeta)

    if ($Portable) {
        Build-Widget (Join-Path $stage $portableName) $assemblyInfo -PortableBuild
    }
    if ($Installer) {
        $setupCompiler = Resolve-SetupCompiler
        Build-Widget (Join-Path $stage 'AI Usage Widget.exe') $assemblyInfo
        Add-Type -AssemblyName PresentationCore
        $iconStream = [IO.File]::OpenRead((Join-Path $appSource 'AIUsageWidget.ico'))
        try {
            $decoder = [Windows.Media.Imaging.IconBitmapDecoder]::new($iconStream,
                [Windows.Media.Imaging.BitmapCreateOptions]::None, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
            $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
            $encoder.Frames.Add($decoder.Frames[$decoder.Frames.Count - 1])
        } finally {
            $iconStream.Dispose()
        }
        $wizardImage = [IO.File]::Create((Join-Path $stage 'WizardIcon.png'))
        try { $encoder.Save($wizardImage) } finally { $wizardImage.Dispose() }
        & $setupCompiler /Qp "/DWidgetVersion=$releaseVersion" "/DBuildDirectory=$stage" (Join-Path $PSScriptRoot 'installer\AIUsageWidget.iss')
        if ($LASTEXITCODE -ne 0) { throw 'AI Usage Widget installer build failed.' }
    } elseif (!$Portable) {
        Build-Widget (Join-Path $stage $appName) $assemblyInfo
    }

    foreach ($name in $outputs) {
        $destination = Join-Path $release $name
        [IO.File]::Move((Join-Path $stage $name), $destination)
        $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText("$destination.sha256", "$hash *$name$([Environment]::NewLine)")
        Get-Item -LiteralPath $destination | Select-Object FullName, Length, @{Name='SHA256';Expression={$hash}}
    }
} finally {
    try {
        if (Test-Path -LiteralPath $stage) {
            $resolved = (Resolve-Path -LiteralPath $stage).ProviderPath
            if ([IO.Path]::GetDirectoryName($resolved) -eq $release -and [IO.Path]::GetFileName($resolved) -eq $stageName) {
                Remove-Item -LiteralPath $resolved -Recurse -Force
            }
        }
    } finally {
        $releaseLock.Dispose()
    }
}
