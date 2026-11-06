param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$DataDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [switch]$Smoke,
    [switch]$ClassicRolesOnly
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AcceptanceWindow {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
'@

$exe = (Resolve-Path -LiteralPath $Executable).Path
$data = (Resolve-Path -LiteralPath $DataDirectory).Path
$out = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($out) | Out-Null
$proc = [Diagnostics.Process]::Start([Diagnostics.ProcessStartInfo]@{
    FileName = $exe
    UseShellExecute = $false
    Arguments = '--data-dir "' + $data + '"'
})
try {
    for ($n=0; $n -lt 80; $n++) {
        $proc.Refresh()
        if ($proc.HasExited) { throw "测试程序提前退出：$($proc.ExitCode)" }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($proc.MainWindowHandle -eq [IntPtr]::Zero) { throw '主窗口没有出现' }
    $hwnd = $proc.MainWindowHandle
    [AcceptanceWindow]::SetWindowPos($hwnd, [IntPtr]::Zero, 30, 30, 1240, 780, 0x0040) | Out-Null
    Start-Sleep -Seconds 8
    $root = [Windows.Automation.AutomationElement]::FromHandle($hwnd)
    function Find-Id([string]$id) {
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        for ($n=0; $n -lt 80; $n++) {
            $item = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
            if ($item) { return $item }
            Start-Sleep -Milliseconds 250
        }
        throw "未找到控件 $id"
    }
    function Find-Name([string]$name) {
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name)
        for ($n=0; $n -lt 80; $n++) {
            $item = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
            if ($item) { return $item }
            Start-Sleep -Milliseconds 250
        }
        throw "未找到控件名称 $name"
    }
    function Invoke-Id([string]$id) {
        (Find-Id $id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 600
    }
    function Select-Theme([string]$name) {
        Invoke-Id 'SettingsNav'
        $item = Find-Name $name
        $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 1000
    }
    function Capture([string]$path, [IntPtr]$captureHwnd=$hwnd, [string]$titleOverride='') {
        $rect = [AcceptanceWindow+Rect]::new()
        if (-not [AcceptanceWindow]::GetWindowRect($captureHwnd, [ref]$rect)) { throw 'GetWindowRect 失败' }
        $width = $rect.Right - $rect.Left; $height = $rect.Bottom - $rect.Top
        $bmp = [Drawing.Bitmap]::new($width, $height)
        $g = [Drawing.Graphics]::FromImage($bmp)
        $dc = $g.GetHdc()
        try { $ok = [AcceptanceWindow]::PrintWindow($captureHwnd, $dc, 2) }
        finally { $g.ReleaseHdc($dc); $g.Dispose() }
        if (-not $ok) { $bmp.Dispose(); throw 'PrintWindow 失败' }
        $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $title = if ($titleOverride) { $titleOverride } else { (Find-Id 'PageTitle').Current.Name }
        return @{ path=$path; width=$width; height=$height; title=$title }
    }
    function Capture-ClassicRoles {
        Invoke-Id 'RolesNav'
        $condition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::NameProperty, '人物与角色档案')
        for ($n=0; $n -lt 40; $n++) {
            $candidates = [Windows.Automation.AutomationElement]::RootElement.FindAll(
                [Windows.Automation.TreeScope]::Children, $condition)
            foreach ($item in $candidates) {
                $handle = [IntPtr]$item.Current.NativeWindowHandle
                [uint32]$owner = 0
                [AcceptanceWindow]::GetWindowThreadProcessId($handle, [ref]$owner) | Out-Null
                if ($owner -eq $proc.Id) {
                    return Capture (Join-Path $out 'classic-roles.png') $handle '人物与角色档案'
                }
            }
            Start-Sleep -Milliseconds 250
        }
        $names = [Windows.Automation.AutomationElement]::RootElement.FindAll(
            [Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { $_.Current.Name } | Where-Object { $_ }
        throw ('原版人物窗口没有出现；桌面窗口：' + ($names -join ' / '))
    }
    if ($Smoke) {
        $shot = Capture (Join-Path $out 'smoke.png')
        Write-Output "smoke=$($shot.path) title=$($shot.title)"
        return
    }
    if ($ClassicRolesOnly) {
        $shot = Capture-ClassicRoles
        Write-Output "classicRoles=$($shot.path) title=$($shot.title)"
        return
    }
    $result = @()
    foreach ($theme in @(
        @('classic','原版界面'), @('cinema','深色影院'), @('compact','紧凑高密度'),
        @('atlas','关系图谱'), @('glass','雾面玻璃'))) {
        if ($theme[0] -ne 'classic') { Select-Theme $theme[1] }
        $libraryNav = if ($theme[0] -eq 'classic') { 'AllNav' } else { 'LibraryNav' }
        foreach ($page in @(
            @('all',$libraryNav), @('categories','CategoriesNav'), @('roles','RolesNav'))) {
            if ($theme[0] -eq 'classic' -and $page[0] -eq 'roles') {
                $shot = Capture-ClassicRoles
            } else {
                Invoke-Id $page[1]
                Start-Sleep -Milliseconds 800
                $shot = Capture (Join-Path $out "$($theme[0])-$($page[0]).png")
            }
            $result += [pscustomobject]@{ theme=$theme[0]; page=$page[0]; screenshot=$shot.path;
                title=$shot.title; width=$shot.width; height=$shot.height; processId=$proc.Id;
                method='PrintWindow(PW_RENDERFULLCONTENT)'; dataDirectory=$data }
        }
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'capture.json') -Encoding utf8
    Write-Output "captured=$($result.Count) process=$($proc.Id)"
}
finally {
    if (-not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null; if (-not $proc.WaitForExit(5000)) { $proc.Kill(); $proc.WaitForExit() } }
    $proc.Dispose()
}
