# desk_Puff device survey - READ ONLY
#
# Drives ble\desk-puff-ble.exe over its stdio protocol and reads everything the
# device will tell us. The only Lorax verbs this script permits are 0x00 seed,
# 0x01 unlock, 0x02 discovery probe, and 0x10 read. Nothing heats and no device
# setting or profile slot is changed.
# Device-write frames cannot be constructed: New-Frame accepts only the four
# read/authentication verbs listed above. No heater command is issued.
#
# Needs nothing installed. Windows PowerShell is enough.
#
# Two logs land beside this script:
#   survey-<stamp>.log    what the device said, decoded
#   frames-<stamp>.jsonl  every frame out and every frame back, raw

param([switch]$WatchSession)

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
$psi.RedirectStandardError = [bool]$WatchSession
$psi.CreateNoWindow = $true
$proc = [System.Diagnostics.Process]::Start($psi)
if ($WatchSession) { $stderrDrain = $proc.StandardError.ReadToEndAsync() }

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

function Invoke-Helper([hashtable]$request, [int]$timeoutMilliseconds = 0) {
    if ($script:helperUnavailable) { throw $script:helperUnavailable }
    $json = $request | ConvertTo-Json -Compress
    Write-Frame "request" @{ json = $json }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json + "`n")
    $stdin.Write($bytes, 0, $bytes.Length)
    $stdin.Flush()
    if ($timeoutMilliseconds -gt 0) {
        $pending = $proc.StandardOutput.ReadLineAsync()
        if (-not $pending.Wait($timeoutMilliseconds)) {
            # A late response cannot safely be paired with another request. Keep
            # recording rows, but stop using this pipe; never reconnect or retry.
            $script:helperUnavailable = "Helper reply timed out; pipe retired to avoid response misalignment."
            throw $script:helperUnavailable
        }
        $line = $pending.GetAwaiter().GetResult()
    } else {
        $line = $proc.StandardOutput.ReadLine()
    }
    if ($null -eq $line) {
        if ($WatchSession) { $script:helperUnavailable = "The helper closed its pipe." }
        throw "The helper closed its pipe. It may have panicked; rebuild in debug for the message."
    }
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
    Write-Survey ("  {0} REQUEST label={1} target={2} sequence={3} opcode=0x{4:X2} frame={5}" -f `
        (Get-Date -Format "HH:mm:ss.fff"), $label, $target, $built.Sequence, $opcode, (Format-Hex $built.Frame))
    $script:sequence = $script:sequence + 1
    $response = Invoke-Helper @{
        id                = $script:sequence
        operation         = "runCommand"
        frameBase64       = [Convert]::ToBase64String($built.Frame)
        expectedSequence  = $built.Sequence
    }
    if (-not $response.success) {
        Write-Survey ("  {0,-28} target={1} sequence={2} STATUS=n/a FAILED no reply frame: {3}" -f `
            $label, $target, $built.Sequence, $response.error)
        return $null
    }
    $reply = [Convert]::FromBase64String($response.frameBase64)
    if ($reply.Length -lt 3) {
        Write-Survey ("  {0,-28} STATUS=n/a FAILED short reply ({1} bytes) frame={2}" -f `
            $label, $reply.Length, (Format-Hex $reply))
        return $null
    }

    $parsed = ConvertFrom-LoraxReply $reply
    Write-Survey ("  {0} REPLY label={1} target={2} sequence={3} STATUS=0x{4:X2} payloadLength={5} frame={6}" -f `
        (Get-Date -Format "HH:mm:ss.fff"), $label, $target, $parsed.Sequence, $parsed.Status, $parsed.Payload.Length, (Format-Hex $reply))
    Add-StatusRecord $parsed.Status $target
    return $parsed
}

