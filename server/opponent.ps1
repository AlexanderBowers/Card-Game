# A scripted online opponent, for testing the game's online table against a real match.
# It makes an account, names itself, joins the quick-match queue and plays sensibly: plays a
# Modifier that lands it on 18-20, holds at 17+, draws otherwise. Logs every state it sees.
#
#   powershell -ExecutionPolicy Bypass -File server/opponent.ps1 -Base http://127.0.0.1:5080 -Seconds 120
param([string]$Base = "http://127.0.0.1:5080", [int]$Seconds = 120, [string]$Name = "Test Opponent")
$ErrorActionPreference = "Stop"

function Api($method, $path, $token, $body) {
    $headers = @{}
    if ($token) { $headers["Authorization"] = "Bearer $token" }
    $req = @{ Method = $method; Uri = "$Base$path"; Headers = $headers; ContentType = "application/json" }
    if ($body) { $req["Body"] = ($body | ConvertTo-Json -Compress) }
    Invoke-RestMethod @req
}

$acct = Api POST "/v1/accounts"
$sess = Api POST "/v1/sessions" $null @{ accountId = $acct.accountId; secret = $acct.secret }
$token = $sess.token
Api PUT "/v1/me/name" $token @{ name = $Name } | Out-Null

$ws = New-Object System.Net.WebSockets.ClientWebSocket
$ws.ConnectAsync([Uri](($Base -replace "^http", "ws") + "/v1/play"), [Threading.CancellationToken]::None).Wait()
function Send($obj) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json -Compress))
    $ws.SendAsync([ArraySegment[byte]]::new($bytes), "Text", $true, [Threading.CancellationToken]::None).Wait()
}
Send @{ type = "auth"; token = $token; version = 1 }
Send @{ type = "queue" }
"opponent queued as $Name"

$buf = New-Object byte[] 131072
$pending = $null
$deadline = (Get-Date).AddSeconds($Seconds)
$lastActedTurn = ""
while ((Get-Date) -lt $deadline -and $ws.State -eq "Open") {
    if (-not $pending) { $pending = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), [Threading.CancellationToken]::None) }
    if (-not $pending.Wait(300)) { continue }
    $r = $pending.Result; $pending = $null
    if ($r.MessageType -eq "Close") { "<< CLOSE $($ws.CloseStatus) $($ws.CloseStatusDescription)"; break }
    $m = [Text.Encoding]::UTF8.GetString($buf, 0, $r.Count) | ConvertFrom-Json
    "<< $($m.type) $(if($m.code){$m.code})"
    switch ($m.type) {
        "matchStart" { "MATCH START vs $($m.opponent.name)" }
        "setEnd"     { "SET END: $($m.winner) ($($m.yourScore) v $($m.theirScore))" }
        "matchEnd"   { "MATCH END: $($m.winner) ($($m.reason))"; $deadline = Get-Date }
        "error"      { "error: $($m.code)" }
        "state" {
            $y = $m.you; $t = $m.them
            "state phase=$($m.phase) you=$($y.score)$(if($y.holding){'H'}) them=$($t.score)$(if($t.holding){'H'}) hand=$($y.hand.Count) themHand=$($t.handCount) board=$($y.board.Count)/$($t.board.Count) msLeft=$($m.turnMsLeft) event=$($m.event.effect)"
            if ($m.phase -eq "playing" -and $y.canAct) {
                $key = "$($y.board.Count)-$($y.score)"
                if ($key -eq $lastActedTurn) { break }
                $lastActedTurn = $key
                Start-Sleep -Milliseconds 1500
                $help = $y.hand | Where-Object { -not $_.effect -and ($y.score + $_.value) -ge 18 -and ($y.score + $_.value) -le 20 } | Select-Object -First 1
                if ($help) { Send @{ type = "play"; cardId = $help.id; value = $help.value }; "  played $($help.value)" }
                elseif ($y.score -ge 17 -and $y.score -le 20) { Send @{ type = "hold" }; "  hold" }
                else { Send @{ type = "draw" }; "  draw" }
            }
        }
    }
}
"exit: state=$($ws.State) now=$(Get-Date -Format T) deadline=$($deadline.ToString('T'))"
try { $ws.Abort() } catch {}
Api DELETE "/v1/me" $token | Out-Null
"opponent done"


