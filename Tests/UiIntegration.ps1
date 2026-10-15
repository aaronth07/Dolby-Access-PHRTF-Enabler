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
$roomGuid = '1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8'
$dtsContext = '08e54ab7-ff00-4b50-92bb-63d6dc1ef8ea'
$runtimeGuid = 'db6bf241-2068-4aa3-b112-fd2a7058ad0c'
$counterGuid = '22789cc6-8567-4046-acc4-b80f43bd9298'
$deviceGuid = 'f01f13b3-a580-4fdc-9060-22049c7a2353'
$s = Store ([AtmosProfile.ProfileService]::Context) $false $true
try { $roomBefore = $s.Blob($roomGuid) } finally { $s.Dispose() }
$s = Store $dtsContext $false $false
try { $dtsBefore = $s.Blob($runtimeGuid); $deviceBefore = $s.Blob($deviceGuid); $counterBefore = $s.Counter($counterGuid) } finally { $s.Dispose() }
$spatialBefore = [AtmosProfile.AudioPlatform]::Spatial($d)
$filesType = $assembly.GetType('AtmosProfile.SpatialFiles')
$snapshotMethod = $filesType.GetMethod('Snapshot')
$roomContextBefore = $snapshotMethod.Invoke($null,[object[]]@($d,[AtmosProfile.ProfileService]::Context))
$dtsContextBefore = $snapshotMethod.Invoke($null,[object[]]@($d,$dtsContext))
$endpointFolder = Join-Path ([AtmosProfile.ProfileService]::DataRoot) ([guid]::Parse($EndpointId.Substring($EndpointId.LastIndexOf('.')+1)).ToString('B'))
$metadata = @{}
foreach ($name in @('original.json','room/original.json','room/pending.json','dts/original.json')) {
    $path = Join-Path $endpointFolder $name
    $metadata[$name] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
}
@{Endpoint=$EndpointId;Room=$roomBefore;Dts=$dtsBefore;Device=$deviceBefore;Counter=$counterBefore;Spatial=$spatialBefore;Metadata=$metadata;RoomContext=$roomContextBefore;DtsContext=$dtsContextBefore} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $evidence 'before.json')
$results = [Collections.Generic.List[string]]::new()
try {
    Tab 'Dolby PHRTF'; SelectDevice 'DeviceSelector'; SetText 'ProfilePath' $ProfilePath
    (Element 'Reset').SetFocus(); WaitReady 'DeviceSelector'
    Assert (Element 'PersonalizedToggle').Current.IsEnabled 'UI loads the personalized profile and enables the toggle'
    $toggle = (Element 'PersonalizedToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $allow = Element 'Allow'; if ($allow -and !$allow.Current.IsOffscreen) { Click 'Allow' 'DeviceSelector' }
    if ($toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) { Click 'PersonalizedToggle' 'DeviceSelector'; $allow = Element 'Allow'; if ($allow -and !$allow.Current.IsOffscreen) { Click 'Allow' 'DeviceSelector' } }
    Click 'PersonalizedToggle' 'DeviceSelector'
    $allow = Element 'Allow'
    if ($allow -and !$allow.Current.IsOffscreen) { Click 'Allow' 'DeviceSelector' }
    & winapp ui inspect -a $AppProcessId --depth 12 | Set-Content (Join-Path $evidence 'phrtf-ui.txt')
    Assert ([AtmosProfile.ProfileService]::Inspect($d).Atmos) 'UI toggle and explicit override enable Atmos on the selected endpoint'
    & winapp ui inspect -a $AppProcessId | Set-Content (Join-Path $evidence 'phrtf-ui.txt')
    Assert ([AtmosProfile.ProfileService]::Inspect($d).Personalized) 'UI toggle writes a personalized profile and stays On'
    Click 'PersonalizedToggle' 'DeviceSelector'
    Assert (![AtmosProfile.ProfileService]::Inspect($d).Personalized) 'UI toggle restores the Off baseline'
    Click 'PersonalizedToggle' 'DeviceSelector'
    Assert ([AtmosProfile.ProfileService]::Inspect($d).Personalized) 'UI toggle enables PHRTF again without reverting Off'
    Tab 'Dolby room'; SelectDevice 'RoomDevice'
    Pick 'RoomSetting' 'Off'
    Click 'RoomApply' 'RoomDevice'
    Assert ((Element 'RoomStatus').Current.Name -eq 'Room off.') 'UI room Apply selects Off'
    Pick 'RoomSetting' 'Custom'
    SetNumber 'RoomLevel' '150'; SetNumber 'RoomFeedback' '75'
    Click 'RoomApply' 'RoomDevice'
    Assert ((Element 'RoomStatus').Current.Name -eq 'Room level: 150%. Feedback: 75%.') 'UI custom room level and feedback apply'
    Assert ([AtmosProfile.ProfileService]::Inspect($d).Personalized) 'UI custom room preserves PHRTF'
    Click 'RoomRestore' 'RoomDevice'
    Assert ((Element 'RoomStatus').Current.Name -eq 'Original room settings.') 'UI room Restore completes'
    $s = Store ([AtmosProfile.ProfileService]::Context) $false $true
    try { $beforeDouble = $s.Blob($roomGuid) } finally { $s.Dispose() }
    Pick 'RoomSetting' '200% room/direct (experimental)'
    Click 'RoomApply' 'RoomDevice'
    Click 'RoomAllow' 'RoomDevice'
    Assert ((Element 'RoomStatus').Current.Name.Contains('200% room/direct')) 'UI applies the experimental 200% mode'
    $s = Store ([AtmosProfile.ProfileService]::Context) $false $true
    try { $firstDouble = $s.Blob($roomGuid) } finally { $s.Dispose() }
    Click 'RoomApply' 'RoomDevice'; Click 'RoomAllow' 'RoomDevice'
    $s = Store ([AtmosProfile.ProfileService]::Context) $false $true
    try { Assert ([Convert]::ToBase64String($s.Blob($roomGuid)) -eq [Convert]::ToBase64String($firstDouble)) 'UI repeated 200% apply does not accumulate attenuation' } finally { $s.Dispose() }
    Pick 'RoomSetting' '400% room/direct (experimental)'
    Click 'RoomApply' 'RoomDevice'; Click 'RoomAllow' 'RoomDevice'
    Assert ((Element 'RoomStatus').Current.Name.Contains('400% room/direct')) 'UI applies 400% after 200%'
    $s = Store ([AtmosProfile.ProfileService]::Context) $false $true
    try { $firstQuad = $s.Blob($roomGuid) } finally { $s.Dispose() }
    Click 'RoomApply' 'RoomDevice'; Click 'RoomAllow' 'RoomDevice'
    $s = Store ([AtmosProfile.ProfileService]::Context) $false $true
    try { Assert ([Convert]::ToBase64String($s.Blob($roomGuid)) -eq [Convert]::ToBase64String($firstQuad)) 'UI repeated 400% does not accumulate' } finally { $s.Dispose() }
    Click 'RoomRestore' 'RoomDevice'
    $s = Store ([AtmosProfile.ProfileService]::Context) $false $true
    try { Assert ([Convert]::ToBase64String($s.Blob($roomGuid)) -eq [Convert]::ToBase64String($beforeDouble)) 'UI Restore returns exact full-gain bytes' } finally { $s.Dispose() }
    Tab 'DTS profile'; SelectDevice 'DtsDevice'; SetText 'DtsSofaPath' $SofaPath
    Click 'DtsConvert' 'DtsDevice'
    Assert (Element 'DtsApply').Current.IsEnabled 'Final published app converts the supplied SOFA through a real button click'
    (Element 'DtsDetails').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Assert ((WaitElement 'DtsDetailsText').Current.Name.Contains('Native format validation: passed')) 'UI reports actual native conversion validation'
    (Element 'DtsDetails').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    $enable = Element 'DtsEnable'; if ($enable -and !$enable.Current.IsOffscreen) { Click 'DtsEnable' 'DtsDevice'; Assert ($null -ne (Named 'DTS headphone EQ filters are active. Applying a SOFA disables them; Restore returns the original EQ.')) 'Active headphone EQ warning is shown when enabling DTS' }; Click 'DtsApply' 'DtsDevice'
    $allow = Element 'DtsAllow'; if ($allow -and !$allow.Current.IsOffscreen) { Click 'DtsAllow' 'DtsDevice' }
    Assert ((Element 'DtsToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) 'UI DTS Apply reflects the applied custom profile'
    $s = Store $dtsContext $false $false
    try { Assert (@($s.Blob($deviceGuid)[32..55] | Where-Object { $_ -ne 0 }).Count -eq 0) 'UI SOFA apply defaults to EQ Off' } finally { $s.Dispose() }
    foreach ($preset in @('Balanced','Spacious','Off','Original')) {
        Pick 'DtsRoom' $preset; WaitReady 'DtsDevice'
        $allow = Element 'DtsAllow'; if ($allow -and !$allow.Current.IsOffscreen) { Click 'DtsAllow' 'DtsDevice' }
        Assert ((Element 'DtsToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) "UI DTS $preset room retains personalization"
    }
    Assert (!(Element 'DtsEq')) 'Removed headphone EQ dropdown is absent'
    Assert ((Element 'DtsReset').Current.Name -eq 'Reset DTS configuration') 'DTS reset label contains no question mark or invalid punctuation'
    Assert ((Element 'PersistenceDisclaimer').Current.Name.Contains('Settings remain')) 'Persistence is a shared footer'
    Click 'DtsRestore' 'DtsDevice'
    Assert ((Element 'DtsToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) 'UI DTS Restore works without a selected applied profile'
} finally {
    # Stop only this owned test window before restoration, so no UI task can write afterward.
    $p = Get-Process -Id $AppProcessId -ErrorAction SilentlyContinue
    if ($p) { $p.CloseMainWindow() | Out-Null; if (!$p.WaitForExit(3000)) { $p.Kill(); $p.WaitForExit() } }
    $s = Store ([AtmosProfile.ProfileService]::Context) $true $true
    try { $s.Write($roomGuid,$roomBefore) } finally { $s.Dispose() }
    $s = Store $dtsContext $true $false
    try { $s.Write($runtimeGuid,$dtsBefore); $s.Write($deviceGuid,$deviceBefore); $s.Write($counterGuid,[uint32]$counterBefore) } finally { $s.Dispose() }
    SelectProvider $spatialBefore.DefaultSpatialAudioFormat
    foreach ($name in $metadata.Keys) {
        $path = Join-Path $endpointFolder $name
        if ($null -ne $metadata[$name]) { [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null; [IO.File]::WriteAllBytes($path,[byte[]]$metadata[$name]) }
        elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    $verify = $filesType.GetMethod('VerifyUnchanged')
    $verify.Invoke($null,[object[]]@($roomContextBefore,$snapshotMethod.Invoke($null,[object[]]@($d,[AtmosProfile.ProfileService]::Context)),[string[]]@())) | Out-Null
    $verify.Invoke($null,[object[]]@($dtsContextBefore,$snapshotMethod.Invoke($null,[object[]]@($d,$dtsContext)),[string[]]@())) | Out-Null
    Assert ([AtmosProfile.AudioPlatform]::Spatial($d) -eq $spatialBefore) 'UI live tests restore the starting spatial provider exactly'
    $results | ConvertTo-Json | Set-Content (Join-Path $evidence 'results.json')
    $evidence
}
$results
