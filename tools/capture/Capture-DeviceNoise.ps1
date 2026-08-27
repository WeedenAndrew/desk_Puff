# desk_Puff device survey - READ ONLY
#
# Drives ble\desk-puff-ble.exe over its stdio protocol and reads everything the
# device will tell us. Every Lorax frame it sends carries a read opcode: 0x00
# GetAccessSeed, 0x01 UnlockAccess, 0x10 ReadShort. Opcode 0x11 (write) is never
# constructed here, and the helper itself rejects it with PermissionDenied even
# if it were. Nothing heats, nothing is written to a profile slot.
#
# Needs nothing installed. Windows PowerShell is enough.
#
# Two logs land beside this script:
#   survey-<stamp>.log    what the device said, decoded
#   frames-<stamp>.jsonl  every frame out and every frame back, raw

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$helper = Join-Path $here "ble\desk-puff-ble.exe"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$surveyLog = Join-Path $here "survey-$stamp.log"
$frameLog = Join-Path $here "frames-$stamp.jsonl"

if (-not (Test-Path $helper)) { throw "Helper not found at $helper" }

function Write-Survey([string]$text) {
    Write-Host $text
    [System.IO.File]::AppendAllText($surveyLog, $text + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
}

function Write-Frame([string]$direction, $payload) {
    $line = ([ordered]@{
        at        = (Get-Date).ToString("o")
        direction = $direction
        payload   = $payload
    } | ConvertTo-Json -Compress -Depth 6)
    [System.IO.File]::AppendAllText($frameLog, $line + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
}

# ---- helper process ---------------------------------------------------------

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $helper
$psi.Arguments = "--stdio"
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.CreateNoWindow = $true
$proc = [System.Diagnostics.Process]::Start($psi)

# Raw bytes onto the pipe, not the StreamWriter. A StreamWriter built from
# Encoding.UTF8 emits a byte order mark on its first write; the helper reads one
# JSON object per line, so the mark lands ahead of the opening brace and serde
# rejects the entire first request with "expected value at line 1 column 1".
# Every later line parses, which is what makes it read like a fluke rather than
# an encoding fault. Encoding.UTF8.GetBytes never emits the preamble, and this
# also avoids ProcessStartInfo.StandardInputEncoding, which does not exist on
# Windows PowerShell 5.1.
$stdin = $proc.StandardInput.BaseStream

# Touching Process.StandardInput builds a StreamWriter with AutoFlush = true,
# and that setter flushes at once, which emits the encoding preamble into the
# pipe before we have written a byte. The helper reads one JSON object per line,
# so the mark sits in front of our first opening brace and serde rejects that
# request with "expected value at line 1 column 1" while every later line parses
# cleanly. Send a bare newline first: the mark ends up alone on line one, the
# helper rejects that empty line instead, and we throw its answer away.
$stdin.Write([byte[]](0x0A), 0, 1)
$stdin.Flush()
$proc.StandardOutput.ReadLine() | Out-Null

function Invoke-Helper([hashtable]$request) {
    $json = $request | ConvertTo-Json -Compress
    Write-Frame "request" @{ json = $json }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json + "`n")
    $stdin.Write($bytes, 0, $bytes.Length)
    $stdin.Flush()
    $line = $proc.StandardOutput.ReadLine()
    if ($null -eq $line) { throw "The helper closed its pipe. It may have panicked; rebuild in debug for the message." }
    $response = $line | ConvertFrom-Json
    Write-Frame "response" $response
    return $response
}

# ---- Lorax framing ----------------------------------------------------------
# frame = [sequence u16 LE][opcode u8][body]
# read body = [offset u16 LE][size u16 LE][path UTF-8]

$script:sequence = 0

function New-Frame([byte]$opcode, [byte[]]$body) {
    $seq = $script:sequence
    $frame = New-Object byte[] (3 + $body.Length)
    $frame[0] = [byte]($seq -band 0xFF)
    $frame[1] = [byte](($seq -shr 8) -band 0xFF)
    $frame[2] = $opcode
    if ($body.Length -gt 0) { [Array]::Copy($body, 0, $frame, 3, $body.Length) }
    return @{ Frame = $frame; Sequence = $seq }
}

function Invoke-Lorax([byte]$opcode, [byte[]]$body, [string]$label) {
    $built = New-Frame $opcode $body
    $script:sequence = $script:sequence + 1
    $response = Invoke-Helper @{
        id                = $script:sequence
        operation         = "runCommand"
        frameBase64       = [Convert]::ToBase64String($built.Frame)
        expectedSequence  = $built.Sequence
    }
    if (-not $response.success) {
        Write-Survey ("  {0,-28} FAILED  {1}" -f $label, $response.error)
        return $null
    }
    $reply = [Convert]::FromBase64String($response.frameBase64)
    # reply = [sequence u16][headerByte u8][payload]
    if ($reply.Length -lt 3) { Write-Survey ("  {0,-28} short reply" -f $label); return $null }
    return $reply[3..($reply.Length - 1)]
}

function Read-Path([string]$path, [int]$size, [string]$label) {
    $pathBytes = [System.Text.Encoding]::UTF8.GetBytes($path)
    $body = New-Object byte[] (4 + $pathBytes.Length)
    $body[0] = 0; $body[1] = 0                                  # offset 0
    $body[2] = [byte]($size -band 0xFF)
    $body[3] = [byte](($size -shr 8) -band 0xFF)
    [Array]::Copy($pathBytes, 0, $body, 4, $pathBytes.Length)

    $payload = Invoke-Lorax 0x10 $body $label
    if ($null -eq $payload -or $payload.Length -eq 0) {
        Write-Survey ("  {0,-28} {1,-24} (no payload)" -f $label, $path)
        return
    }

    $hex = ($payload | ForEach-Object { $_.ToString("X2") }) -join " "
    $text = (($payload | ForEach-Object { if ($_ -ge 32 -and $_ -lt 127) { [char]$_ } else { "." } }) -join "")
    $extra = ""
    if ($payload.Length -eq 4) {
        $extra = "  u32={0}  f32={1}" -f `
            [BitConverter]::ToUInt32($payload, 0), `
            [Math]::Round([BitConverter]::ToSingle($payload, 0), 3)
    } elseif ($payload.Length -eq 1) {
        $extra = "  u8={0}" -f $payload[0]
    } elseif ($payload.Length -eq 2) {
        $extra = "  u16={0}" -f [BitConverter]::ToUInt16($payload, 0)
    }
    Write-Survey ("  {0,-28} {1,-24} {2,3}B  {3}  |{4}|{5}" -f $label, $path, $payload.Length, $hex, $text, $extra)
}

# ---- run --------------------------------------------------------------------

try {
    Write-Survey "desk_Puff device survey  $stamp"
    Write-Survey "READ ONLY. Opcodes used: 0x00 seed, 0x01 unlock, 0x10 read. No 0x11 write."
    Write-Survey ""

    Write-Survey "-- scan --"
    $scan = Invoke-Helper @{ id = 1; operation = "scan"; durationMilliseconds = 8000 }
    if (-not $scan.success) { Write-Survey "scan failed: $($scan.error)"; return }
    if (-not $scan.candidates -or $scan.candidates.Count -eq 0) {
        Write-Survey "no candidates. Device asleep, out of range, or held by the phone app."
        return
    }
    foreach ($c in $scan.candidates) {
        Write-Survey ("  {0}  rssi {1}  id {2}" -f $c.name, $c.signalStrength, $c.id)
    }

    $target = $scan.candidates[0]
    Write-Survey ""
    Write-Survey "-- connect to $($target.name) --"
    $connect = Invoke-Helper @{ id = 2; operation = "connect"; candidateId = $target.id }
    if (-not $connect.success) { Write-Survey "connect failed: $($connect.error)"; return }
    Write-Survey "  advertisedName: $($connect.advertisedName)"

    Write-Survey ""
    Write-Survey "-- lorax handshake --"
    $seed = Invoke-Lorax 0x00 @() "GetAccessSeed"
    if ($null -eq $seed -or $seed.Length -ne 16) {
        Write-Survey "  no 16-byte seed; reads may still work unauthenticated, continuing"
    } else {
        Write-Survey ("  seed: {0}" -f (($seed | ForEach-Object { $_.ToString("X2") }) -join " "))
        # key = SHA256(handshakeKey || seed)[0..15]
        $handshakeKey = [Convert]::FromBase64String("ZMZFYlbyb1scoSc3pd1x+w==")
        $input = New-Object byte[] 32
        [Array]::Copy($handshakeKey, 0, $input, 0, 16)
        [Array]::Copy($seed, 0, $input, 16, 16)
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $hash = $sha.ComputeHash($input)
        $key = $hash[0..15]
        $unlock = Invoke-Lorax 0x01 $key "UnlockAccess"
        Write-Survey ("  unlock: {0}" -f $(if ($null -eq $unlock) { "rejected" } else { "accepted" }))
    }

    Write-Survey ""
    Write-Survey "-- device --"
    Read-Path "/p/sys/hw/mdcd"   4  "model code"
    Read-Path "/p/sys/fw/ver"   32  "firmware version"
    Read-Path "/u/sys/name"     32  "device name"
    Read-Path "/p/htr/chmt"      4  "chamber type"
    Read-Path "/p/bat/cap"       4  "battery capacity"
    Read-Path "/p/bat/soc"       4  "battery charge"
    Read-Path "/p/bat/chg/stat"  4  "charge state"

    Write-Survey ""
    Write-Survey "-- session state --"
    Read-Path "/p/app/stat/id"    4  "operating state"
    Read-Path "/p/app/hcs"        4  "active profile index"
    Read-Path "/p/app/thc/name"  32  "active profile name"
    Read-Path "/p/app/thc/temp"   4  "active profile temp"
    Read-Path "/p/app/thc/time"   4  "active profile time"
    Read-Path "/p/app/htr/temp"   4  "heater temperature"
    Read-Path "/p/app/htr/tcmd"   4  "heater target"
    Read-Path "/p/app/stat/elap"  4  "elapsed"
    Read-Path "/p/app/stat/tott"  4  "total"

    Write-Survey ""
    Write-Survey "-- saved profile slots --"
    foreach ($i in 0..3) {
        Write-Survey ("  slot {0}" -f $i)
        Read-Path "/u/app/hc/$i/name"  32 "    name"
        Read-Path "/u/app/hc/$i/temp"   4 "    temperature"
        Read-Path "/u/app/hc/$i/time"   4 "    duration"
        Read-Path "/u/app/hc/$i/btmp"   4 "    boost temp"
        Read-Path "/u/app/hc/$i/btim"   4 "    boost time"
        Read-Path "/u/app/hc/$i/colr" 128 "    colorway"
    }

    Write-Survey ""
    Write-Survey "-- overrides and modes (read, never written) --"
    Read-Path "/p/app/mc"        4 "mode command"
    Read-Path "/p/app/tmpo"      4 "temperature override"
    Read-Path "/p/app/timo"      4 "time override"
    Read-Path "/u/app/ui/stlm"   4 "stealth mode"
    Read-Path "/p/app/ltrn/cmd" 64 "lantern"
}
finally {
    try { Invoke-Helper @{ id = 900; operation = "disconnect" } | Out-Null } catch { }
    try { Invoke-Helper @{ id = 901; operation = "shutdown" } | Out-Null } catch { }
    try { if (-not $proc.HasExited) { $proc.WaitForExit(3000) | Out-Null } } catch { }
    try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
    Write-Host ""
    Write-Host "survey : $surveyLog"
    Write-Host "frames : $frameLog"
}
