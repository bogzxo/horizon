# Shell integration for PowerShell, run by the terminal (Dusk, Hex's terminal page) as the shell starts and never
# written into anybody's profile. It says where the prompt starts and ends, where a command's output starts and how
# the command ended (FinalTerm's OSC 133, A B C D) and which folder the shell is in (OSC 7), round whatever prompt the
# profile already has. The terminal makes blocks of commands out of it, and knows where the typed line is to colour it.

if ($Global:__DuskIntegrated) { return }
$Global:__DuskIntegrated = $true
$Global:__DuskRan = $false

$Global:__DuskOriginalPrompt = $function:prompt

function Global:prompt {
    $succeeded = $?
    $code = if ($succeeded) { 0 } elseif ($LASTEXITCODE) { $LASTEXITCODE } else { 1 }
    $e = [char]27
    $bell = [char]7

    $said = ''
    if ($Global:__DuskRan) { $said += "$e]133;D;$code$bell" }
    $Global:__DuskRan = $false

    $folder = $ExecutionContext.SessionState.Path.CurrentLocation
    if ($folder.Provider.Name -eq 'FileSystem') {
        $said += "$e]7;file://$env:COMPUTERNAME/$($folder.ProviderPath -replace '\\', '/')$bell"
    }

    $said += "$e]133;A$bell"
    $said += & $Global:__DuskOriginalPrompt
    $said += "$e]133;B$bell"
    return $said
}

# The line read by PSReadLine, and the output's start said the moment it is handed over to run
if (Get-Command PSConsoleHostReadLine -ErrorAction SilentlyContinue) {
    $Global:__DuskReadLine = $function:PSConsoleHostReadLine
    function Global:PSConsoleHostReadLine {
        $line = & $Global:__DuskReadLine
        $Global:__DuskRan = $true
        [Console]::Write("$([char]27)]133;C$([char]7)")
        return $line
    }
}

# What commands the shell has, every alias, function and cmdlet, written once it is first idle so the prompt isn't
# kept waiting, for the terminal to colour the first word of a line green or red
if ($env:DUSK_COMMANDS_FILE) {
    $null = Register-EngineEvent -SourceIdentifier PowerShell.OnIdle -MaxTriggerCount 1 -Action {
        try {
            $folder = Split-Path -Parent $env:DUSK_COMMANDS_FILE
            if ($folder -and -not (Test-Path -LiteralPath $folder)) { $null = New-Item -ItemType Directory -Path $folder -Force }
            Get-Command -CommandType Alias, Function, Cmdlet, Filter -ErrorAction SilentlyContinue |
                ForEach-Object Name | Set-Content -LiteralPath $env:DUSK_COMMANDS_FILE -Encoding utf8
        } catch { }
    }
}

# The dawn command. dawn file.cs edits the file in place of the terminal, Dawn's editor taking over the pane until
# Ctrl+Q, dawn -open path hands a file or a folder to the Dawn that's running, dawn -window path opens a new Dawn
# window on it, and dawn on its own opens the folder the shell is in. The terminal does all of it, the shell only says
# what was asked for with a private OSC (1717) and goes straight back to its prompt.
function Global:dawn {
    $mode = 'inline'
    $target = ''
    foreach ($word in $args) {
        if ($word -match '^--?(w|window)$') { $mode = 'window' }
        elseif ($word -match '^--?(o|open)$') { $mode = 'open' }
        else { $target = [string]$word }
    }

    if (-not $target) {
        if ($mode -eq 'inline') { $mode = 'open' }
        $target = $ExecutionContext.SessionState.Path.CurrentFileSystemLocation.ProviderPath
    } else {
        $target = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($target)
    }

    [Console]::Write("$([char]27)]1717;dawn;$mode;$target$([char]7)")
}

# The tui command. tui on its own says which programs with a screen of their own there are and how to start them, tui
# name starts one in a tab of its own, with -split or -down in a split instead. Nobody found them in the palette.
function Global:tui {
    $where = 'tab'
    $name = ''
    foreach ($word in $args) {
        if ($word -match '^--?split$') { $where = 'split' }
        elseif ($word -match '^--?down$') { $where = 'down' }
        else { $name = [string]$word }
    }

    if (-not $name) {
        Write-Host 'The programs with a screen of their own, each in a tab unless told -split or -down:'
        Write-Host '  tui files       a file picker, Ctrl+Shift+O'
        Write-Host '  tui git         the status and the log of the repository, Ctrl+Shift+G'
        Write-Host '  tui processes   what is running, and ending it'
        Write-Host '  tui settings    the terminal''s settings'
        Write-Host '  tui repl        HIDL with a list of names, in a split'
        Write-Host 'Your own go in the tui folder of Dusk''s home, Ctrl+Shift+P and "tui" lists the lot.'
        return
    }

    [Console]::Write("$([char]27)]1717;tui;$where;$name$([char]7)")
}