function New-ReadBody([string]$path, [int]$size, [int]$offset = 0) {
    $pathBytes = [System.Text.Encoding]::UTF8.GetBytes($path)
    $body = New-Object byte[] (4 + $pathBytes.Length)
    $body[0] = [byte]($offset -band 0xFF)
    $body[1] = [byte](($offset -shr 8) -band 0xFF)
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

function Read-RawPath([string]$path, [int]$size, [string]$label, [int]$offset = 0) {
    Invoke-RawProbe 0x10 (New-ReadBody $path $size $offset) $label $path
}

function Invoke-LimitExperiments {
    Write-Survey ""
    Write-Survey "-- payload ceiling: single raw reads, offset 0, no retry or chunking --"
    # This value measured 517 bytes on PEAKSHI V2 firmware 39 on 2026-08-31.
    # It is large enough that a short value cannot explain a 125-byte response.
    foreach ($size in @(126, 125, 124)) {
        $label = "payload-ceiling requested=$size offset=0"
        Read-RawPath "/u/app/hc/0/colr" $size $label
    }

    Write-Survey ""
    Write-Survey "-- path-length differential: presumed nonexistent ASCII paths --"
    # Same nonexistent prefix, differing only in suffix length. Existence is not
    # guaranteed; a successful read invalidates that negative-control assumption.
    foreach ($length in @(20, 50)) {
        $path = "/p/zzprobe/" + ("x" * ($length - 11))
        if ($path.Length -ne $length) { throw "Probe path length mismatch." }
        $label = "path-length chars=$length utf8Bytes=$length requested=1 offset=0"
        Read-RawPath $path 1 $label
    }
    Write-Survey "Identical errors are inconclusive; different errors motivate a boundary test, not a proven limit of 40."
    Write-Survey "Capabilities come from CompatibilityCatalog by family; model/firmware reads below identify that lookup. No capability wire path is known."
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

function Read-WatchValue([string]$path, [bool]$isState, [int]$timeoutMilliseconds) {
    $size = if ($isState) { 1 } else { 4 }
    $built = New-Frame 0x10 (New-ReadBody $path $size)
    $script:sequence++
    $response = Invoke-Helper @{
        id = $script:sequence
        operation = "runCommand"
        frameBase64 = [Convert]::ToBase64String($built.Frame)
        expectedSequence = $built.Sequence
    } $timeoutMilliseconds
    if (-not $response.success) { throw "Helper error: $($response.error)" }
    $reply = ConvertFrom-LoraxReply ([Convert]::FromBase64String($response.frameBase64))
    if ($reply.Sequence -ne $built.Sequence) { throw "Reply sequence mismatch." }
    Add-StatusRecord $reply.Status $path
    if ($reply.Status -ne 0) { return "S{0:X2}" -f $reply.Status }
    $script:watchLastSuccess = "{0} {1}" -f (Get-Date -Format "o"), $path
    if ($reply.Payload.Length -ne $size) { return "LEN=$($reply.Payload.Length)" }
    if ($isState) { return $reply.Payload[0].ToString([Globalization.CultureInfo]::InvariantCulture) }
    return [BitConverter]::ToSingle($reply.Payload, 0).ToString("0.000", [Globalization.CultureInfo]::InvariantCulture)
}

function Watch-Session {
    $paths = @('/p/app/tmpo', '/p/app/timo', '/p/app/htr/tcmd',
        '/p/app/htr/temp', '/p/app/stat/elap', '/p/app/stat/id', '/p/app/thc/temp')
    $script:watchLastSuccess = "none"
    $script:helperUnavailable = $null
    Write-Survey "WATCH BEGIN: 120 seconds, 1500 ms cadence. Start the session and apply any boost on the device itself."
    Write-Survey "Values are sequential reads, not atomic. Sxx=status failure; ERR=transport/read error; END=deadline; no stale values reused."
    Write-Survey "timestamp                         elapsed_ms       tmpo       timo       tcmd       temp       elap      state profile_C pass_ms notes"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $next = 0L
    $previousState = $null
    while ($clock.ElapsedMilliseconds -lt 120000) {
        $delay = [Math]::Min($next, 120000) - $clock.ElapsedMilliseconds
        if ($delay -gt 0) { Start-Sleep -Milliseconds ([int]$delay) }
        if ($clock.ElapsedMilliseconds -ge 120000) { break }
        $started = $clock.ElapsedMilliseconds
        $timestamp = Get-Date -Format "o"
        $cells = @()
        $notes = @()
        foreach ($path in $paths) {
            $remaining = 120000 - $clock.ElapsedMilliseconds
            if ($remaining -le 0) { $cells += 'END'; continue }
            try {
                $cells += Read-WatchValue $path ($path -eq '/p/app/stat/id') ([int][Math]::Min(4000, $remaining))
            } catch {
                $cells += 'ERR'
                $message = $_.Exception.Message -replace '[\r\n]+', ' '
                $notes += "$(Get-Date -Format 'HH:mm:ss.fff') $path $message"
                Write-Frame "watch-error" @{ path = $path; error = $message; lastSuccessfulRead = $script:watchLastSuccess }
            }
        }
        if ($cells[5] -match '^\d+$') {
            if ($null -ne $previousState -and $previousState -ne $cells[5]) {
                $notes += "state=$previousState->$($cells[5])"
            }
            $previousState = $cells[5]
        }
        if ($cells -contains 'ERR') { $notes += "lastSuccessfulRead=$script:watchLastSuccess" }
        Write-Survey ("{0} {1,10} {2,10} {3,10} {4,10} {5,10} {6,10} {7,10} {8,9} {9,7} {10}" -f `
            $timestamp, $started, $cells[0], $cells[1], $cells[2], $cells[3], $cells[4], $cells[5], $cells[6],
            ($clock.ElapsedMilliseconds - $started), ($notes -join '; '))
        # Anchor cadence to elapsed time; slow failures skip slots instead of
        # adding 1.5 s after every pass or issuing a burst of catch-up requests.
        $next = ([Math]::Floor($clock.ElapsedMilliseconds / 1500) + 1) * 1500
    }
    Write-Survey ("WATCH END {0} elapsed_ms={1} lastSuccessfulRead={2}" -f `
        (Get-Date -Format 'o'), $clock.ElapsedMilliseconds, $script:watchLastSuccess)
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
    if ($WatchSession) {
        Watch-Session
        return
    }
    Write-Survey "-- opcode 0x02 probes (raw, no interpretation) --"
    Invoke-RawProbe 0x02 @() "empty body" "<empty body>"
    $verbProbePath = "/p/sys/fw/ver"
    Invoke-RawProbe 0x02 (New-ReadBody $verbProbePath 32) "read-shaped body" $verbProbePath

    Invoke-LimitExperiments

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
        # Keep the working 125-byte reads. The experiment above tests whether
        # the first GetLimits field actually denotes a read-payload ceiling.
        Read-RawPath "/u/app/hc/$i/colr" 125 "    colorway /u"
        Read-RawPath "/p/app/hc/$i/colr" 125 "    colorway /p"
    }

    # The colorway value is longer than one 125-byte read, so the application's
    # ReadAllAsync fetches it in chunks at increasing offsets and joins them.
    # Every capture so far has read offset 0 only, which means the second chunk
    # has never been observed even though the app depends on it. If the assembled
    # CBOR is incomplete or malformed, this is where it happens.
    Write-Survey ""
    Write-Survey "-- colorway continuation, slot 0 (offsets the app actually uses) --"
    Read-RawPath "/u/app/hc/0/colr" 125 "  offset 125" 125
    Read-RawPath "/u/app/hc/0/colr" 125 "  offset 250" 250
    Read-RawPath "/u/app/hc/0/colr" 125 "  offset 375" 375

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
    $cleanupTimeout = if ($WatchSession) { 4000 } else { 0 }
    try { Invoke-Helper @{ id = 900; operation = "disconnect" } $cleanupTimeout | Out-Null } catch { }
    try { Invoke-Helper @{ id = 901; operation = "shutdown" } $cleanupTimeout | Out-Null } catch { }
    try { if (-not $proc.HasExited) { $proc.WaitForExit(3000) | Out-Null } } catch { }
    try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
    if ($WatchSession -and $stderrDrain.Wait(3000)) {
        Write-Frame "helper-stderr" @{ text = $stderrDrain.GetAwaiter().GetResult() }
    }
    Write-Host ""
    Write-Host "survey : $surveyLog"
    Write-Host "frames : $frameLog"
}
