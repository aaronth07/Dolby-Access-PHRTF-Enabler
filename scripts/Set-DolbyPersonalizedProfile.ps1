#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('List','Verify','Validate','Backup','On','Off')][string]$Mode = 'Verify',
    [string]$DeviceId = 'Default',
    [string]$ProfilePath,
    [switch]$AutomaticRestart
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell 7.4 or later.' }
$sourceRoot = Split-Path -Parent $PSScriptRoot
if (-not ('AtmosProfile.ProfileService' -as [type])) {
    $sources = @('NativeInterfaces.cs','AudioPlatform.cs','ProfileFile.cs','ProfileService.cs') | ForEach-Object { Join-Path $sourceRoot $_ }
    $references = (Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName
    Add-Type -Path $sources -ReferencedAssemblies $references -CompilerOptions '/nullable:enable'
}
[AtmosProfile.ProfileService]::ToolRoot = $sourceRoot
$profile = $null
if ($Mode -in 'On','Validate') {
    if (-not $ProfilePath) { throw 'Specify -ProfilePath.' }
    $profile = [AtmosProfile.ProfileFile]::Load((Resolve-Path -LiteralPath $ProfilePath).Path)
    if ($profile.Warning) { Write-Warning $profile.Warning }
}
if ($Mode -eq 'Validate') { $profile | Select-Object Name,Version,Room,Warning; return }
$devices = [AtmosProfile.ProfileService]::Devices().GetAwaiter().GetResult()
if ($Mode -eq 'List') { $devices | Select-Object Name,Id; return }
if ($DeviceId -eq 'Default') { $device = [AtmosProfile.ProfileService]::Resolve([AtmosProfile.ProfileService]::DefaultDevice) }
else { $device = $devices | Where-Object Id -eq $DeviceId; if (@($device).Count -ne 1) { throw 'Select an active playback endpoint ID from -Mode List.' } }
if ($Mode -eq 'Verify') { [AtmosProfile.ProfileService]::Inspect($device); return }
if ($Mode -eq 'Backup') { [AtmosProfile.ProfileService]::Backup($device); return }
$restart = $null
if (-not $AutomaticRestart) {
    Write-Host ('Device: ' + $device.Name)
    $restart = [Action[bool]] {
        param([bool]$enabled)
        $format = if ($enabled) { 'Dolby Atmos for Headphones' } else { 'Off' }
        Read-Host ("In Windows sound settings, set Spatial sound to '$format' on the selected device, then press Enter") | Out-Null
    }
}
$backup = [AtmosProfile.ProfileService]::Switch($device,$profile,($Mode -eq 'On'),$restart)
Write-Output ('Transaction backup: ' + $backup)
[AtmosProfile.ProfileService]::Inspect($device)

