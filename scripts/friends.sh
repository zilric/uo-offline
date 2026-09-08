#!/usr/bin/env bash
# =========================================================================
# friends.sh — play UO Offline with friends (Linux / Steam Deck).
#
# Lives in the install folder (install.sh copies it there).
#
#   ./friends.sh                    what this install is set to, and the
#                                   address to give friends when hosting
#   ./friends.sh host               let friends connect to this PC
#   ./friends.sh solo               back to this PC only
#   ./friends.sh join ADDRESS [NAME] [PASSWORD]
#                                   connect to a friend's PC instead of
#                                   running a server here
#
# Hosting binds the server to every network this PC is on. Friends on the
# same LAN use the LAN address; friends elsewhere use Tailscale (or
# ZeroTier). If a firewall is on (ufw, firewalld), open TCP 2593 yourself:
#   sudo ufw allow 2593/tcp        or      sudo firewall-cmd --add-port=2593/tcp --permanent
# See docs/FRIENDS.md in the UO Offline download.
#
# Changes to the server (host/solo) take effect the next time it starts.
# =========================================================================
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLAY_FILE="${ROOT}/play.json"
CFG_FILE="${ROOT}/ModernUO/Distribution/Configuration/modernuo.json"
PORT=2593

read_play() {
  PLAY_MODE="solo"; PLAY_ADDRESS="127.0.0.1"; PLAY_PORT="${PORT}"
  if [[ -f "${PLAY_FILE}" ]]; then
    local m a p
    m="$(grep -oE '"mode"[[:space:]]*:[[:space:]]*"[^"]*"' "${PLAY_FILE}" | sed -E 's/.*"([^"]*)"$/\1/' || true)"
    a="$(grep -oE '"address"[[:space:]]*:[[:space:]]*"[^"]*"' "${PLAY_FILE}" | sed -E 's/.*"([^"]*)"$/\1/' || true)"
    p="$(grep -oE '"port"[[:space:]]*:[[:space:]]*[0-9]+' "${PLAY_FILE}" | grep -oE '[0-9]+$' || true)"
    [[ -n "$m" ]] && PLAY_MODE="$m"
    [[ -n "$a" ]] && PLAY_ADDRESS="$a"
    [[ -n "$p" ]] && PLAY_PORT="$p"
  fi
}

write_play() {
  cat > "${PLAY_FILE}" <<EOF
{
  "mode": "${PLAY_MODE}",
  "address": "${PLAY_ADDRESS}",
  "port": ${PLAY_PORT}
}
EOF
}

set_listener() {
  [[ -f "${CFG_FILE}" ]] || return 1
  sed -i -E "s/\"listeners\"[[:space:]]*:[[:space:]]*\[[^]]*\]/\"listeners\": [\"$1\"]/" "${CFG_FILE}"
  sed -i -E 's/"serverListing\.autoDetect"[[:space:]]*:[[:space:]]*"[^"]*"/"serverListing.autoDetect": "false"/' "${CFG_FILE}"
  return 0
}

current_listener() {
  [[ -f "${CFG_FILE}" ]] || { echo "(no server here)"; return; }
  grep -oE '"listeners"[[:space:]]*:[[:space:]]*\[[[:space:]]*"[^"]*"' "${CFG_FILE}" | sed -E 's/.*"([^"]*)"$/\1/'
}

client_settings_files() {
  local f
  for f in "${ROOT}/ClassicUO/settings.json" "${ROOT}"/ClassicUO/*/settings.json; do
    [[ -f "$f" ]] && echo "$f"
  done
}

set_client() {
  local ip="$1" user="${2:-}" pass="${3:-}" n=0 f
  while IFS= read -r f; do
    [[ -n "$f" ]] || continue
    sed -i -E "s/\"ip\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"ip\": \"${ip}\"/" "$f"
    [[ -n "$user" ]] && sed -i -E "s/\"username\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"username\": \"${user}\"/" "$f"
    [[ -n "$pass" ]] && sed -i -E "s/\"password\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"password\": \"${pass}\"/" "$f"
    n=$((n + 1))
  done < <(client_settings_files)
  echo "$n"
}

server_up() {
  (exec 3<>"/dev/tcp/127.0.0.1/${PORT}") 2>/dev/null
}

addresses() {
  # ip(8) everywhere modern; hostname -I as a fallback.
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
  case "${PLAY_MODE}" in
    join)
      echo "This install JOINS a friend's game at ${PLAY_ADDRESS}:${PLAY_PORT}."
      echo "No server runs here. start.sh connects there if their game is up."
      echo
      echo "To change the address or your login:  ./friends.sh join ADDRESS NAME PASSWORD"
      echo "To run your own world instead:         ./friends.sh solo"
      ;;
    host)
      echo "This PC HOSTS. The server listens on $(current_listener) and friends can connect."
      if server_up; then echo "Server running now: yes"; else echo "Server running now: no - run start.sh"; fi
      echo
      echo "Give friends ONE of these addresses (port ${PORT}):"
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
      echo "Your friends install UO Offline with --join ADDRESS, or run ./friends.sh join ADDRESS."
      echo "To stop hosting: ./friends.sh solo"
      ;;
    *)
      echo "This install plays by itself: the server is on this PC only (127.0.0.1)."
      echo
      echo "To let friends in:   ./friends.sh host"
      echo "To join a friend:    ./friends.sh join ADDRESS NAME PASSWORD"
      ;;
  esac
}

case "${1:-status}" in
  host)
    read_play; PLAY_MODE="host"; PLAY_ADDRESS="127.0.0.1"; write_play
    if set_listener "0.0.0.0:${PORT}"; then
      echo "Hosting is ON. The server will listen for friends the next time it starts."
      server_up && echo "The server is running right now on the old setting; run stop.sh, then start.sh."
    else
      echo "No server was found in this install (a join-only install?). Re-run install.sh without --join to build one."
    fi
    set_client "127.0.0.1" >/dev/null
    echo; status
    ;;
  solo)
    read_play; PLAY_MODE="solo"; PLAY_ADDRESS="127.0.0.1"; write_play
    set_listener "127.0.0.1:${PORT}" >/dev/null 2>&1 || true
    set_client "127.0.0.1" >/dev/null
    echo "Back to playing by yourself. The server listens on this PC only from its next start."
    echo; status
    ;;
  join)
    addr="${2:-}"; user="${3:-}"; pass="${4:-}"
    if [[ -z "$addr" ]]; then echo "Usage: ./friends.sh join ADDRESS [NAME] [PASSWORD]"; exit 1; fi
    read_play; PLAY_MODE="join"; PLAY_ADDRESS="$addr"; PLAY_PORT="${PORT}"; write_play
    n="$(set_client "$addr" "$user" "$pass")"
    echo "Set to join ${addr} as ${user:-the saved login}. ${n} client settings file(s) updated."
    [[ "$n" == "0" ]] && echo "No ClassicUO settings.json was found; run install.sh once so the client exists."
    echo; status
    ;;
  status|*)
    status
    ;;
esac
