param([int]$AppProcessId,[string]$EndpointId,[string]$ProfilePath,[string]$SofaPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$repo = Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $repo 'Tests/bin/Debug/net10.0-windows10.0.26100.0/Tests.dll')
$assembly = [AtmosProfile.AudioPlatform].Assembly
$storeType = $assembly.GetType('AtmosProfile.SpatialStore')
$dtsType = $assembly.GetType('AtmosProfile.DtsProfileService')
$dtsType.GetField('ToolRoot').SetValue($null,$repo)
$d = [AtmosProfile.AudioDevice]([AtmosProfile.AudioPlatform]::Devices() | Where-Object Id -EQ $EndpointId)
if (!$d) { throw 'The test endpoint is not active.' }
[AtmosProfile.ProfileService]::ToolRoot = $repo
$root = [System.Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $AppProcessId).MainWindowHandle)
$scope = [System.Windows.Automation.TreeScope]::Descendants
$evidence = Join-Path $repo ('release/review-2026-10-15/ui-live-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence | Out-Null
function Element([string]$id) { $root.FindFirst($scope,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)) }
function Named([string]$name) { $root.FindFirst($scope,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name)) }
function Assert([bool]$value,[string]$name) { if (!$value) { throw $name }; $script:results.Add($name) }
function WaitReady([string]$id) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do { Start-Sleep -Milliseconds 150; $e = Element $id; if ($e -and $e.Current.IsEnabled) { return } } while ($timer.Elapsed.TotalSeconds -lt 45)
    throw "Operation did not finish: $id"
}
function WaitElement([string]$id) {
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do { $element=Element $id; if ($element) { return $element }; Start-Sleep -Milliseconds 100 } while ($timer.Elapsed.TotalSeconds -lt 15)
    throw "Element did not appear: $id"
}
function Click([string]$id,[string]$ready) { & winapp ui invoke $id -a $AppProcessId | Out-Null; if ($LASTEXITCODE) { throw "Click failed: $id" }; WaitReady $ready }
function Tab([string]$name) {
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do { $item=Named $name; if ($item) { break }; Start-Sleep -Milliseconds 100 } while ($timer.Elapsed.TotalSeconds -lt 15)
    if (!$item) { throw "Tab did not appear: $name" }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 350
}
function SelectDevice([string]$id) {
    WaitReady $id
    (Element $id).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 150
    $item = Named $d.Name
    if (!$item) { throw 'Test device is missing from the selector.' }
    $matches = (& winapp ui search $d.Name -a $AppProcessId --json | ConvertFrom-Json).matches
    $selector = ($matches | Where-Object type -EQ 'ListItem' | Select-Object -First 1).selector
    if (!$selector) { throw 'Test device list item was not found.' }
    $item.SetFocus()
    & winapp ui send-keys 'enter' -a $AppProcessId | Out-Null
    if ($LASTEXITCODE) { throw 'Test device selection failed.' }
    WaitReady $id
}
function Pick([string]$id,[string]$name) {
    (Element $id).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 150
    (Named $name).SetFocus()
    & winapp ui send-keys 'enter' -a $AppProcessId | Out-Null
    if ($LASTEXITCODE) { throw "Selection failed: $name" }
    Start-Sleep -Milliseconds 250
}
function SetNumber([string]$id,[string]$value) {
    $edit = (Element $id).FindFirst($scope,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit))
    $edit.SetFocus(); $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    & winapp ui send-keys 'enter' -a $AppProcessId | Out-Null
}
function SetText([string]$id,[string]$value) { (Element $id).SetFocus(); (Element $id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Store([string]$context,[bool]$write,[bool]$commit) { $storeType.GetConstructors()[0].Invoke([object[]]@($d,$context,$write,$commit)) }
function SelectProvider([string]$provider) { $dtsType.GetMethod('SelectProvider').Invoke($null,[object[]]@($d,$provider,'test spatial provider')) | Out-Null }
$results = [Collections.Generic.List[string]]::new()
$before = [AtmosProfile.AudioPlatform]::Spatial($d)
try {
    Tab 'Dolby PHRTF'; SelectDevice 'DeviceSelector'; SetText 'ProfilePath' $ProfilePath
    (Element 'Reset').SetFocus(); WaitReady 'DeviceSelector'
    Assert ((Element 'AtmosStatus').Current.Name.Contains('Dolby Atmos for Headphones active')) 'Already-active Atmos remains correctly reported'
    Assert ((Element 'PersonalizedToggle').Current.IsEnabled) 'Missing initialization does not disable the profile toggle'
    $toggle=(Element 'PersonalizedToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off){throw 'UI override test requires a missing, unpersonalized device property'}
    Click 'PersonalizedToggle' 'DeviceSelector'
    $allow=Named 'Allow anyway'
    Assert ($null -ne $allow -and !$allow.Current.IsOffscreen -and $allow.Current.IsEnabled) 'Missing device initialization offers an enabled Allow anyway button'
    Assert (@($root.FindAll($scope,[System.Windows.Automation.Condition]::TrueCondition) | Where-Object {$_.Current.Name.Contains('No Dolby initialization')}).Count -gt 0) 'Override warning identifies the missing initialization instead of an unsupported device'
    Assert ([AtmosProfile.ProfileService]::Inspect($d).Warning -ne $null) 'Staging the override leaves the device unmodified'
    Assert ([AtmosProfile.AudioPlatform]::Spatial($d) -eq $before) 'Staging the override preserves the active provider'
    $results
} finally {
    $owned=Get-Process -Id $AppProcessId -ErrorAction SilentlyContinue
    if($owned){$owned.CloseMainWindow()|Out-Null;if(!$owned.WaitForExit(3000)){$owned.Kill();$owned.WaitForExit()}}
}
