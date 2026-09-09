#!/usr/bin/env bash
# =========================================================================
# friends.sh — playing UO Offline with friends (Linux / Steam Deck): the
# address to give out and the "how do you want to play" question.
#
# Lives in the install folder (install.sh copies it there). The question
# itself is asked by start.sh every time: play by myself, host for
# friends, or join a friend. This script is for the bits around it.
#
#   ./friends.sh                what this install is set to; when hosting,
#                               the address to give friends
#   ./friends.sh ask            ask the question at every start again
#   ./friends.sh default MODE   stop asking and always use solo, host or join
#
# Hosting binds the server to every network this PC is on. If a firewall
# is on (ufw, firewalld), open TCP 2593 yourself:
#   sudo ufw allow 2593/tcp     or   sudo firewall-cmd --add-port=2593/tcp --permanent
# See docs/FRIENDS.md in the UO Offline download.
# =========================================================================
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLAY_FILE="${ROOT}/play.json"
CFG_FILE="${ROOT}/ModernUO/Distribution/Configuration/modernuo.json"
MODE_FILE="${ROOT}/server-mode.txt"
PORT=2593

json_str() { grep -oE "\"$2\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" "$1" 2>/dev/null | sed -E 's/.*"([^"]*)"$/\1/' || true; }
json_raw() { grep -oE "\"$2\"[[:space:]]*:[[:space:]]*[a-z0-9]+" "$1" 2>/dev/null | sed -E 's/.*:[[:space:]]*//' || true; }

read_play() {
  PLAY_MODE="solo"; PLAY_REMEMBER="false"; PLAY_ADDRESS=""; PLAY_USER=""; PLAY_PORT="${PORT}"
  OWNER_USER="admin"; OWNER_PASS="admin"
  if [[ -f "${PLAY_FILE}" ]]; then
    local v
    v="$(json_str "${PLAY_FILE}" mode)";       [[ -n "$v" ]] && PLAY_MODE="$v"
    v="$(json_raw "${PLAY_FILE}" remember)";   [[ -n "$v" ]] && PLAY_REMEMBER="$v"
    v="$(json_str "${PLAY_FILE}" address)";    [[ -n "$v" ]] && PLAY_ADDRESS="$v"
    v="$(json_str "${PLAY_FILE}" user)";       [[ -n "$v" ]] && PLAY_USER="$v"
    v="$(json_raw "${PLAY_FILE}" port)";       [[ -n "$v" ]] && PLAY_PORT="$v"
    v="$(json_str "${PLAY_FILE}" owner_user)"; [[ -n "$v" ]] && OWNER_USER="$v"
    v="$(json_str "${PLAY_FILE}" owner_pass)"; [[ -n "$v" ]] && OWNER_PASS="$v"
  fi
}

write_play() {
  cat > "${PLAY_FILE}" <<EOF
{
  "mode": "${PLAY_MODE}",
  "remember": ${PLAY_REMEMBER},
  "address": "${PLAY_ADDRESS}",
  "user": "${PLAY_USER}",
  "port": ${PLAY_PORT},
  "owner_user": "${OWNER_USER}",
  "owner_pass": "${OWNER_PASS}"
}
EOF
}

current_listener() {
  [[ -f "${CFG_FILE}" ]] || { echo "(no server here)"; return; }
  grep -oE '"listeners"[[:space:]]*:[[:space:]]*\[[[:space:]]*"[^"]*"' "${CFG_FILE}" | sed -E 's/.*"([^"]*)"$/\1/'
}

server_up() { (exec 3<>"/dev/tcp/127.0.0.1/${PORT}") 2>/dev/null; }

addresses() {
  if command -v ip >/dev/null 2>&1; then
    ip -4 -o addr show scope global 2>/dev/null | awk '{print $4 "  (" $2 ")"}' | sed 's#/[0-9]*##'
  else
    hostname -I 2>/dev/null | tr ' ' '\n' | grep -v '^$'
  fi
}

status() {
  read_play
  echo "UO Offline - playing with friends"
  echo
  if [[ "${PLAY_REMEMBER}" == "true" ]]; then
    echo "start.sh always uses '${PLAY_MODE}' without asking (./friends.sh ask changes that)."
  else
    echo "start.sh asks how you want to play each time. Last choice: ${PLAY_MODE}."
  fi
  [[ "${PLAY_MODE}" == "join" && -n "${PLAY_ADDRESS}" ]] && echo "Joining: ${PLAY_ADDRESS} as ${PLAY_USER}"
  echo
  if server_up; then
    local m="solo"; [[ -f "${MODE_FILE}" ]] && m="$(tr -d '[:space:]' < "${MODE_FILE}")"
    echo "Server running now: yes, started for '${m}'"
  else
    echo "Server running now: no"
  fi
  echo "Server listener:    $(current_listener)"
  echo
  echo "When you host, give friends ONE of these (port ${PORT}):"
  local any=0 line
  while IFS= read -r line; do
    [[ -n "$line" ]] || continue
    any=1
    case "$line" in
      100.*|*tailscale*|*zt*) echo "  From anywhere (Tailscale / ZeroTier):  $line" ;;
      *)                      echo "  Same house / LAN:                      $line" ;;
    esac
  done < <(addresses)
  [[ $any -eq 0 ]] && echo "  (no network address found)"
  echo
  echo "For friends who are not on your LAN, install Tailscale (tailscale.com) on both"
  echo "PCs and use the 100.x.y.z address it gives this one. No router changes needed."
  echo "If a firewall is on here, open TCP ${PORT} (see the top of this script)."
  echo
  echo "Friends pick 'Join a friend' when they start UO Offline and type the address."
}

case "${1:-status}" in
  ask)
    read_play; PLAY_REMEMBER="false"; write_play
    echo "start.sh will ask how you want to play at every start."; echo; status
    ;;
  default)
    m="${2:-}"
    case "$m" in solo|host|join) ;; *) echo "Usage: ./friends.sh default solo|host|join"; exit 1 ;; esac
    read_play; PLAY_MODE="$m"; PLAY_REMEMBER="true"; write_play
    echo "start.sh will always use '${m}' without asking. ./friends.sh ask brings the question back."; echo; status
    ;;
  *)
    status
    ;;
esac
