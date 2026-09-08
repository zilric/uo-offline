# =========================================================================
# start.ps1 — UO Offline launcher (Windows).
#
# The installer copies this into the install folder and fills in the
# __PLACEHOLDERS__. The desktop shortcut runs it minimized, so anything the
# player needs to read goes in a window, not the console.
#
# Every start asks how you want to play:
#
#   Play by myself   - server and game on this PC, nobody else can connect.
#   Host for friends - the same, but the server listens on the network so
#                      friends can join (LAN, or Tailscale from anywhere).
#                      Windows asks once to open port 2593 in its firewall.
#   Join a friend    - no server here. The game connects to a friend's PC
#                      with the account name you give.
#
# Tick "don't ask again" to skip the question; friends.bat ask brings it
# back. Scripted use: start.ps1 -Mode host|solo|join.
#
# Switching a running server between solo and hosting needs a restart,
# because the listener is read at start. The launcher asks the server to
# save and stop (shutdown_request.txt, handled by the PlayerBots build),
# waits for it, and starts it again. Nothing is lost.
# =========================================================================
param(
  [ValidateSet("", "solo", "host", "join")][string]$Mode = "",
  [switch]$NoLaunch,
  [switch]$NoFirewall,
  # Tests and scripts: no message boxes (questions answer yes), and never
  # start the server.
  [switch]$Quiet,
  [switch]$NoServer
)
$ErrorActionPreference = "Continue"

$root      = "__ROOT__"
$dist      = "__DIST__"
$dotnet    = "__DOTNET__"
$cuo       = "__CUO__"
$cfg       = Join-Path $dist "Configuration\modernuo.json"
$liveDir   = Join-Path $dist "Data\Live"
$serverLog = Join-Path $root "server.log"
$playFile  = Join-Path $root "play.json"
$modeFile  = Join-Path $root "server-mode.txt"
$port      = 2593
$ruleName  = "UO Offline (friends)"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Box([string]$text, [string]$title = "UO Offline") {
  if ($Quiet) { Write-Host "[$title] $text"; return }
  [System.Windows.Forms.MessageBox]::Show($text, $title) | Out-Null
}

function Ask([string]$text, [string]$title = "UO Offline") {
  if ($Quiet) { Write-Host "[$title] $text -> yes"; return $true }
  return [System.Windows.Forms.MessageBox]::Show($text, $title, "YesNo", "Question") -eq "Yes"
}

# ---------------------------------------------------------------------------
# Ask GitHub whether there is a newer UO Offline before starting anything.
# Any failure at all falls straight through to launching the game.
# ---------------------------------------------------------------------------
$verdict = "continue"
$updater = Join-Path $root "update-check.ps1"
if (Test-Path $updater) {
  try { $verdict = (& $updater | Select-Object -Last 1) } catch { $verdict = "continue" }
}
if ($verdict -eq "updating") { return }

# ---------------------------------------------------------------------------
# play.json — the last choice, whether to keep asking, the friend's address
# and the account name used to join, and the owner login for this PC.
# ---------------------------------------------------------------------------
function ReadPlay {
  $play = @{ mode = "solo"; remember = $false; address = ""; user = ""; port = $port; owner_user = "admin"; owner_pass = "admin" }
  if (Test-Path $playFile) {
    try {
      $j = Get-Content $playFile -Raw | ConvertFrom-Json
      foreach ($k in @("mode", "address", "user", "owner_user", "owner_pass")) {
        if ($null -ne $j.$k) { $play[$k] = [string]$j.$k }
      }
      if ($null -ne $j.remember) { $play.remember = [bool]$j.remember }
      if ($j.port) { $play.port = [int]$j.port }
    } catch { }
  }
  return $play
}

function WritePlay($play) {
  $rem = if ($play.remember) { "true" } else { "false" }
  @"
{
  "mode": "$($play.mode)",
  "remember": $rem,
  "address": "$($play.address)",
  "user": "$($play.user)",
  "port": $($play.port),
  "owner_user": "$($play.owner_user)",
  "owner_pass": "$($play.owner_pass)"
}
"@ | Set-Content $playFile
}

# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------
function PortOpen([string]$address = "127.0.0.1", [int]$p = 2593, [int]$timeoutMs = 1500) {
  try {
    $c = New-Object System.Net.Sockets.TcpClient
    $ar = $c.BeginConnect($address, $p, $null, $null)
    if (-not $ar.AsyncWaitHandle.WaitOne($timeoutMs)) { $c.Close(); return $false }
    $c.EndConnect($ar); $c.Close(); return $true
  } catch { return $false }
}

function CurrentListener {
  if (-not (Test-Path $cfg)) { return "" }
  $m = [regex]::Match((Get-Content $cfg -Raw), '"listeners"\s*:\s*\[\s*"([^"]*)"')
  if ($m.Success) { return $m.Groups[1].Value } else { return "" }
}

# What the server binds. Hosting opens every network this PC is on; solo is
# this PC only. Read by the server at start, so a running one is unaffected.
function SetListener([string]$addr) {
  if (-not (Test-Path $cfg)) { return }
  $text = Get-Content $cfg -Raw
  $new = [regex]::Replace($text, '"listeners"\s*:\s*\[[^\]]*\]', "`"listeners`": [`"$addr`"]")
  $new = [regex]::Replace($new, '"serverListing\.autoDetect"\s*:\s*"[^"]*"', '"serverListing.autoDetect": "false"')
  if ($new -ne $text) { Set-Content $cfg $new }
}

function ClientSettingsFiles {
  $files = @()
  $d = Join-Path $root "ClassicUO"
  if (Test-Path (Join-Path $d "settings.json")) { $files += (Join-Path $d "settings.json") }
  if ($cuo) {
    $f = Join-Path (Split-Path -Parent $cuo) "settings.json"
    if ((Test-Path $f) -and ($files -notcontains $f)) { $files += $f }
  }
  return $files
}

