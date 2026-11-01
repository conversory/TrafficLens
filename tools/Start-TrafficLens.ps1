param([switch]$InspectOnly)
$ErrorActionPreference = 'Stop'

# The existing app checkbox remains the source of truth. This launcher never
# enables a disabled entry and never evaluates a registry command as a script.
function Get-TrafficLensStartupDecision {
    param([AllowNull()][string]$Command, [bool]$DisabledByWindows)
    if ([string]::IsNullOrWhiteSpace($Command)) {
        return [pscustomobject]@{ State='Disabled'; Executable=$null }
    }
    if ($DisabledByWindows) {
        return [pscustomobject]@{ State='DisabledByWindows'; Executable=$null }
    }
    if ($Command -notmatch '^"([^"\r\n]+\\TrafficLens\.exe)"\s+--background\s+--startup\s*$') {
        return [pscustomobject]@{ State='InvalidCommand'; Executable=$null }
    }
    $target = $Matches[1]
    if (-not [IO.Path]::IsPathRooted($target)) {
        return [pscustomobject]@{ State='InvalidCommand'; Executable=$null }
    }
    return [pscustomobject]@{ State='Ready'; Executable=$target }
}

function Write-StartupStatus {
    param([string]$State, [string]$Executable, [int[]]$ProcessIds=@(), [string]$Detail='')
    $status = [ordered]@{
        Time=(Get-Date).ToString('o'); State=$State; Executable=$Executable
        User=[Security.Principal.WindowsIdentity]::GetCurrent().Name
        SID=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        Profile=$env:USERPROFILE; Is64Bit=[Environment]::Is64BitProcess
        ProcessIds=@($ProcessIds); Detail=$Detail
    }
    $json = $status | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'startup-status.json'), $json, [Text.UTF8Encoding]::new($false))
    Write-Output $json
}

try {
    $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
    try { $command = if ($runKey) { $runKey.GetValue('TrafficLens', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } else { $null } }
    finally { if ($runKey) { $runKey.Dispose() } }
    $approvalKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run')
    try { $approval = if ($approvalKey) { $approvalKey.GetValue('TrafficLens') } else { $null } }
    finally { if ($approvalKey) { $approvalKey.Dispose() } }
    $disabled = $approval -is [byte[]] -and $approval.Length -gt 0 -and $approval[0] -in @(3,7)
    $decision = Get-TrafficLensStartupDecision -Command $command -DisabledByWindows $disabled
    if ($InspectOnly) { $decision | ConvertTo-Json; exit 0 }
    if ($decision.State -in @('Disabled','DisabledByWindows')) {
        Write-StartupStatus -State $decision.State
        exit 0
    }
    if ($decision.State -ne 'Ready') { throw 'The TrafficLens startup command is not a supported executable command.' }
    $exe = $decision.Executable
    if (-not [IO.File]::Exists($exe)) { throw 'The registered TrafficLens executable is unavailable.' }
    $session = (Get-Process -Id $PID).SessionId
    $running = @(Get-Process -Name TrafficLens -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session -and $_.Path -eq $exe })
    if ($running.Count -gt 0) {
        Write-StartupStatus -State 'AlreadyRunning' -Executable $exe -ProcessIds @($running.Id)
        exit 0
    }
    $started = Start-Process -FilePath $exe -ArgumentList '--background','--startup' -WorkingDirectory ([IO.Path]::GetDirectoryName($exe)) -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 5
    $running = @(Get-Process -Name TrafficLens -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session -and $_.Path -eq $exe })
    if ($running.Count -eq 0) { throw 'TrafficLens did not remain running after startup.' }
    Write-StartupStatus -State 'Started' -Executable $exe -ProcessIds @($running.Id)
    exit 0
}
catch {
    try { Write-StartupStatus -State 'Failed' -Executable $exe -Detail $_.Exception.Message } catch { }
    Write-Error $_ -ErrorAction Continue
    exit 1
}
