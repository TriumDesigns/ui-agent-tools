# ui-agent (Windows PowerShell) - run UI Agent Tools commands against this Unity project.
#
#   .\ui-agent.cmd <command> [asset-or-method]      (or: powershell -File ui-agent.ps1 ...)
#
# Commands: map [asset] | validate [asset] | screenshots [asset] | run <Namespace.Class.Method>
#           compile | selftest | baseline | install | all | tests
#
# If the project is open in the Unity Editor, the command is sent to the running Editor.
# Otherwise Unity is started in batch mode.
# Env overrides: UNITY_EDITOR_PATH, UI_AGENT_TIMEOUT (1800), UI_AGENT_PICKUP (120), UI_AGENT_MODE (auto|batch|editor)

param(
    [Parameter(Position = 0)][string]$Command = "",
    [Parameter(Position = 1)][string]$Arg = ""
)

$ErrorActionPreference = "Stop"
$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$MapDir = Join-Path $ProjectDir "UIAgentMap"
$Timeout = if ($env:UI_AGENT_TIMEOUT) { [int]$env:UI_AGENT_TIMEOUT } else { 1800 }
$Pickup = if ($env:UI_AGENT_PICKUP) { [int]$env:UI_AGENT_PICKUP } else { 120 }
$Mode = if ($env:UI_AGENT_MODE) { $env:UI_AGENT_MODE } else { "auto" }

$Valid = @("map", "validate", "screenshots", "run", "compile", "selftest", "baseline", "install", "all", "tests")
if ($Command -eq "" -or $Command -eq "help" -or $Command -eq "-h") {
    Get-Content $MyInvocation.MyCommand.Path -TotalCount 10 | ForEach-Object { $_ -replace '^# ?', '' }
    exit 0
}
if ($Valid -notcontains $Command) { Write-Host "ui-agent: unknown command '$Command'"; exit 2 }
if ($Command -eq "run" -and $Arg -eq "") { Write-Host "ui-agent: run needs a method, e.g. .\ui-agent.cmd run AgentEdits.Edit_20260928_LoginCopy.Run"; exit 2 }
$VersionFile = Join-Path $ProjectDir "ProjectSettings\ProjectVersion.txt"
if (-not (Test-Path $VersionFile)) { Write-Host "ui-agent: $ProjectDir is not a Unity project root."; exit 2 }

New-Item -ItemType Directory -Force -Path (Join-Path $MapDir "logs"), (Join-Path $MapDir "requests") | Out-Null

function Show-Result([string]$File) {
    if (-not (Test-Path $File)) { Write-Host "ui-agent: no result file produced ($File)"; return 1 }
    $r = Get-Content $File -Raw -Encoding UTF8 | ConvertFrom-Json
    $status = if ($r.ok) { "RESULT: OK" } else { "RESULT: FAILED" }
    $t = if ($r.target) { " " + $r.target } else { "" }
    Write-Host "$status [$($r.command)$t]"
    Write-Host $r.summary
    $msgs = @($r.messages | Where-Object { $_ })
    if ($msgs.Count -gt 0) {
        Write-Host "--- messages ---"
        $msgs | Select-Object -Last 60 | ForEach-Object { Write-Host $_ }
    }
    return [int]$r.exitCode
}

function Test-EditorOpen { Test-Path (Join-Path $ProjectDir "Temp\UnityLockfile") }