# Where the game connects and as whom.
function SetClient([string]$ip, [string]$user, [string]$pass) {
  foreach ($f in (ClientSettingsFiles)) {
    $text = Get-Content $f -Raw
    $text = [regex]::Replace($text, '"ip"\s*:\s*"[^"]*"', "`"ip`": `"$ip`"")
    if ($user) { $text = [regex]::Replace($text, '"username"\s*:\s*"[^"]*"', "`"username`": `"$user`"") }
    if ($null -ne $pass) { $text = [regex]::Replace($text, '"password"\s*:\s*"[^"]*"', "`"password`": `"$pass`"") }
    Set-Content $f $text
  }
}

function LanAddresses {
  try {
    return @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
      Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" -and $_.IPAddress -notlike "100.*" } |
      ForEach-Object { "$($_.IPAddress)  ($($_.InterfaceAlias))" })
  } catch {
    return @(ipconfig | Select-String "IPv4" | ForEach-Object { ($_ -split ":\s*")[-1].Trim() } |
      Where-Object { $_ -notlike "127.*" -and $_ -notlike "169.254.*" -and $_ -notlike "100.*" })
  }
}

# Tailscale hands out 100.64.0.0/10; ZeroTier shows under its own alias.
function OverlayAddresses {
  try {
    return @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
      Where-Object { $_.IPAddress -like "100.*" -or $_.InterfaceAlias -like "Tailscale*" -or $_.InterfaceAlias -like "ZeroTier*" } |
      ForEach-Object { "$($_.IPAddress)  ($($_.InterfaceAlias))" })
  } catch { return @() }
}

function AddressText {
  $lines = @("Friends connect to ONE of these (port $port):")
  $ov = OverlayAddresses; $lan = LanAddresses
  if ($ov.Count -gt 0)  { $lines += ""; $lines += "From anywhere (Tailscale / ZeroTier):"; foreach ($a in $ov) { $lines += "    $a" } }
  if ($lan.Count -gt 0) { $lines += ""; $lines += "Same house / LAN:"; foreach ($a in $lan) { $lines += "    $a" } }
  if ($ov.Count -eq 0) {
    $lines += ""
    $lines += "For friends who are not on your LAN, put Tailscale (tailscale.com) on both PCs;"
    $lines += "it gives this PC a 100.x.y.z address that works from anywhere, no router changes."
  }
  $lines += ""
  $lines += "They pick 'Join a friend' when they start UO Offline and type the address."
  $lines += "friends.bat in the install folder shows this again any time."
  return ($lines -join [Environment]::NewLine)
}

function RuleExists {
  try {
    $out = & netsh advfirewall firewall show rule name="$ruleName" 2>&1
    return ($LASTEXITCODE -eq 0 -and ($out -join "`n") -match "Rule Name")
  } catch { return $false }
}

# Needs admin: one UAC prompt, the first time hosting is chosen.
function EnsureFirewallRule {
  if ($NoFirewall -or (RuleExists)) { return $true }
  $args = "advfirewall firewall add rule name=`"$ruleName`" dir=in action=allow protocol=TCP localport=$port"
  try {
    $p = Start-Process -FilePath "netsh" -ArgumentList $args -Verb RunAs -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -eq 0 -or (RuleExists)) { return $true }
  } catch { }
  return $false
}

# Ask the running server to save and stop, then wait for the port to close.
function StopServer {
  try {
    New-Item -ItemType Directory -Force -Path $liveDir | Out-Null
    Set-Content (Join-Path $liveDir "shutdown_request.txt") ([string][DateTimeOffset]::UtcNow.ToUnixTimeSeconds())
  } catch { return $false }
  for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 1
    if (-not (PortOpen)) { Start-Sleep -Seconds 2; return $true }
  }
  return $false
}

# ---------------------------------------------------------------------------
# The question.
# ---------------------------------------------------------------------------
function AskMode($play) {
  $form = New-Object System.Windows.Forms.Form
  $form.Text = "UO Offline"
  $form.ClientSize = New-Object System.Drawing.Size(430, 250)
  $form.StartPosition = "CenterScreen"
  $form.FormBorderStyle = "FixedDialog"
  $form.MaximizeBox = $false; $form.MinimizeBox = $false
  $form.TopMost = $true
  $ico = Join-Path $root "uoico.ico"
  if (Test-Path $ico) { try { $form.Icon = New-Object System.Drawing.Icon($ico) } catch { } }

  $lbl = New-Object System.Windows.Forms.Label
  $lbl.Text = "How do you want to play?"
  $lbl.Font = New-Object System.Drawing.Font("Segoe UI", 12, [System.Drawing.FontStyle]::Bold)
  $lbl.Location = New-Object System.Drawing.Point(20, 16); $lbl.Size = New-Object System.Drawing.Size(390, 28)
  $form.Controls.Add($lbl)

  $script:choice = ""
  $y = 54
  foreach ($opt in @(
      @{ mode = "solo"; text = "Play by myself"; hint = "This PC only. Nobody else can connect." },
      @{ mode = "host"; text = "Host for friends"; hint = "Friends can join your world while you play." },
      @{ mode = "join"; text = "Join a friend"; hint = "Connect to a friend's world. No server here." })) {
    $b = New-Object System.Windows.Forms.Button
    $b.Text = $opt.text
    $b.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
    $b.Location = New-Object System.Drawing.Point(20, $y); $b.Size = New-Object System.Drawing.Size(150, 38)
    $b.Tag = $opt.mode
    $b.Add_Click({ param($s, $e) $script:choice = [string]$s.Tag; $form.Close() })
    $form.Controls.Add($b)
    $h = New-Object System.Windows.Forms.Label
    $h.Text = $opt.hint
    $h.Font = New-Object System.Drawing.Font("Segoe UI", 9)
    $h.Location = New-Object System.Drawing.Point(180, ($y + 9)); $h.Size = New-Object System.Drawing.Size(240, 24)
    $form.Controls.Add($h)
    $y += 46
  }

  $chk = New-Object System.Windows.Forms.CheckBox
  $chk.Text = "Don't ask again (friends.bat ask brings this back)"
  $chk.Font = New-Object System.Drawing.Font("Segoe UI", 9)
  $chk.Location = New-Object System.Drawing.Point(22, ($y + 6)); $chk.Size = New-Object System.Drawing.Size(390, 24)
  $form.Controls.Add($chk)

  [void]$form.ShowDialog()
  $play.remember = [bool]$chk.Checked
  return $script:choice
}

function AskJoin($play) {
  $form = New-Object System.Windows.Forms.Form
  $form.Text = "UO Offline - join a friend"
  $form.ClientSize = New-Object System.Drawing.Size(430, 210)
  $form.StartPosition = "CenterScreen"
  $form.FormBorderStyle = "FixedDialog"
  $form.MaximizeBox = $false; $form.MinimizeBox = $false
  $form.TopMost = $true

  $rows = @(
    @{ label = "Friend's address:"; name = "address"; value = $play.address; hint = "what friends.bat shows on their PC" },
    @{ label = "Your name:";        name = "user";    value = $(if ($play.user) { $play.user } else { $env:USERNAME }); hint = "created on their world at first login" },
    @{ label = "Password:";         name = "pass";    value = ""; hint = "" })
  $boxes = @{}
  $y = 18
  foreach ($r in $rows) {
    $l = New-Object System.Windows.Forms.Label
    $l.Text = $r.label; $l.Location = New-Object System.Drawing.Point(16, ($y + 4)); $l.Size = New-Object System.Drawing.Size(120, 22)
    $form.Controls.Add($l)
    $t = New-Object System.Windows.Forms.TextBox
    $t.Text = $r.value; $t.Location = New-Object System.Drawing.Point(140, $y); $t.Size = New-Object System.Drawing.Size(270, 24)
    if ($r.name -eq "pass") { $t.UseSystemPasswordChar = $true }
    $form.Controls.Add($t)
    $boxes[$r.name] = $t
    $y += 30
    if ($r.hint) {
      $h = New-Object System.Windows.Forms.Label
      $h.Text = $r.hint; $h.ForeColor = [System.Drawing.Color]::Gray
      $h.Location = New-Object System.Drawing.Point(140, ($y - 6)); $h.Size = New-Object System.Drawing.Size(280, 18)
      $form.Controls.Add($h)
      $y += 14
    }
  }

  $ok = New-Object System.Windows.Forms.Button
  $ok.Text = "Connect"; $ok.Location = New-Object System.Drawing.Point(300, 170); $ok.Size = New-Object System.Drawing.Size(110, 30)
  $ok.DialogResult = "OK"
  $form.Controls.Add($ok); $form.AcceptButton = $ok
  $cancel = New-Object System.Windows.Forms.Button
  $cancel.Text = "Cancel"; $cancel.Location = New-Object System.Drawing.Point(180, 170); $cancel.Size = New-Object System.Drawing.Size(110, 30)
  $cancel.DialogResult = "Cancel"
  $form.Controls.Add($cancel); $form.CancelButton = $cancel

  if ($form.ShowDialog() -ne "OK") { return $null }
  $a = $boxes.address.Text.Trim(); $u = $boxes.user.Text.Trim(); $p = $boxes.pass.Text
  if (-not $a -or -not $u -or -not $p) {
    Box "All three are needed: the address, a name, and a password."
    return $null
  }
  return @{ address = $a; user = $u; pass = $p }
}

# ---------------------------------------------------------------------------
# Starting the server and the game.
# ---------------------------------------------------------------------------
function StartServer([string]$forMode) {
  if ($NoServer) { Write-Host "(not starting the server: -NoServer) would start for '$forMode'"; Set-Content $modeFile $forMode; return $true }

  # First launch has no accounts yet, and ModernUO asks on the console whether
  # to create the owner account. That question cannot be answered through a
  # redirected stdin, so the first time the server gets a real window.
  $firstRun = -not (Test-Path (Join-Path $dist "Saves\Accounts\Accounts.bin"))

  if ($firstRun) {
    Box ("First launch. A server window is about to open and ask you two things:" + [Environment]::NewLine + [Environment]::NewLine +
      "  1. Create the owner account now?  Answer  y" + [Environment]::NewLine +
      "  2. A username and password.  admin / admin is fine - it is your own machine." + [Environment]::NewLine + [Environment]::NewLine +
      "Then it builds the world and bakes the pathfinding cache the bots use." + [Environment]::NewLine +
      "That part is a one-off and takes a few minutes. Later starts are quick." + [Environment]::NewLine + [Environment]::NewLine +
      "The game starts by itself once the server has finished loading.") "UO Offline - first launch"
    Start-Process -FilePath $dotnet -ArgumentList "ModernUO.dll" -WorkingDirectory $dist | Out-Null
  } else {
    # Output goes to a log, not a console window. Handed a raw console the
    # server stalls before it binds the port; redirected, it is up in seconds.
    Start-Process -FilePath $dotnet -ArgumentList "ModernUO.dll" -WorkingDirectory $dist `
      -WindowStyle Minimized -RedirectStandardOutput $serverLog | Out-Null
  }
  Set-Content $modeFile $forMode

  $limit = if ($firstRun) { 1200 } else { 180 }
  for ($i = 0; $i -lt $limit; $i++) {
    if (PortOpen) { return $true }
    Start-Sleep -Seconds 1
  }

  Box ("The server did not start listening within $limit seconds, so the game has not been launched - it would only fail to connect." + [Environment]::NewLine + [Environment]::NewLine +
    "What went wrong should be at the end of:" + [Environment]::NewLine + $serverLog + [Environment]::NewLine + [Environment]::NewLine +
    "If it is still loading on a slow machine, waiting a moment and clicking UO Offline again will connect to it.") "UO Offline - server did not start"
  return $false
}

function LaunchClient {
  if ($NoLaunch) { return }
  if ($cuo -and (Test-Path $cuo)) { Start-Process -FilePath $cuo -WorkingDirectory (Split-Path -Parent $cuo) }
  else { Box "ClassicUO.exe was not found. Re-run the installer." }
}

# What the running server was started as. Unknown means somebody started it
# by hand; treat it as solo, which is the safe assumption.
function RunningMode {
  if (Test-Path $modeFile) { $m = (Get-Content $modeFile -Raw).Trim(); if ($m) { return $m } }
  return "solo"
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
$play = ReadPlay

$choice = $Mode
if (-not $choice) {
  if ($play.remember -and $play.mode) { $choice = $play.mode }
  else { $choice = AskMode $play }
}
if (-not $choice) { return }
$play.mode = $choice

switch ($choice) {

  "join" {
    if ($Mode) {
      if (-not $play.address) { Box "No friend's address saved yet. Start UO Offline and pick Join a friend."; return }
      $j = @{ address = $play.address; user = $play.user; pass = $null }
    } else {
      $j = AskJoin $play
      if (-not $j) { return }
    }
    $play.address = $j.address; $play.user = $j.user
    WritePlay $play
    SetClient $j.address $j.user $j.pass

    if (-not (PortOpen $j.address $play.port 4000)) {
      Box ("Could not reach your friend's game at $($j.address):$($play.port)." + [Environment]::NewLine + [Environment]::NewLine +
        "Check that:" + [Environment]::NewLine +
        "  - their UO Offline is running (the server starts when they click Play)" + [Environment]::NewLine +
        "  - they picked 'Host for friends' (friends.bat on their PC shows the address)" + [Environment]::NewLine +
        "  - the address is right, and if you use Tailscale, that it is on") "UO Offline - friend not reachable"
      return
    }
    LaunchClient
  }

  "host" {
    WritePlay $play
    $ruleOk = EnsureFirewallRule
    SetListener "0.0.0.0:$port"
    SetClient "127.0.0.1" $play.owner_user $play.owner_pass

    if (PortOpen) {
      if ((RunningMode) -ne "host") {
        if (Ask ("The server is already running for solo play, and hosting needs it restarted." + [Environment]::NewLine + [Environment]::NewLine +
                 "Restart it now? The world is saved first; it takes about half a minute." + [Environment]::NewLine + [Environment]::NewLine +
                 "No keeps playing by yourself this time.")) {
          if (-not (StopServer)) {
            Box ("The server did not stop on request. Close its window (or reboot), then click UO Offline again." + [Environment]::NewLine + [Environment]::NewLine +
              "This can happen on an older install; re-running the installer updates it.") "UO Offline"
            return
          }
          if (-not (StartServer "host")) { return }
        }
      }
    } else {
      if (-not (StartServer "host")) { return }
    }

    $msg = AddressText
    if (-not $ruleOk) {
      $msg = "The Windows firewall rule was not added, so friends may not get through. friends.bat firewall tries again." + [Environment]::NewLine + [Environment]::NewLine + $msg
    }
    if (-not $NoLaunch) { Box $msg "UO Offline - hosting" }
    LaunchClient
  }

  default {
    $play.mode = "solo"
    WritePlay $play
    SetListener "127.0.0.1:$port"
    SetClient "127.0.0.1" $play.owner_user $play.owner_pass
    if (-not (PortOpen)) {
      if (-not (StartServer "solo")) { return }
    }
    LaunchClient
  }
}
