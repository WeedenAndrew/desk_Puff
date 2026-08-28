# desk_Puff device survey - READ ONLY
#
# Drives ble\desk-puff-ble.exe over its stdio protocol and reads everything the
# device will tell us. The only Lorax verbs this script permits are 0x00 seed,
# 0x01 unlock, 0x02 discovery probe, and 0x10 read. Nothing heats and no device
# setting or profile slot is changed.
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
$script:statusRecords = @()

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
    if ([byte[]]@(0x00, 0x01, 0x02, 0x10) -notcontains $opcode) {
        throw ("Opcode 0x{0:X2} is outside this read-only prober's allowlist." -f $opcode)
    }

    $seq = $script:sequence
    $frame = New-Object byte[] (3 + $body.Length)
    $frame[0] = [byte]($seq -band 0xFF)
    $frame[1] = [byte](($seq -shr 8) -band 0xFF)
    $frame[2] = $opcode
    if ($body.Length -gt 0) { [Array]::Copy($body, 0, $frame, 3, $body.Length) }
    return @{ Frame = $frame; Sequence = $seq }
}

function ConvertFrom-LoraxReply([byte[]]$reply) {
    if ($null -eq $reply -or $reply.Length -lt 3) {
        throw "A Lorax reply must contain a two-byte sequence and one-byte status."
    }

    [byte[]]$payload = @()
    if ($reply.Length -gt 3) {
        $payload = [byte[]]$reply[3..($reply.Length - 1)]
    }

    return [pscustomobject]@{
        Sequence = [BitConverter]::ToUInt16($reply, 0)
        Status   = [byte]$reply[2]
        Payload  = $payload
    }
}

function Format-Hex([byte[]]$bytes) {
    if ($null -eq $bytes -or $bytes.Length -eq 0) { return "(empty)" }
    return (($bytes | ForEach-Object { $_.ToString("X2") }) -join " ")
}

function Format-Ascii([byte[]]$bytes) {
    if ($null -eq $bytes -or $bytes.Length -eq 0) { return "" }
    return (($bytes | ForEach-Object {
        if ($_ -ge 32 -and $_ -lt 127) { [char]$_ } else { "." }
    }) -join "")
}

function Add-StatusRecord([byte]$status, [string]$target) {
    $script:statusRecords += [pscustomobject]@{
        Status = $status
        Target = $target
    }
}

function Invoke-Lorax([byte]$opcode, [byte[]]$body, [string]$label, [string]$target) {
    $built = New-Frame $opcode $body
    $script:sequence = $script:sequence + 1
    $response = Invoke-Helper @{
        id                = $script:sequence
        operation         = "runCommand"
        frameBase64       = [Convert]::ToBase64String($built.Frame)
        expectedSequence  = $built.Sequence
    }
    if (-not $response.success) {
        Write-Survey ("  {0,-28} STATUS=n/a FAILED  {1}" -f $label, $response.error)
        return $null
    }
    $reply = [Convert]::FromBase64String($response.frameBase64)
    if ($reply.Length -lt 3) {
        Write-Survey ("  {0,-28} STATUS=n/a FAILED  short reply ({1} bytes)" -f $label, $reply.Length)
        return $null
    }

    $parsed = ConvertFrom-LoraxReply $reply
    Add-StatusRecord $parsed.Status $target
    return $parsed
}

function New-ReadBody([string]$path, [int]$size) {
    $pathBytes = [System.Text.Encoding]::UTF8.GetBytes($path)
    $body = New-Object byte[] (4 + $pathBytes.Length)
    $body[0] = 0; $body[1] = 0                                  # offset 0
    $body[2] = [byte]($size -band 0xFF)
    $body[3] = [byte](($size -shr 8) -band 0xFF)
    [Array]::Copy($pathBytes, 0, $body, 4, $pathBytes.Length)
    return $body
}

function Read-Path([string]$path, [int]$size, [string]$label) {
    $body = New-ReadBody $path $size
    $result = Invoke-Lorax 0x10 $body $label $path
    if ($null -eq $result) { return }

    $statusText = "0x{0:X2}" -f $result.Status
    [byte[]]$payload = $result.Payload
    if ($result.Status -ne 0) {
        Write-Survey ("  {0,-28} {1,-24} STATUS={2} FAILED  payload={3}" -f `
            $label, $path, $statusText, (Format-Hex $payload))
        return
    }

    if ($payload.Length -eq 0) {
        Write-Survey ("  {0,-28} {1,-24} STATUS={2} SUCCESS  (no payload)" -f `
            $label, $path, $statusText)
        return
    }

    $hex = Format-Hex $payload
    $text = Format-Ascii $payload
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
    Write-Survey ("  {0,-28} {1,-24} STATUS={2} {3,3}B  {4}  |{5}|{6}" -f `
        $label, $path, $statusText, $payload.Length, $hex, $text, $extra)
}

function Invoke-RawProbe([byte]$opcode, [byte[]]$body, [string]$label, [string]$target) {
    $result = Invoke-Lorax $opcode $body $label $target
    if ($null -eq $result) { return }

    $statusText = "0x{0:X2}" -f $result.Status
    $outcome = if ($result.Status -eq 0) { "SUCCESS" } else { "FAILED" }
    [byte[]]$payload = $result.Payload
    Write-Survey ("  {0,-28} {1,-24} STATUS={2} {3}  {4,3}B  {5}  |{6}|" -f `
        $label, $target, $statusText, $outcome, $payload.Length,
        (Format-Hex $payload), (Format-Ascii $payload))
}

