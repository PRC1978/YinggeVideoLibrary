param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$DataDirectory,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$exe = (Resolve-Path -LiteralPath $Executable).Path
$data = (Resolve-Path -LiteralPath $DataDirectory).Path
$proc = [Diagnostics.Process]::Start([Diagnostics.ProcessStartInfo]@{
    FileName=$exe; UseShellExecute=$false; Arguments='--data-dir "'+$data+'"'
})
try {
    for($i=0;$i -lt 100;$i++) {
        $proc.Refresh()
        if($proc.HasExited) { throw "应用提前退出：$($proc.ExitCode)" }
        if($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 250
    }
    if($proc.MainWindowHandle -eq [IntPtr]::Zero) { throw '未找到主窗口' }
    Start-Sleep -Seconds 6
    $root=[Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    $windowName=$root.Current.Name
    $ids=@('AllNav','RecentNav','FavoriteNav','RecommendationsNav','CategoriesNav',
        'FoldersNav','StatusNav','RolesNav','ScrapeNav','OnlineNav','SettingsNav',
        'SearchBox','SortBox')
    $rows=foreach($id in $ids) {
        $condition=[Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
        $item=$root.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
        [pscustomobject]@{ id=$id; found=($null -ne $item);
            name=if($item){$item.Current.Name}else{''};
            passed=($null -ne $item -and -not [string]::IsNullOrWhiteSpace($item.Current.Name)) }
    }
    $result=[pscustomobject]@{ processId=$proc.Id; dataDirectory=$data; windowName=$windowName;
        method='Windows UI Automation on visible isolated process';
        passed=(@($rows|Where-Object {-not $_.passed}).Count -eq 0); rows=@($rows) }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath)))|Out-Null
    $result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $OutputPath -Encoding utf8
    Write-Output "passed=$($result.passed) items=$($rows.Count)"
    if(-not $result.passed){exit 1}
}
finally {
    if(-not $proc.HasExited){$proc.CloseMainWindow()|Out-Null;if(-not $proc.WaitForExit(5000)){$proc.Kill();$proc.WaitForExit()}}
    $proc.Dispose()
}
