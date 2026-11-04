param(
    [Parameter(Mandatory = $true)][int]$TargetProcessId,
    [Parameter(Mandatory = $true)][string]$DataDirectory,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$target = Get-Process -Id $TargetProcessId
$commandLine = (Get-CimInstance Win32_Process -Filter "ProcessId=$TargetProcessId").CommandLine
$resolvedDataDirectory = (Resolve-Path -LiteralPath $DataDirectory).Path
if (-not $commandLine.Contains('--data-dir') -or -not $commandLine.Contains($resolvedDataDirectory) -or
    -not $target.Path.Contains('artifacts\build-')) {
    throw 'The target is not the isolated UI test instance.'
}
$root = [System.Windows.Automation.AutomationElement]::FromHandle($target.MainWindowHandle)

function Find-ById([string]$id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 250
    }
    throw "UI element missing: $id"
}

function Invoke-ById([string]$id) {
    (Find-ById $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
}

function Select-ByName([string]$name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $element) { throw "UI name missing: $name" }
    $selection = $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    Start-Sleep -Milliseconds 400
    return $selection.Current.IsSelected
}

$pages = @()
foreach ($pair in @(
    @('LibraryNav', '我的视频'), @('CategoriesNav', '分类'),
    @('FoldersNav', '视频文件夹'), @('ReviewNav', '人工审核'),
    @('ScrapeNav', '搜刮'))) {
    Invoke-ById $pair[0]
    $actual = (Find-ById 'PageTitle').Current.Name
    $pages += [pscustomobject]@{ id = $pair[0]; expected = $pair[1]; actual = $actual; passed = ($actual -eq $pair[1]) }
}
$sourceTab = Select-ByName '来源与凭据'
$tasksTab = Select-ByName '任务与队列'
Invoke-ById 'SidebarToggle'
$collapsedWidth = (Find-ById 'SidebarToggle').Current.BoundingRectangle.Width
Invoke-ById 'SidebarToggle'
$libraryVisibleAfterExpand = -not (Find-ById 'LibraryNav').Current.IsOffscreen
Invoke-ById 'SettingsNav'
$cinemaSelected = Select-ByName '深色影院'
$glassSelected = Select-ByName '雾面玻璃'
$motionModes = @()
foreach ($mode in @(
    @('动画优先', 'animated'), @('流畅优先', 'smooth'), @('平衡', 'balanced'))) {
    $selected = Select-ByName $mode[0]
    $persisted = (Get-Content (Join-Path $resolvedDataDirectory 'config.json') -Raw | ConvertFrom-Json).uiMotion
    $motionModes += [pscustomobject]@{ label = $mode[0]; selected = $selected; persisted = $persisted; passed = ($selected -and $persisted -eq $mode[1]) }
}
$saved = Get-Content (Join-Path $resolvedDataDirectory 'config.json') -Raw | ConvertFrom-Json
$passed = @($pages | Where-Object { -not $_.passed }).Count -eq 0 -and
    $sourceTab -and $tasksTab -and $collapsedWidth -le 28 -and
    $libraryVisibleAfterExpand -and $cinemaSelected -and $glassSelected -and $saved.uiTheme -eq 'glass' -and
    @($motionModes | Where-Object { -not $_.passed }).Count -eq 0
$result = [pscustomobject]@{
    executable = $target.Path
    dataDirectory = $resolvedDataDirectory
    method = 'Windows UI Automation on the isolated visible process'
    pages = $pages
    scrapeSourceTabSelected = $sourceTab
    scrapeTasksTabSelected = $tasksTab
    collapsedToggleWidth = $collapsedWidth
    libraryVisibleAfterExpand = $libraryVisibleAfterExpand
    cinemaSelected = $cinemaSelected
    glassSelected = $glassSelected
    motionModes = $motionModes
    persistedTheme = $saved.uiTheme
    passed = $passed
}
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Output "passed=$passed pages=$($pages.Count) theme=$($saved.uiTheme)"
if (-not $passed) { exit 1 }
