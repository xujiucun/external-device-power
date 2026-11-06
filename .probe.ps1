$ErrorActionPreference = 'SilentlyContinue'
$out = 'F:\MyProject\external-device-power\.probe.txt'
$lines = New-Object System.Collections.Generic.List[string]

$lines.Add("=== 1. USB devices with VID/PID (keyboard + 2.4G dongle live here) ===")
Get-CimInstance Win32_PnPEntity -Filter "Name like '%VID_%'" |
  ForEach-Object {
    $m = [regex]::Match($_.DeviceID, 'VID_([0-9A-F]{4})&PID_([0-9A-F]{4})')
    if ($m.Success) {
      $lines.Add(("{0}  {1}  {2}" -f $m.Groups[1].Value + ':' + $m.Groups[2].Value, $_.Status, $_.Name))
    }
  }

$lines.Add("")
$lines.Add("=== 2. HIDClass devices full InstanceId (look for vendor-defined collections) ===")
Get-PnpDevice -Class HIDClass |
  Where-Object { $_.InstanceId -like '*VID_*' } |
  ForEach-Object { $lines.Add(("{0}  |  {1}  |  {2}" -f $_.Status, $_.InstanceId, $_.FriendlyName)) }

$lines.Add("")
$lines.Add("=== 3. Any device whose name hints at MCHOSE / 迈从 / keyboard ===")
Get-PnpDevice | Where-Object { $_.FriendlyName -match 'mchose|迈从|keyboard|键盘' } |
  ForEach-Object { $lines.Add(("{0}  |  {1}  |  {2}" -f $_.Status, $_.InstanceId, $_.FriendlyName)) }

$lines.Add("")
$lines.Add("=== 4. Battery devices right now ===")
$b = Get-PnpDevice -Class Battery
if ($b) { $b | ForEach-Object { $lines.Add(("{0}  |  {1}  |  {2}" -f $_.Status, $_.InstanceId, $_.FriendlyName)) } } else { $lines.Add("(EMPTY - no device reports battery through the HID battery driver)") }

$lines | Out-File -FilePath $out -Encoding utf8
"written: " + $out