function Read-RawPath([string]$path, [int]$size, [string]$label) {
    Invoke-RawProbe 0x10 (New-ReadBody $path $size) $label $path
}

function Write-StatusSummary {
    Write-Survey ""
    Write-Survey "-- status summary --"
    if ($script:statusRecords.Count -eq 0) {
        Write-Survey "  STATUS=none count=0 paths/targets: (no Lorax replies)"
        return
    }

    $groups = $script:statusRecords | Group-Object Status | Sort-Object { [int]$_.Name }
    foreach ($group in $groups) {
        $status = [byte][int]$group.Name
        $targets = @($group.Group | ForEach-Object { $_.Target } | Sort-Object -Unique)
        Write-Survey ("  STATUS=0x{0:X2} count={1} paths/targets: {2}" -f `
            $status, $group.Count, ($targets -join ", "))
    }
}

# ---- run --------------------------------------------------------------------

try {
    Write-Survey "desk_Puff device survey  $stamp"
    Write-Survey "READ ONLY. Opcodes used: 0x00 seed, 0x01 unlock, 0x02 probe, 0x10 read."
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
    $seedResult = Invoke-Lorax 0x00 @() "GetAccessSeed" "<empty body: GetAccessSeed>"
    if ($null -eq $seedResult) {
        Write-Survey "  handshake continuation       STATUS=n/a no seed result; continuing"
    } elseif ($seedResult.Status -ne 0) {
        Write-Survey ("  GetAccessSeed                STATUS=0x{0:X2} FAILED  payload={1}" -f `
            $seedResult.Status, (Format-Hex $seedResult.Payload))
    } elseif ($seedResult.Payload.Length -ne 16) {
        Write-Survey ("  GetAccessSeed                STATUS=0x00 SUCCESS  unexpected {0}B payload={1}; continuing" -f `
            $seedResult.Payload.Length, (Format-Hex $seedResult.Payload))
    } else {
        [byte[]]$seed = $seedResult.Payload
        Write-Survey ("  GetAccessSeed                STATUS=0x00 SUCCESS   16B  {0}" -f (Format-Hex $seed))
        # key = SHA256(handshakeKey || seed)[0..15]
        $handshakeKey = [Convert]::FromBase64String("ZMZFYlbyb1scoSc3pd1x+w==")
        $input = New-Object byte[] 32
        [Array]::Copy($handshakeKey, 0, $input, 0, 16)
        [Array]::Copy($seed, 0, $input, 16, 16)
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $hash = $sha.ComputeHash($input)
        $key = $hash[0..15]
        $unlock = Invoke-Lorax 0x01 $key "UnlockAccess" "<challenge response: UnlockAccess>"
        if ($null -eq $unlock) {
            Write-Survey "  UnlockAccess                 STATUS=n/a no reply"
        } else {
            $outcome = if ($unlock.Status -eq 0) { "SUCCESS" } else { "FAILED" }
            Write-Survey ("  UnlockAccess                 STATUS=0x{0:X2} {1}  payload={2}" -f `
                $unlock.Status, $outcome, (Format-Hex $unlock.Payload))
        }
    }

    Write-Survey ""
    Write-Survey "-- opcode 0x02 probes (raw, no interpretation) --"
    Invoke-RawProbe 0x02 @() "empty body" "<empty body>"
    $verbProbePath = "/p/sys/fw/ver"
    Invoke-RawProbe 0x02 (New-ReadBody $verbProbePath 32) "read-shaped body" $verbProbePath

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
        # 125, not 128. Opcode 0x02 reports this device's maximum single read as
        # 125 bytes (7D 00), and 128 is the only size in this whole survey that
        # exceeds it — which is also the only read that fails with status 0x7A.
        # The path was never wrong; the request was three bytes too large.
        Read-RawPath "/u/app/hc/$i/colr" 125 "    colorway /u"
        Read-RawPath "/p/app/hc/$i/colr" 125 "    colorway /p"
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
    Write-StatusSummary
    try { Invoke-Helper @{ id = 900; operation = "disconnect" } | Out-Null } catch { }
    try { Invoke-Helper @{ id = 901; operation = "shutdown" } | Out-Null } catch { }
    try { if (-not $proc.HasExited) { $proc.WaitForExit(3000) | Out-Null } } catch { }
    try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
    Write-Host ""
    Write-Host "survey : $surveyLog"
    Write-Host "frames : $frameLog"
}