function Invoke-InEditor {
    $id = (Get-Date -Format "yyyyMMddHHmmss") + "-" + $PID + "-" + (Get-Random -Maximum 99999)
    $req = Join-Path $MapDir "requests\$id.request.json"
    $processing = Join-Path $MapDir "requests\$id.processing"
    $result = Join-Path $MapDir "requests\$id.result.json"
    $json = @{ id = $id; command = $Command; target = $Arg } | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText("$req.tmp", $json, (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -Force "$req.tmp" $req
    Write-Host "ui-agent: Unity Editor is open - sent '$Command' to it (request $id). Waiting..."

    $waited = 0
    while (-not (Test-Path $processing) -and -not (Test-Path $result)) {
        Start-Sleep -Seconds 1; $waited++
        if ($waited -ge $Pickup) {
            Remove-Item -Force -ErrorAction SilentlyContinue $req
            Write-Host "ui-agent: the Editor did not pick up the request within ${Pickup}s."
            Write-Host "  - Is Unity busy (importing, compiling, a modal dialog, Play Mode)? Bring it to the front and retry."
            Write-Host "  - Is the UI Agent Tools package installed in this project?"
            Write-Host "  - If Unity is actually closed, delete Temp\UnityLockfile (stale lock) or set UI_AGENT_MODE=batch."
            return 1
        }
    }
    $waited = 0
    while (-not (Test-Path $result)) {
        Start-Sleep -Seconds 1; $waited++
        if ($waited -ge $Timeout) { Write-Host "ui-agent: timed out after ${Timeout}s."; return 1 }
    }
    Start-Sleep -Milliseconds 200
    $code = Show-Result $result
    Remove-Item -Force -ErrorAction SilentlyContinue $result
    return $code
}

function Find-Unity {
    if ($env:UNITY_EDITOR_PATH) { return $env:UNITY_EDITOR_PATH }
    $version = ((Get-Content $VersionFile | Where-Object { $_ -match '^m_EditorVersion:' }) -replace '^m_EditorVersion:\s*', '').Trim()
    $default = "C:\Program Files\Unity\Hub\Editor\$version\Editor\Unity.exe"
    if (Test-Path $default) { return $default }
    $hubCfg = Join-Path $env:APPDATA "UnityHub\secondaryInstallPath.json"
    if (Test-Path $hubCfg) {
        $secondary = (Get-Content $hubCfg -Raw | ConvertFrom-Json)
        if ($secondary) {
            $candidate = Join-Path $secondary "$version\Editor\Unity.exe"
            if (Test-Path $candidate) { return $candidate }
        }
    }
    return $default
}

function Invoke-Batch {
    $unity = Find-Unity
    if (-not (Test-Path $unity)) {
        Write-Host "ui-agent: Unity executable not found at '$unity'. Install the project's Unity version via Unity Hub or set UNITY_EDITOR_PATH."
        return 1
    }
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $log = Join-Path $MapDir "logs\$Command-$stamp.log"
    $methods = @{ map = "Map"; validate = "Validate"; screenshots = "Screenshots"; run = "Run"; compile = "Compile"; selftest = "SelfTestAll"; baseline = "Baseline"; install = "Install"; all = "All" }
    $needsGpu = @("screenshots", "selftest", "all") -contains $Command

    $argList = @("-batchmode", "-projectPath", "`"$ProjectDir`"", "-logFile", "`"$log`"")
    if ($Command -eq "tests") {
        $argList += @("-runTests", "-testPlatform", "EditMode", "-assemblyNames", "UIAgentTools.Tests.Editor", "-testResults", "`"$(Join-Path $MapDir 'test-results.xml')`"")
    } else {
        if (-not $needsGpu) { $argList += "-nographics" }
        $argList += @("-quit", "-executeMethod", "UIAgentTools.Cli.$($methods[$Command])")
        if ($Arg -ne "") { $argList += @("-agentTarget", "`"$Arg`"") }
    }

    Write-Host "ui-agent: starting Unity in batch mode for '$Command' (log: UIAgentMap\logs\$Command-$stamp.log)..."
    $last = Join-Path $MapDir "last-result.json"
    Remove-Item -Force -ErrorAction SilentlyContinue $last
    $p = Start-Process -FilePath $unity -ArgumentList $argList -Wait -PassThru -NoNewWindow
    $code = $p.ExitCode

    if ($Command -eq "tests") {
        switch ($code) {
            0 { Write-Host "RESULT: OK - all tests passed (UIAgentMap\test-results.xml)" }
            2 { Write-Host "RESULT: FAILED - some tests failed (UIAgentMap\test-results.xml)" }
            default { Write-Host "RESULT: FAILED - test run error (exit $code), see $log" }
        }
        return $code
    }

    if (Test-Path $last) { $code = Show-Result $last }
    else { Write-Host "RESULT: FAILED - Unity exited with code $code before the command ran."; $code = 1 }

    if ($code -ne 0 -and (Test-Path $log)) {
        $errs = Select-String -Path $log -Pattern "error CS" | ForEach-Object { $_.Line } | Sort-Object -Unique | Select-Object -First 40
        if ($errs) { Write-Host "--- compile errors ---"; $errs | ForEach-Object { Write-Host $_ } }
        if (Select-String -Path $log -Pattern "another Unity instance is running" -Quiet) {
            Write-Host "ui-agent: Unity says the project is already open. Retry with the Editor open or close it."
        }
        Write-Host "--- last lines of $log ---"
        Get-Content $log -Tail 40 | ForEach-Object { Write-Host $_ }
    }
    return $code
}

if ($Mode -eq "editor" -or ($Mode -eq "auto" -and (Test-EditorOpen))) {
    if ($Command -eq "tests") { Write-Host "ui-agent: 'tests' needs Unity closed. While the Editor is open use 'validate' (same checks)."; exit 2 }
    $exit = Invoke-InEditor
    exit ([int]$exit)
}
$exit = Invoke-Batch
exit ([int]$exit)
