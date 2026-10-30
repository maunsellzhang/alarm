param(
    [switch]$Test,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $frameworkRoot 'csc.exe'))) {
    $frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compilerPath = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    throw 'The .NET Framework compiler is missing. Enable .NET Framework 4.8 and retry.'
}

if ([String]::IsNullOrWhiteSpace($OutputDirectory)) {
    $outputDirectory = Join-Path $projectRoot 'dist'
}
else {
    $requestedDirectory = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }
    $outputDirectory = [System.IO.Path]::GetFullPath($requestedDirectory)
}
$assetDirectory = Join-Path $projectRoot 'Assets'
[void](New-Item -ItemType Directory -Path $outputDirectory -Force)
[void](New-Item -ItemType Directory -Path $assetDirectory -Force)
$iconPath = Join-Path $assetDirectory 'Reminder.ico'

Add-Type -AssemblyName System.Drawing
if (-not ('QingReminder.Build.NativeIcon' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace QingReminder.Build
{
    public static class NativeIcon
    {
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);
    }
}
'@
}

$bitmap = New-Object System.Drawing.Bitmap(32, 32)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$font = New-Object System.Drawing.Font('Segoe MDL2 Assets', 22, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$format = New-Object System.Drawing.StringFormat
$format.Alignment = [System.Drawing.StringAlignment]::Center
$format.LineAlignment = [System.Drawing.StringAlignment]::Center
$iconHandle = [IntPtr]::Zero
$icon = $null
$iconStream = $null
try {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#18795E'))
    $bounds = New-Object System.Drawing.RectangleF(0, 0, 32, 32)
    $graphics.DrawString([string][char]0xE823, $font, [System.Drawing.Brushes]::White, $bounds, $format)
    $iconHandle = $bitmap.GetHicon()
    $icon = [System.Drawing.Icon]::FromHandle($iconHandle)
    $iconStream = [System.IO.File]::Create($iconPath)
    $icon.Save($iconStream)
}
finally {
    if ($iconStream) { $iconStream.Dispose() }
    if ($icon) { $icon.Dispose() }
    if ($iconHandle -ne [IntPtr]::Zero) { [void][QingReminder.Build.NativeIcon]::DestroyIcon($iconHandle) }
    $format.Dispose()
    $font.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

$applicationSources = @((Join-Path $projectRoot 'Program.cs'))
$applicationSources += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Core') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
$applicationSources += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'UI') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
$applicationPath = Join-Path $outputDirectory 'QingReminder.exe'
$compilerArguments = @(
    '/nologo', '/langversion:5', '/target:winexe', '/optimize+', '/platform:anycpu',
    "/out:$applicationPath",
    "/win32manifest:$(Join-Path $projectRoot 'app.manifest')",
    "/win32icon:$iconPath",
    "/resource:$iconPath,QingReminder.Icon",
    "/resource:$(Join-Path $projectRoot 'UI\Theme.xaml'),QingReminder.Theme.xaml",
    "/resource:$(Join-Path $projectRoot 'UI\MainWindow.xaml'),QingReminder.MainWindow.xaml",
    '/reference:System.dll', '/reference:System.Core.dll',
    '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.Runtime.Serialization.dll', '/reference:System.Xaml.dll',
    "/reference:$(Join-Path $frameworkRoot 'WPF\WindowsBase.dll')",
    "/reference:$(Join-Path $frameworkRoot 'WPF\PresentationCore.dll')",
    "/reference:$(Join-Path $frameworkRoot 'WPF\PresentationFramework.dll')"
)
& $compilerPath $compilerArguments $applicationSources
if ($LASTEXITCODE -ne 0) {
    throw "Application compilation failed with exit code $LASTEXITCODE."
}
Write-Host "Built $applicationPath"

if ($Test) {
    $testPath = Join-Path $outputDirectory 'CoreTests.exe'
    $coreSources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Core') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
    $testArguments = @('/nologo', '/langversion:5', '/target:exe', '/optimize+', "/out:$testPath", '/reference:System.Runtime.Serialization.dll')
    $testArguments += $coreSources
    $testArguments += (Join-Path $projectRoot 'Tests\CoreTests.cs')
    & $compilerPath $testArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Test compilation failed with exit code $LASTEXITCODE."
    }
    & $testPath
    if ($LASTEXITCODE -ne 0) {
        throw "Core tests failed with exit code $LASTEXITCODE."
    }
}
