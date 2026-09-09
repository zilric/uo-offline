# =========================================================================
# friends.ps1 — playing UO Offline with friends: the address to give out,
# the firewall rule, and the "how do you want to play" question.
#
# Lives in the install folder (the installer copies it there). The question
# itself is asked by the launcher every time you click UO Offline: play by
# myself, host for friends, or join a friend. This script is for the bits
# around it. Double-click friends.bat, or from a console:
#
#   friends.bat                 what this install is set to; when hosting,
#                               the address to give friends
#   friends.bat firewall        add the Windows firewall rule for port 2593
#                               again (one admin prompt)
#   friends.bat ask             ask the question at every start again
#   friends.bat default MODE    stop asking and always use solo, host or join
#
# See docs/FRIENDS.md in the UO Offline download.
# =========================================================================
param(
  [Parameter(Position = 0)][string]$Command = "status",
  [Parameter(Position = 1)][string]$Arg,
  [switch]$Gui,
  [switch]$NoFirewall
)
$ErrorActionPreference = "Stop"
$Root     = Split-Path -Parent $MyInvocation.MyCommand.Definition
$PlayFile = Join-Path $Root "play.json"
$CfgFile  = [IO.Path]::Combine($Root, "ModernUO", "Distribution", "Configuration", "modernuo.json")
$ModeFile = Join-Path $Root "server-mode.txt"
$Port     = 2593
$RuleName = "UO Offline (friends)"

function ReadPlay {
  $play = @{ mode = "solo"; remember = $false; address = ""; user = ""; port = $Port; owner_user = "admin"; owner_pass = "admin" }
  if (Test-Path $PlayFile) {
    try {
      $j = Get-Content $PlayFile -Raw | ConvertFrom-Json
      foreach ($k in @("mode", "address", "user", "owner_user", "owner_pass")) { if ($null -ne $j.$k) { $play[$k] = [string]$j.$k } }
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
"@ | Set-Content $PlayFile
}

function CurrentListener {
  if (-not (Test-Path $CfgFile)) { return "(no server here)" }
  $m = [regex]::Match((Get-Content $CfgFile -Raw), '"listeners"\s*:\s*\[\s*"([^"]*)"')
  if ($m.Success) { return $m.Groups[1].Value } else { return "?" }
}

function ServerUp {
  try { $c = New-Object Net.Sockets.TcpClient; $c.Connect("127.0.0.1", $Port); $c.Close(); return $true } catch { return $false }
}

function RunningMode {
  if (Test-Path $ModeFile) { $m = (Get-Content $ModeFile -Raw).Trim(); if ($m) { return $m } }
  return "solo"
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

function OverlayAddresses {
  try {
    return @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
      Where-Object { $_.IPAddress -like "100.*" -or $_.InterfaceAlias -like "Tailscale*" -or $_.InterfaceAlias -like "ZeroTier*" } |
      ForEach-Object { "$($_.IPAddress)  ($($_.InterfaceAlias))" })
  } catch { return @() }
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

function StatusText {
  $play = ReadPlay
  $lines = @("UO Offline - playing with friends", "")
  $lines += "Clicking UO Offline asks how you want to play each time" +
            $(if ($play.remember) { " - except it is set to always use '$($play.mode)' (friends.bat ask changes that)." } else { "." })
  $lines += "Last choice: $($play.mode)$(if ($play.mode -eq 'join' -and $play.address) { " ($($play.address) as $($play.user))" })"
  $lines += ""
  $up = ServerUp
  $lines += "Server running now: $(if ($up) { "yes, started for '$(RunningMode)'" } else { 'no' })"
  $lines += "Server listener:    $(CurrentListener)"
  $lines += "Firewall rule:      $(if (RuleExists) { 'present' } else { 'missing (added the first time you host, or: friends.bat firewall)' })"
  $lines += ""
  $lines += "When you host, give friends ONE of these (port $Port):"
  $ov = OverlayAddresses; $lan = LanAddresses
  if ($ov.Count -gt 0)  { $lines += "  From anywhere (Tailscale / ZeroTier):"; foreach ($a in $ov) { $lines += "    $a" } }
  if ($lan.Count -gt 0) { $lines += "  Same house / LAN:"; foreach ($a in $lan) { $lines += "    $a" } }
  if ($ov.Count -eq 0) {
    $lines += ""
    $lines += "For friends who are not on your LAN, install Tailscale (tailscale.com) on both PCs"
    $lines += "and give them the 100.x.y.z address it shows here. No router changes needed."
  }
  $lines += ""
  $lines += "Friends pick 'Join a friend' when they start UO Offline and type the address."
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
  "firewall" {
    Show ((AddRule) + [Environment]::NewLine + [Environment]::NewLine + (StatusText))
  }
  "ask" {
    $play = ReadPlay; $play.remember = $false; WritePlay $play
    Show ("UO Offline will ask how you want to play at every start." + [Environment]::NewLine + [Environment]::NewLine + (StatusText))
  }
  "default" {
    $m = "$Arg".ToLowerInvariant()
    if ($m -notin @("solo", "host", "join")) { Show "Usage: friends.bat default solo|host|join"; break }
    $play = ReadPlay; $play.mode = $m; $play.remember = $true; WritePlay $play
    Show ("UO Offline will always use '$m' without asking. friends.bat ask brings the question back." + [Environment]::NewLine + [Environment]::NewLine + (StatusText))
  }
  default {
    Show (StatusText)
  }
}
