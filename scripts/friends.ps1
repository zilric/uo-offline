# =========================================================================
# friends.ps1 — play UO Offline with friends.
#
# Lives in the install folder (the installer copies it there). Double-click
# friends.bat, or run from a console:
#
#   friends.bat                       what this install is set to, and the
#                                     address to give friends when hosting
#   friends.bat host                  let friends connect to this PC
#   friends.bat solo                  back to this PC only
#   friends.bat join ADDRESS [NAME] [PASSWORD]
#                                     connect to a friend's PC instead of
#                                     running a server here
#   friends.bat firewall              add the Windows firewall rule again
#
# Hosting binds the server to every network this PC is on and adds a
# firewall rule for port 2593 (one UAC prompt). Friends on the same LAN use
# the LAN address; friends elsewhere use Tailscale (or ZeroTier), which
# gives every PC a private address that works from anywhere with no router
# changes. See docs/FRIENDS.md in the UO Offline download.
#
# Changes to the server (host/solo) take effect the next time it starts.
# =========================================================================
param(
  [Parameter(Position = 0)][string]$Command = "status",
  [Parameter(Position = 1)][string]$Address,
  [Parameter(Position = 2)][string]$Name,
  [Parameter(Position = 3)][string]$Password,
  [switch]$Gui,
  # Skip the firewall prompt (scripted runs and tests).
  [switch]$NoFirewall
)
$ErrorActionPreference = "Stop"
$Root      = Split-Path -Parent $MyInvocation.MyCommand.Definition
$PlayFile  = Join-Path $Root "play.json"
$CfgFile   = [IO.Path]::Combine($Root, "ModernUO", "Distribution", "Configuration", "modernuo.json")
$Port      = 2593
$RuleName  = "UO Offline (friends)"

# ---- the play file: mode, address, port ----
function ReadPlay {
  $play = @{ mode = "solo"; address = "127.0.0.1"; port = $Port }
  if (Test-Path $PlayFile) {
    try {
      $j = Get-Content $PlayFile -Raw | ConvertFrom-Json
      if ($j.mode)    { $play.mode    = [string]$j.mode }
      if ($j.address) { $play.address = [string]$j.address }
      if ($j.port)    { $play.port    = [int]$j.port }
    } catch { }
  }
  return $play
}

function WritePlay($play) {
  @"
{
  "mode": "$($play.mode)",
  "address": "$($play.address)",
  "port": $($play.port)
}
"@ | Set-Content $PlayFile
}

# ---- the server config: who it listens for ----
function SetListener([string]$addr) {
  if (-not (Test-Path $CfgFile)) { return $false }
  $text = Get-Content $CfgFile -Raw
  $new = [regex]::Replace($text, '"listeners"\s*:\s*\[[^\]]*\]', "`"listeners`": [`"$addr`"]")
  # No outside lookup of a public address: a friend is told to connect to
  # whatever address they reached this PC on, which is right for LAN and
  # Tailscale both.
  $new = [regex]::Replace($new, '"serverListing\.autoDetect"\s*:\s*"[^"]*"', '"serverListing.autoDetect": "false"')
  if ($new -ne $text) { Set-Content $CfgFile $new }
  return $true
}

function CurrentListener {
  if (-not (Test-Path $CfgFile)) { return "(no server here)" }
  $m = [regex]::Match((Get-Content $CfgFile -Raw), '"listeners"\s*:\s*\[\s*"([^"]*)"')
  if ($m.Success) { return $m.Groups[1].Value } else { return "?" }
}

# ---- the client settings: where it connects and as whom ----
function ClientSettingsFiles {
  $files = @()
  $cuo = Join-Path $Root "ClassicUO"
  if (Test-Path (Join-Path $cuo "settings.json")) { $files += (Join-Path $cuo "settings.json") }
  $binPath = Join-Path $Root ".classicuo-bin-path"
  if (Test-Path $binPath) {
    $nested = Split-Path -Parent (Get-Content $binPath)
    $f = Join-Path $nested "settings.json"
    if ((Test-Path $f) -and ($files -notcontains $f)) { $files += $f }
  }
  return $files
}

