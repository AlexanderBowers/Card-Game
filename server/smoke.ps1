# End-to-end check against a running server: two new accounts, names, friends by code, a search,
# the leaderboard, then a real quick match over the WebSocket played until it ends.
#
#   powershell -ExecutionPolicy Bypass -File server/smoke.ps1                       (local server)
#   powershell -ExecutionPolicy Bypass -File server/smoke.ps1 -Base https://play.example.com
#
# It creates two throwaway accounts and deletes them again at the end.
param([string]$Base = "http://127.0.0.1:5080")
$ErrorActionPreference = "Stop"

function Api($method, $path, $token, $body) {
    $headers = @{}
    if ($token) { $headers["Authorization"] = "Bearer $token" }
    $req = @{ Method = $method; Uri = "$Base$path"; Headers = $headers; ContentType = "application/json" }
    if ($body) { $req["Body"] = ($body | ConvertTo-Json -Compress) }
    Invoke-RestMethod @req
}

function NewPlayer($name) {
    $acct = Api POST "/v1/accounts"
    $sess = Api POST "/v1/sessions" $null @{ accountId = $acct.accountId; secret = $acct.secret }
    $prof = Api PUT "/v1/me/name" $sess.token @{ name = $name }
    [pscustomobject]@{ Id = $acct.accountId; Token = $sess.token; Profile = $prof }
}

$a = NewPlayer "Smoke Ann"
$b = NewPlayer "Smoke Ben"
"A: $($a.Profile.display) code $($a.Profile.friendCode)"
"B: $($b.Profile.display)"

try { Api PUT "/v1/me/name" $a.Token @{ name = "sh1t" } | Out-Null; throw "a rude name was accepted" }
catch { if ($_.Exception.Response.StatusCode.value__ -ne 400) { throw } ; "Rude name refused: OK" }

(Api POST "/v1/friends/code" $b.Token @{ code = $a.Profile.friendCode }).result
(Api POST "/v1/friends/$($b.Id)/accept" $a.Token).result
"A's friends: " + ((Api GET "/v1/friends" $a.Token).friends | ForEach-Object { $_.name })
"Search 'smoke b' from A: " + ((Api GET "/v1/players/search?name=smoke%20b" $a.Token) | ForEach-Object { $_.name })
Api POST "/v1/leaderboard/endless" $a.Token @{ streak = 3 } | Out-Null
"Leaderboard you: rank " + (Api GET "/v1/leaderboard/endless" $a.Token).you.rank

# ---- the socket ----
$wsBase = $Base -replace "^http", "ws"
function Connect($token) {
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ws.ConnectAsync([Uri]"$wsBase/v1/play", [Threading.CancellationToken]::None).Wait()
    Send $ws @{ type = "auth"; token = $token; version = 1 }
    $ws
}
function Send($ws, $obj) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json -Compress))
    $ws.SendAsync([ArraySegment[byte]]::new($bytes), "Text", $true, [Threading.CancellationToken]::None).Wait()
}
$script:pending = @{}
function Receive($ws, $ms = 3000) {
    if (-not $script:pending[$ws]) {
        $buf = New-Object byte[] 65536
        $script:pending[$ws] = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), [Threading.CancellationToken]::None)
        $script:bufs = $script:bufs; if (-not $script:bufs) { $script:bufs = @{} }; $script:bufs[$ws] = $buf
    }
    $t = $script:pending[$ws]
    if (-not $t.Wait($ms)) { return $null }
    $script:pending[$ws] = $null
    $r = $t.Result
    [Text.Encoding]::UTF8.GetString($script:bufs[$ws], 0, $r.Count) | ConvertFrom-Json
}

$wa = Connect $a.Token
$wb = Connect $b.Token
(Receive $wa).type; (Receive $wb).type
# A brings a deck of its own; a deck naming a card that does not exist must be refused.
Send $wa @{ type = "queue"; deck = @("+5","+5","+5","+5","-5","-5","-5","-5","flip2","flip2","flip2","SetToTarget") }
$refused = Receive $wa
if ($refused.type -ne "error" -or $refused.code -ne "bad_deck") { throw "a made-up card was accepted" }
"Made-up card refused: OK"
Send $wa @{ type = "queue"; deck = @("+5","+5","+5","+5","-5","-5","-5","-5","flip2","flip2","flip2","flip2") }
Send $wb @{ type = "queue" }

$states = @{ a = $null; b = $null }
$over = $false; $sets = 0; $steps = 0
while (-not $over -and $steps -lt 2000) {
    $steps++
    foreach ($pair in @(@("a", $wa), @("b", $wb))) {
        $who = $pair[0]; $ws = $pair[1]
        $m = Receive $ws 200
        if ($null -eq $m) { continue }
        switch ($m.type) {
            "state"    {
                if ($who -eq "a" -and -not $states["a"]) {
                    $vals = $m.you.hand | ForEach-Object { [Math]::Abs($_.value) }
                    if ($vals | Where-Object { $_ -ne 5 -and $_ -ne 2 }) { throw "A was dealt a card not in its deck: $vals" }
                    "A's hand came from its deck: $($m.you.hand.text -join ' ')"
                }
                $states[$who] = $m
            }
            "setEnd"   { if ($who -eq "a") { $sets++; "set $sets -> $($m.winner) ($($m.yourScore) v $($m.theirScore))" } }
            "matchEnd" { "match over for ${who}: $($m.winner) ($($m.reason))"; $over = $true }
            "error"    { "error for ${who}: $($m.code)" }
        }
        $s = $states[$who]
        if ($s -and $s.phase -eq "playing" -and $s.you.canAct) {
            $move = if ($s.you.score -ge 17) { "hold" } else { "draw" }
            Send $ws @{ type = $move }
            $s.you.canAct = $false
        }
    }
}
if (-not $over) { throw "the match never finished" }
"Opponent hand visible to A? " + ($null -ne $states["a"].them.hand)

$wa.Abort(); $wb.Abort()
Api DELETE "/v1/me" $a.Token | Out-Null
Api DELETE "/v1/me" $b.Token | Out-Null
"Accounts deleted. Smoke test passed."
