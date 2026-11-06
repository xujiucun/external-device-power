$ErrorActionPreference = 'SilentlyContinue'
$out = 'F:\MyProject\external-device-power\.probe_kbd.txt'
$L = New-Object System.Collections.Generic.List[string]

function Add-Prop($id, $name) {
  $p = Get-PnpDeviceProperty -InstanceId $id -KeyName 'DEVPKEY_Device_BusReportedDeviceDesc','DEVPKEY_Device_Manufacturer','DEVPKEY_Device_LocationInfo'
  $desc = ($p | Where-Object { $_.Key -eq 'DEVPKEY_Device_BusReportedDeviceDesc' }).Data
  $mfg  = ($p | Where-Object { $_.Key -eq 'DEVPKEY_Device_Manufacturer' }).Data
  $loc  = ($p | Where-Object { $_.Key -eq 'DEVPKEY_Device_LocationInfo' }).Data
  $L.Add(("  {0}" -f $name))
  $L.Add(("     BusReportedDeviceDesc = {0}" -f $desc))
  $L.Add(("     Manufacturer          = {0}" -f $mfg))
  $L.Add(("     LocationInfo          = {0}" -f $loc))
}

$L.Add("=== A. Top-level USB devices (iProduct strings) ===")
Get-PnpDevice -Class USB | Where-Object { $_.InstanceId -match 'VID_(\w{4})&PID_(\w{4})' } | ForEach-Object {
  Add-Prop $_.InstanceId ("[{0}] {1}" -f $_.Status, $_.InstanceId)
}

$L.Add("")
$L.Add("=== B. All HID collections under 05AC:024F (your suspected receiver) ===")
Get-PnpDevice -Class HIDClass | Where-Object { $_.InstanceId -like '*VID_05AC&PID_024F*' } | ForEach-Object {
  $L.Add(("  {0,-9} {1}" -f $_.Status, $_.InstanceId))
  $L.Add(("            {0}" -f $_.FriendlyName))
  $hid = Get-PnpDevice -InstanceId $_.InstanceId -Class HIDClass
  $svc = (Get-CimInstance Win32_PnPEntity -Filter "DeviceID='$($_.InstanceId)'").Service
  $L.Add(("            lower filter / service = {0}" -f $svc))
}

$L.Add("")
$L.Add("=== C. Composite-interface (MI_xx) endpoints of 05AC:024F ===")
Get-PnpDevice -Class 'USB' -PresentOnly | Where-Object { $_.InstanceId -like '*VID_05AC&PID_024F*' } | ForEach-Object {
  Add-Prop $_.InstanceId ("[{0}] {1}" -f $_.Status, $_.InstanceId)
}

$L.Add("")
$L.Add("=== D. Does ANY HID collection declare a battery usage page? (Battery class members) ===")
$b = Get-PnpDevice -Class Battery -PresentOnly
if ($b) { $b | ForEach-Object { $L.Add(("  {0} | {1} | {2}" -f $_.Status, $_.InstanceId, $_.FriendlyName)) } }
else { $L.Add("  (EMPTY) -> hidbatt.sys bound to nothing = NO device exposes the standard HID battery page") }

$L | Out-File -FilePath $out -Encoding utf8
"written: " + $out