function SetClient([string]$ip, [string]$user, [string]$pass) {
  $n = 0
  foreach ($f in (ClientSettingsFiles)) {
    $text = Get-Content $f -Raw
    $text = [regex]::Replace($text, '"ip"\s*:\s*"[^"]*"', "`"ip`": `"$ip`"")
    if ($user) { $text = [regex]::Replace($text, '"username"\s*:\s*"[^"]*"', "`"username`": `"$user`"") }
    if ($pass) { $text = [regex]::Replace($text, '"password"\s*:\s*"[^"]*"', "`"password`": `"$pass`"") }
    Set-Content $f $text
    $n++
  }
  return $n
}

# ---- addresses to give out ----
function LanAddresses {
  $out = @()
  try {
    $out = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
      Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" -and $_.IPAddress -notlike "100.*" } |
      ForEach-Object { "$($_.IPAddress)  ($($_.InterfaceAlias))" })
  } catch {
    $out = @(ipconfig | Select-String "IPv4" | ForEach-Object { ($_ -split ":\s*")[-1].Trim() } |
      Where-Object { $_ -notlike "127.*" -and $_ -notlike "169.254.*" -and $_ -notlike "100.*" })
  }
  return $out
}

# Tailscale hands out 100.64.0.0/10; ZeroTier shows up under its own alias.
function OverlayAddresses {
  $out = @()
  try {
    $out = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
      Where-Object { $_.IPAddress -like "100.*" -or $_.InterfaceAlias -like "Tailscale*" -or $_.InterfaceAlias -like "ZeroTier*" } |
      ForEach-Object { "$($_.IPAddress)  ($($_.InterfaceAlias))" })
  } catch { }
  return $out
}

function ServerUp {
  try { $c = New-Object Net.Sockets.TcpClient; $c.Connect("127.0.0.1", $Port); $c.Close(); return $true } catch { return $false }
}

function RuleExists {
  try {
    $out = & netsh advfirewall firewall show rule name="$RuleName" 2>&1
    return ($LASTEXITCODE -eq 0 -and ($out -join "`n") -match "Rule Name")
  } catch { return $false }
}

function AddRule {
  if ($NoFirewall) { return "Firewall rule not touched (-NoFirewall)." }
  if (RuleExists) { return "Firewall rule already present." }
  $args = "advfirewall firewall add rule name=`"$RuleName`" dir=in action=allow protocol=TCP localport=$Port"
  try {
    $p = Start-Process -FilePath "netsh" -ArgumentList $args -Verb RunAs -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -eq 0 -or (RuleExists)) { return "Firewall rule added ($RuleName)." }
    return "netsh returned $($p.ExitCode); the rule may not have been added."
  } catch {
    return "The firewall prompt was declined or failed. Friends may not get through until it is added."
  }
}

# ---- what to tell the user ----
function StatusText {
  $play = ReadPlay
  $lines = @()
  $lines += "UO Offline - playing with friends"
  $lines += ""
  switch ($play.mode) {
    "join" {
      $lines += "This install JOINS a friend's game at $($play.address):$($play.port)."
      $lines += "No server runs here. Clicking Play connects there if their game is up."
      $lines += ""
      $lines += "To change the address or your login:  friends.bat join ADDRESS NAME PASSWORD"
      $lines += "To run your own world instead:         friends.bat solo"
    }
    "host" {
      $lines += "This PC HOSTS. The server listens on $(CurrentListener) and friends can connect."
      $lines += "Server running now: $(if (ServerUp) { 'yes' } else { 'no - click Play to start it' })"
      $lines += "Firewall rule:      $(if (RuleExists) { 'present' } else { 'MISSING - run: friends.bat firewall' })"
      $lines += ""
      $lines += "Give friends ONE of these addresses (port $Port):"
      $lan = LanAddresses; $ov = OverlayAddresses
      if ($ov.Count -gt 0)  { $lines += "  From anywhere (Tailscale / ZeroTier):"; foreach ($a in $ov) { $lines += "    $a" } }
      if ($lan.Count -gt 0) { $lines += "  Same house / LAN:"; foreach ($a in $lan) { $lines += "    $a" } }
      if ($ov.Count -eq 0) {
        $lines += ""
        $lines += "For friends who are not on your LAN, install Tailscale (tailscale.com) on both"
        $lines += "PCs, log in with the same account or invite them, then give them the 100.x.y.z"
        $lines += "address this shows once Tailscale is on. No router changes needed."
      }
      $lines += ""
      $lines += "Your friends install UO Offline, pick 'Join a friend's game', and type the address."
      $lines += "To stop hosting: friends.bat solo"
    }
    default {
      $lines += "This install plays by itself: the server is on this PC only (127.0.0.1)."
      $lines += ""
      $lines += "To let friends in:   friends.bat host"
      $lines += "To join a friend:    friends.bat join ADDRESS NAME PASSWORD"
    }
  }
  return ($lines -join [Environment]::NewLine)
}

function Show([string]$text) {
  if ($Gui) {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show($text, "UO Offline - friends") | Out-Null
  } else {
    Write-Host $text
  }
}

switch ($Command.ToLowerInvariant()) {
  "host" {
    $play = ReadPlay
    $play.mode = "host"; $play.address = "127.0.0.1"
    WritePlay $play
    $hadServer = SetListener "0.0.0.0:2593"
    $n = SetClient "127.0.0.1" $null $null
    $msg = @()
    if (-not $hadServer) { $msg += "No server was found in this install (was it a 'join a friend' install?). Re-run the installer and pick 'Play by myself' or 'Host for friends' to build one." }
    else { $msg += "Hosting is ON. The server will listen for friends the next time it starts." }
    $msg += (AddRule)
    if (ServerUp) { $msg += "The server is running right now on the old setting; stop it (close its window or reboot) and click Play again." }
    $msg += ""
    $msg += (StatusText)
    Show ($msg -join [Environment]::NewLine)
  }
  "solo" {
    $play = ReadPlay
    $play.mode = "solo"; $play.address = "127.0.0.1"
    WritePlay $play
    SetListener "127.0.0.1:2593" | Out-Null
    SetClient "127.0.0.1" $null $null | Out-Null
    Show ("Back to playing by yourself. The server listens on this PC only from its next start." + [Environment]::NewLine + [Environment]::NewLine + (StatusText))
  }
  "join" {
    if (-not $Address) {
      if ($Gui) {
        Add-Type -AssemblyName Microsoft.VisualBasic
        $Address = [Microsoft.VisualBasic.Interaction]::InputBox("Your friend's address (the one friends.bat shows on their PC):", "UO Offline - join a friend", "")
        if ($Address) { $Name = [Microsoft.VisualBasic.Interaction]::InputBox("Your account name on their world (new names are created on first login):", "UO Offline - join a friend", $env:USERNAME) }
        if ($Name)    { $Password = [Microsoft.VisualBasic.Interaction]::InputBox("Your password:", "UO Offline - join a friend", "") }
      }
      if (-not $Address) { Show "Usage: friends.bat join ADDRESS [NAME] [PASSWORD]"; break }
    }
    $Address = $Address.Trim()
    $play = ReadPlay
    $play.mode = "join"; $play.address = $Address; $play.port = $Port
    WritePlay $play
    $n = SetClient $Address $Name $Password
    $msg = @("Set to join $Address as $(if ($Name) { $Name } else { 'the saved login' }). $n client settings file(s) updated.")
    if ($n -eq 0) { $msg += "No ClassicUO settings.json was found; run the installer once so the client exists." }
    $msg += ""
    $msg += (StatusText)
    Show ($msg -join [Environment]::NewLine)
  }
  "firewall" {
    Show ((AddRule) + [Environment]::NewLine + [Environment]::NewLine + (StatusText))
  }
  default {
    Show (StatusText)
  }
}
