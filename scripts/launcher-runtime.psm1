# Only an app's explicit registration grants ownership. Never infer it from command lines or paths.
function Get-LauncherOwnerFile {
    param([Parameter(Mandatory = $true)][string]$RepoDir)
    return Join-Path (Join-Path $RepoDir '.git') 'launcher-runtime-owner'
}

function Claim-LauncherRuntime {
    param([string]$Token, [string]$RepoDir)
    $ownerFile = Get-LauncherOwnerFile -RepoDir $RepoDir
    $temporaryFile = "$ownerFile.$PID.$([guid]::NewGuid().ToString('N'))"
    [System.IO.File]::WriteAllText($temporaryFile, "$Token`n", [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryFile -Destination $ownerFile -Force
}

function Test-LauncherRuntimeOwner {
    param([string]$Token, [string]$RepoDir)
    $ownerFile = Get-LauncherOwnerFile -RepoDir $RepoDir
    return (Test-Path -LiteralPath $ownerFile) -and
        ((Get-Content -LiteralPath $ownerFile -Raw).Trim() -ceq $Token)
}

function Release-LauncherRuntime {
    param([string]$Token, [string]$RepoDir)
    if (Test-LauncherRuntimeOwner -Token $Token -RepoDir $RepoDir) {
        Remove-Item -LiteralPath (Get-LauncherOwnerFile -RepoDir $RepoDir) -Force -ErrorAction SilentlyContinue
    }
}

function Get-RuntimeRegistration {
    param([string]$RepoDir)
    $file = Join-Path (Join-Path $RepoDir '.git') 'launcher-runtime-process'
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    $fields = @(Get-Content -LiteralPath $file)
    if ($fields.Count -ne 3) { return $null }
    $processId = 0
    $started = 0L
    if (-not [int]::TryParse($fields[1], [ref]$processId) -or $processId -lt 0 -or
        -not [long]::TryParse($fields[2], [ref]$started)) { return $null }
    return [pscustomobject]@{ Token = $fields[0]; ProcessId = $processId; Started = $started }
}

function Get-RegisteredRuntimeProcess {
    param($Registration)
    if ($null -eq $Registration -or $Registration.ProcessId -le 0) { return $null }
    $process = Get-Process -Id $Registration.ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    try {
        $started = ([DateTimeOffset]$process.StartTime.ToUniversalTime()).ToUnixTimeSeconds()
        if ($started -eq $Registration.Started) { return $process }
    } catch [System.ComponentModel.Win32Exception] {
        # An inaccessible process cannot establish ownership.
    } catch [System.InvalidOperationException] {
        # The process exited while its identity was being read.
    }
    return $null
}

function Stop-OwnedRuntime {
    param([string]$Label, [string]$RepoDir, [string]$Token, [switch]$RequireCurrentRegistration)
    if (-not (Test-LauncherRuntimeOwner -Token $Token -RepoDir $RepoDir)) { return }
    $registration = Get-RuntimeRegistration -RepoDir $RepoDir
    if ($null -eq $registration -or ($RequireCurrentRegistration -and $registration.Token -cne $Token)) { return }
    $process = Get-RegisteredRuntimeProcess -Registration $registration
    if ($null -eq $process) { return }
    Write-Host "Stopping the existing $Label runtime (pid $($registration.ProcessId))."
    if (-not (Test-LauncherRuntimeOwner -Token $Token -RepoDir $RepoDir)) { return }
    $process = Get-RegisteredRuntimeProcess -Registration $registration
    if ($null -eq $process) { return }
    try {
        $null = $process.CloseMainWindow()
    } catch [System.InvalidOperationException] {
        # Exiting between identity validation and the close request needs no replacement action.
        return
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 100
        $process = Get-RegisteredRuntimeProcess -Registration $registration
    } while ($null -ne $process -and [DateTime]::UtcNow -lt $deadline)
    if (-not (Test-LauncherRuntimeOwner -Token $Token -RepoDir $RepoDir)) { return }
    $process = Get-RegisteredRuntimeProcess -Registration $registration
    if ($null -ne $process) { Stop-Process -InputObject $process -Force -ErrorAction SilentlyContinue }
}

function Wait-OwnedRuntime {
    param([string]$Label, [string]$RepoDir, [string]$Token, [int]$TimeoutSeconds = 30)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (-not (Test-LauncherRuntimeOwner -Token $Token -RepoDir $RepoDir)) {
            throw 'This launch was superseded by another launcher.'
        }
        $registration = Get-RuntimeRegistration -RepoDir $RepoDir
        if ($null -ne $registration -and $registration.Token -ceq $Token) {
            if ($registration.ProcessId -eq 0 -or $null -ne (Get-RegisteredRuntimeProcess -Registration $registration)) { return }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "$Label did not start within ${TimeoutSeconds}s."
}

Export-ModuleMember -Function Claim-LauncherRuntime, Test-LauncherRuntimeOwner, Release-LauncherRuntime, Stop-OwnedRuntime, Wait-OwnedRuntime
