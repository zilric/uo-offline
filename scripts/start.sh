#!/usr/bin/env bash
# =========================================================================
# start.sh — Launch UO Offline (ModernUO + ClassicUO).
#
# Behavior:
#   - First run: also creates the owner account by feeding scripted answers
#     to the server over stdin.
#   - Subsequent runs: just launches the server in the background, waits
#     for it to be listening, then launches ClassicUO.
#   - Exiting ClassicUO does NOT stop the server. Use stop.sh for that.
# =========================================================================
set -uo pipefail

# install.sh copies this script INTO the install root, so our own
# directory is the install root - including when the player chose a
# custom location. Hardcoding ~/uo-modernuo broke every such install.
INSTALL_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST_DIR="${INSTALL_ROOT}/ModernUO/Distribution"
PIDFILE="${INSTALL_ROOT}/modernuo.pid"
LOGFILE="${INSTALL_ROOT}/modernuo.log"
MARKER="${INSTALL_ROOT}/.needs-owner-account"

OWNER_USER="admin"
OWNER_PASS="admin"
LISTEN_PORT=2593

# ClassicUO lives inside our install root, alongside the server.
CLASSICUO_DIR="${INSTALL_ROOT}/ClassicUO"

# .NET was installed per-user by install.sh into ~/.dotnet/. Make dotnet
# reachable here so we don't depend on the user's shell rc files having
# been re-sourced since install.
DOTNET_ROOT="${HOME}/.dotnet"
export DOTNET_ROOT
export PATH="${DOTNET_ROOT}:${PATH}"

LAUNCHLOG="${INSTALL_ROOT}/launch.log"
: > "${LAUNCHLOG}" 2>/dev/null || true

log_line() { printf '%s %s\n' "$(date '+%H:%M:%S')" "$*" >> "${LAUNCHLOG}" 2>/dev/null || true; }

# A launch failure the player can actually read. The desktop icon runs with
# Terminal=false, so without this a failed start is indistinguishable from
# the icon doing nothing at all.
gui_error() {
  local msg="$1"
  local full="${msg}

Full details: ${LAUNCHLOG}"
  if [[ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]]; then
    if command -v zenity >/dev/null 2>&1; then
      zenity --error --title="UO Offline" --no-wrap --text="${full}" >/dev/null 2>&1 &
    elif command -v kdialog >/dev/null 2>&1; then
      kdialog --title "UO Offline" --error "${full}" >/dev/null 2>&1 &
    fi
  fi
}

say()  { printf '\033[0;36m--> %s\033[0m\n' "$*"; log_line "--> $*"; }
warn() { printf '\033[0;33m[WARN]\033[0m %s\n' "$*" >&2; log_line "[WARN] $*"; }
die()  { printf '\033[0;31m[ERROR]\033[0m %s\n' "$*" >&2; log_line "[ERROR] $*"; gui_error "$*"; exit 1; }

if [[ "${UO_MODE:-}" != "join" ]] && [[ ! -f "${DIST_DIR}/ModernUO.dll" ]] && [[ ! -f "${INSTALL_ROOT}/play.json" ]]; then
  die "ModernUO not built. Run install.sh first."
fi

# ---------------------------------------------------------------------------
# Ask GitHub whether there is a newer UO Offline before starting anything.
#
# The checker stays silent unless there is genuinely something new, and any
# failure at all - no internet, GitHub down, rate limited - falls straight
# through to launching the game. Exit code 10 means the player chose to
# update and the installer is now running, so we get out of the way.
# ---------------------------------------------------------------------------
UPDATER="${INSTALL_ROOT}/update-check.sh"
if [[ -x "${UPDATER}" ]]; then
  "${UPDATER}"
  if [[ $? -eq 10 ]]; then
    exit 0
  fi
fi

# ---------------------------------------------------------------------------
# How do you want to play? Asked every start (unless play.json says to
# remember): by myself, host for friends, or join a friend. See
# docs/FRIENDS.md. Scripted: UO_MODE=solo|host|join ./start.sh
# ---------------------------------------------------------------------------
PLAY_FILE="${INSTALL_ROOT}/play.json"
CFG_FILE="${DIST_DIR}/Configuration/modernuo.json"
MODE_FILE="${INSTALL_ROOT}/server-mode.txt"
LIVE_DIR="${DIST_DIR}/Data/Live"

json_str() { grep -oE "\"$2\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" "$1" 2>/dev/null | sed -E 's/.*"([^"]*)"$/\1/' || true; }
json_raw() { grep -oE "\"$2\"[[:space:]]*:[[:space:]]*[a-z0-9]+" "$1" 2>/dev/null | sed -E 's/.*:[[:space:]]*//' || true; }

PLAY_MODE="solo"; PLAY_REMEMBER="false"; PLAY_ADDRESS=""; PLAY_USER=""; PLAY_PORT="${LISTEN_PORT}"
if [[ -f "${PLAY_FILE}" ]]; then
  _v="$(json_str "${PLAY_FILE}" mode)";       [[ -n "${_v}" ]] && PLAY_MODE="${_v}"
  _v="$(json_raw "${PLAY_FILE}" remember)";   [[ -n "${_v}" ]] && PLAY_REMEMBER="${_v}"
  _v="$(json_str "${PLAY_FILE}" address)";    [[ -n "${_v}" ]] && PLAY_ADDRESS="${_v}"
  _v="$(json_str "${PLAY_FILE}" user)";       [[ -n "${_v}" ]] && PLAY_USER="${_v}"
  _v="$(json_raw "${PLAY_FILE}" port)";       [[ -n "${_v}" ]] && PLAY_PORT="${_v}"
  _v="$(json_str "${PLAY_FILE}" owner_user)"; [[ -n "${_v}" ]] && OWNER_USER="${_v}"
  _v="$(json_str "${PLAY_FILE}" owner_pass)"; [[ -n "${_v}" ]] && OWNER_PASS="${_v}"
  unset _v
fi

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

have_gui() { [[ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]]; }

ask_mode() {
  local pick=""
  if have_gui && command -v zenity >/dev/null 2>&1; then
    pick="$(zenity --list --radiolist --title="UO Offline" --text="How do you want to play?" \
      --column="" --column="Choice" --column="What it means" \
      TRUE "Play by myself" "This PC only. Nobody else can connect." \
      FALSE "Host for friends" "Friends can join your world while you play." \
      FALSE "Join a friend" "Connect to a friend's world. No server here." \
      --width=560 --height=260 2>/dev/null || true)"
  elif have_gui && command -v kdialog >/dev/null 2>&1; then
    pick="$(kdialog --title "UO Offline" --radiolist "How do you want to play?" \
      solo "Play by myself (this PC only)" on \
      host "Host for friends (they can join your world)" off \
      join "Join a friend (connect to their world)" off 2>/dev/null || true)"
  else
    echo "How do you want to play?"
    echo "  1) Play by myself   - this PC only"
    echo "  2) Host for friends - friends can join your world"
    echo "  3) Join a friend    - connect to a friend's world"
    read -r -p "Choice [1]: " pick
  fi
  case "${pick}" in
    ""|1|"Play by myself"|solo) echo "solo" ;;
    2|"Host for friends"|host)   echo "host" ;;
    3|"Join a friend"|join)      echo "join" ;;
    *) echo "" ;;
  esac
}

ask_join() {
  # Sets JOIN_ADDR / JOIN_USER / JOIN_PASS, or returns 1 on cancel.
  local dflt_user="${PLAY_USER:-${USER:-player}}"
  if have_gui && command -v zenity >/dev/null 2>&1; then
    local out
    out="$(zenity --forms --title="UO Offline - join a friend" --text="Your friend's address is what friends.sh shows on their PC. Your name and password make your account on their world." \
      --add-entry="Friend's address" --add-entry="Your name" --add-password="Password" --separator=$'\t' 2>/dev/null || true)"
    [[ -n "${out}" ]] || return 1
    JOIN_ADDR="$(printf '%s' "${out}" | cut -f1)"
    JOIN_USER="$(printf '%s' "${out}" | cut -f2)"
    JOIN_PASS="$(printf '%s' "${out}" | cut -f3)"
    [[ -n "${JOIN_ADDR}" && -n "${JOIN_USER}" && -n "${JOIN_PASS}" ]] || { zenity --error --title="UO Offline" --text="All three are needed: the address, a name, and a password." 2>/dev/null || true; return 1; }
    [[ -z "${JOIN_ADDR}" ]] && JOIN_ADDR="${PLAY_ADDRESS}"
  elif have_gui && command -v kdialog >/dev/null 2>&1; then
    JOIN_ADDR="$(kdialog --title "UO Offline" --inputbox "Your friend's address (friends.sh on their PC shows it):" "${PLAY_ADDRESS}" 2>/dev/null)" || return 1
    JOIN_USER="$(kdialog --title "UO Offline" --inputbox "Your name on their world:" "${dflt_user}" 2>/dev/null)" || return 1
    JOIN_PASS="$(kdialog --title "UO Offline" --password "Your password:" 2>/dev/null)" || return 1
  else
    read -r -p "Friend's address [${PLAY_ADDRESS}]: " JOIN_ADDR; JOIN_ADDR="${JOIN_ADDR:-${PLAY_ADDRESS}}"
    read -r -p "Your name [${dflt_user}]: " JOIN_USER; JOIN_USER="${JOIN_USER:-${dflt_user}}"
    read -r -s -p "Password: " JOIN_PASS; echo
  fi
  [[ -n "${JOIN_ADDR}" && -n "${JOIN_USER}" && -n "${JOIN_PASS}" ]]
}

gui_info() {
  local msg="$1"
  if have_gui && command -v zenity >/dev/null 2>&1; then
    zenity --info --title="UO Offline" --no-wrap --text="${msg}" >/dev/null 2>&1 || true
  elif have_gui && command -v kdialog >/dev/null 2>&1; then
    kdialog --title "UO Offline" --msgbox "${msg}" >/dev/null 2>&1 || true
  fi
  printf '%s\n' "${msg}"
}

gui_yesno() {
  local msg="$1"
  if have_gui && command -v zenity >/dev/null 2>&1; then
    zenity --question --title="UO Offline" --no-wrap --text="${msg}" >/dev/null 2>&1
  elif have_gui && command -v kdialog >/dev/null 2>&1; then
    kdialog --title "UO Offline" --yesno "${msg}" >/dev/null 2>&1
  else
    local a; read -r -p "${msg} [y/N] " a; [[ "${a}" =~ ^[Yy] ]]
  fi
}

set_listener() {
  [[ -f "${CFG_FILE}" ]] || return 0
  sed -i -E "s/\"listeners\"[[:space:]]*:[[:space:]]*\[[^]]*\]/\"listeners\": [\"$1\"]/" "${CFG_FILE}"
  sed -i -E 's/"serverListing\.autoDetect"[[:space:]]*:[[:space:]]*"[^"]*"/"serverListing.autoDetect": "false"/' "${CFG_FILE}"
}

set_client() {
  local ip="$1" user="$2" pass="$3" f
  for f in "${CLASSICUO_DIR}/settings.json" "${CLASSICUO_DIR}"/*/settings.json; do
    [[ -f "$f" ]] || continue
    sed -i -E "s/\"ip\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"ip\": \"${ip}\"/" "$f"
    sed -i -E "s/\"username\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"username\": \"${user}\"/" "$f"
    sed -i -E "s/\"password\"[[:space:]]*:[[:space:]]*\"[^\"]*\"/\"password\": \"${pass}\"/" "$f"
    sed -i -E "s/\"save_password\"[[:space:]]*:[[:space:]]*[a-z]+/\"save_password\": true/" "$f"
  done
}

addresses_text() {
  local line out="Friends connect to ONE of these (port ${LISTEN_PORT}):"
  while IFS= read -r line; do
    [[ -n "$line" ]] || continue
    case "$line" in
      100.*|*tailscale*|*zt*) out+=$'\n'"  From anywhere (Tailscale / ZeroTier):  ${line}" ;;
      *)                      out+=$'\n'"  Same house / LAN:                      ${line}" ;;
    esac
  done < <( if command -v ip >/dev/null 2>&1; then ip -4 -o addr show scope global 2>/dev/null | awk '{print $4 "  (" $2 ")"}' | sed 's#/[0-9]*##'; else hostname -I 2>/dev/null | tr ' ' '\n'; fi )
  out+=$'\n\n'"Friends pick 'Join a friend' when they start UO Offline and type the address."
  out+=$'\n'"If a firewall is on here, open TCP ${LISTEN_PORT} (./friends.sh says how)."
  printf '%s' "${out}"
}

CHOICE="${UO_MODE:-}"
if [[ -z "${CHOICE}" ]]; then
  if [[ "${PLAY_REMEMBER}" == "true" ]]; then CHOICE="${PLAY_MODE}"; else CHOICE="$(ask_mode)"; fi
fi
[[ -n "${CHOICE}" ]] || exit 0
PLAY_MODE="${CHOICE}"

JOIN_MODE=0
case "${CHOICE}" in
  join)
    ask_join || exit 0
    PLAY_ADDRESS="${JOIN_ADDR}"; PLAY_USER="${JOIN_USER}"; write_play
    set_client "${JOIN_ADDR}" "${JOIN_USER}" "${JOIN_PASS}"
    if ! timeout 5 bash -c "exec 3<>/dev/tcp/${JOIN_ADDR}/${PLAY_PORT}" 2>/dev/null; then
      gui_error "Could not reach your friend's game at ${JOIN_ADDR}:${PLAY_PORT}.

Check that their UO Offline is running and that they picked
'Host for friends' (friends.sh on their PC shows the address),
that the address is right, and that Tailscale is on if you use it."
      die "Friend's game at ${JOIN_ADDR}:${PLAY_PORT} is not reachable."
    fi
    JOIN_MODE=1
    ;;
  host)
    write_play
    set_listener "0.0.0.0:${LISTEN_PORT}"
    set_client "127.0.0.1" "${OWNER_USER}" "${OWNER_PASS}"
    if [[ -f "${PIDFILE}" ]] && kill -0 "$(cat "${PIDFILE}")" 2>/dev/null; then
      _running="solo"; [[ -f "${MODE_FILE}" ]] && _running="$(tr -d '[:space:]' < "${MODE_FILE}")"
      if [[ "${_running}" != "host" ]]; then
        if gui_yesno "The server is already running for solo play, and hosting needs it restarted.
Restart it now? The world is saved first; it takes about half a minute.
No keeps playing by yourself this time."; then
          mkdir -p "${LIVE_DIR}"; date +%s > "${LIVE_DIR}/shutdown_request.txt"
          _pid="$(cat "${PIDFILE}")"
          for _ in $(seq 1 60); do kill -0 "${_pid}" 2>/dev/null || break; sleep 1; done
          if kill -0 "${_pid}" 2>/dev/null; then
            gui_error "The server did not stop on request. Run ./stop.sh, then ./start.sh again."
            die "Server did not stop."
          fi
          rm -f "${PIDFILE}"; sleep 2
        fi
      fi
      unset _running _pid
    fi
    ;;
  *)
    PLAY_MODE="solo"; write_play
    set_listener "127.0.0.1:${LISTEN_PORT}"
    set_client "127.0.0.1" "${OWNER_USER}" "${OWNER_PASS}"
    ;;
esac

# ---------------------------------------------------------------------------
# Already running?
#
# If the server is already up (user clicked the desktop icon twice, or
# something else launched it), we attach the client to it but DON'T shut
# it down when the client exits. The user opened a new client, not the
# whole session.
# ---------------------------------------------------------------------------
SERVER_WAS_ALREADY_RUNNING=0
if [[ "${JOIN_MODE}" == "1" ]]; then
  # Joining a friend: nothing to start here, and nothing to shut down after.
  say "Joining a friend's game at ${PLAY_ADDRESS}:${PLAY_PORT}."
  SERVER_WAS_ALREADY_RUNNING=1
elif [[ -f "${PIDFILE}" ]] && kill -0 "$(cat "${PIDFILE}")" 2>/dev/null; then
  say "Server already running (pid $(cat "${PIDFILE}")). Launching client only."
  SERVER_WAS_ALREADY_RUNNING=1
else
  cd "${DIST_DIR}"
  printf '%s\n' "${CHOICE}" > "${MODE_FILE}"

  if [[ -f "${MARKER}" ]]; then
    # ---------------------------------------------------------------------
    # First-launch wizard answers.
    #
    # On a fresh install, ModernUO walks an interactive wizard:
    #   1. "Please enter the name of your shard: [ModernUO]>"  → press Enter
    #      to accept the default. (Our modernuo.json's serverListing.name
    #      doesn't suppress this prompt; the wizard always runs once.)
    #   2. If expansion.json is missing, an expansion-selection prompt
    #      runs here. We pre-write expansion.json so this is skipped.
    #   3. "This server has no accounts."
    #      "Do you want to create the owner account now? (y/n):"  → y
    #   4. "Input Username:"  → admin
    #   5. "Input Password:"  → admin
    #
    # Previous versions of this script sent all answers after a fixed
    # sleep, which caused them to land on the wrong prompts (the leading
    # "y" got captured as the shard name). We watch the log for each
    # prompt's text and reply only after we see it.
    #
    # But the answers have to arrive down a TERMINAL, not a pipe.
    # ModernUO sets Core.Headless from Console.IsInputRedirected, and
    # ConsoleInputHandler.ReadLine THROWS when headless -- the throw is
    # not caught, so the server kills itself the moment it asks the
    # question. Piping the answers in is the one thing that guarantees
    # they can never be read. That is why this runs under script(1),
    # which puts the server on a pseudo-terminal: stdin is a tty, the
    # prompts wait like they would for a person, and we still get the
    # output in the log.
    # ---------------------------------------------------------------------
    say "First launch: running ModernUO setup wizard and creating owner account."
    say "This takes 30-60 seconds while the world saves are generated."

    # FIFO keeps stdin open across multiple `printf` writes.
    FIFO="$(mktemp -u "${INSTALL_ROOT}/.stdin.XXXXXX")"
    mkfifo "${FIFO}"
    exec 9<>"${FIFO}"
    rm -f "${FIFO}"

    # Truncate log so we don't match prompts from a previous failed run.
    : > "${LOGFILE}"

    if command -v script >/dev/null 2>&1; then
      # -q quiet, -e return the child's status, -f flush after every write
      # so the prompt reaches the log before we look for it.
      nohup script -qefc "dotnet ModernUO.dll" /dev/null <&9 >"${LOGFILE}" 2>&1 &
    else
      # No script(1) (util-linux). The wizard cannot be driven without a
      # tty, so run it plainly; it will ask on the console and the
      # manual-fallback message below explains what to do.
      warn "script(1) not found - the setup wizard needs it to answer the prompts."
      nohup dotnet ModernUO.dll >"${LOGFILE}" 2>&1 &
    fi
    SERVER_PID=$!
    echo "${SERVER_PID}" > "${PIDFILE}"

    # ---------------------------------------------------------------------
    # wait_for_log_line <pattern> <timeout-seconds>
    # Returns 0 when the pattern appears in the log, 1 on timeout or if
    # the server process died.
    # ---------------------------------------------------------------------
    wait_for_log_line() {
      local pattern="$1"
      local timeout="${2:-30}"
      local elapsed=0
      while [[ ${elapsed} -lt ${timeout} ]]; do
        if grep -qE "${pattern}" "${LOGFILE}" 2>/dev/null; then
          return 0
        fi
        if ! kill -0 "${SERVER_PID}" 2>/dev/null; then
          warn "Server process died during wizard. See ${LOGFILE}"
          return 1
        fi
        sleep 1
        elapsed=$((elapsed + 1))
      done
      warn "Timed out (${timeout}s) waiting for log pattern: ${pattern}"
      return 1
    }

    # Step 1: shard-name prompt → accept default.
    if wait_for_log_line "name of your shard" 30; then
      say "Shard-name prompt detected → accepting default name."
      printf '\n' >&9
    fi

    # Step 3: account-creation prompt → answer "y".
    if wait_for_log_line "create the owner account" 30; then
      say "Account-creation prompt detected → answering y."
      printf 'y\n' >&9
    fi

    # Step 4: username prompt.
    if wait_for_log_line "Input Username" 15; then
      say "Username prompt detected → ${OWNER_USER}."
      printf '%s\n' "${OWNER_USER}" >&9
    fi

    # Step 5: password prompt.
    if wait_for_log_line "Input Password" 15; then
      say "Password prompt detected → (hidden)."
      printf '%s\n' "${OWNER_PASS}" >&9
    fi

    # Wait for account creation confirmation before clearing the marker.
    if wait_for_log_line "Owner account created" 15; then
      say "Owner account created."
      rm -f "${MARKER}"
    else
      warn "Did not see 'Owner account created' confirmation in log."
      warn ""
      warn "Create it by hand instead - this takes a minute and only happens once:"
      warn ""
      warn "    cd ${DIST_DIR}"
      warn "    ${DOTNET_ROOT}/dotnet ModernUO.dll"
      warn ""
      warn "(The full path matters: .NET is installed privately under"
      warn "${DOTNET_ROOT} and is not on your PATH, so a bare 'dotnet' will"
      warn "say command not found. Do NOT apt install dotnet - you have it.)"
      warn ""
      warn "Answer 'y' when it asks about the owner account, then give it a"
      warn "username and password (admin / admin is fine on your own machine)."
      warn "Wait for 'Listening: 127.0.0.1:2593', then press Ctrl+C to stop it."
      warn "After that:"
      warn ""
      warn "    rm -f ${MARKER}"
      warn ""
      warn "and start the game normally. Full log: ${LOGFILE}"
    fi
  else
    say "Starting ModernUO server..."
    : > "${LOGFILE}"
    nohup dotnet ModernUO.dll </dev/null >"${LOGFILE}" 2>&1 &
    SERVER_PID=$!
    echo "${SERVER_PID}" > "${PIDFILE}"
  fi

  # Wait for the listener to come up. Up to 60 seconds — first launch with
  # world generation is slower than subsequent ones.
  say "Waiting for server to listen on port ${LISTEN_PORT}..."
  for i in $(seq 1 60); do
    if ss -tln 2>/dev/null | grep -q ":${LISTEN_PORT} "; then
      say "Server is up (took ${i}s)."
      break
    fi
    if ! kill -0 "${SERVER_PID}" 2>/dev/null; then
      die "Server died during startup. See ${LOGFILE}"
    fi
    sleep 1
  done

  if ! ss -tln 2>/dev/null | grep -q ":${LISTEN_PORT} "; then
    warn "Server didn't start listening within 60s. Check ${LOGFILE}"
    warn "Leaving it running; it may still come up."
    log_line "--- last 40 lines of ${LOGFILE} ---"
    tail -n 40 "${LOGFILE}" >> "${LAUNCHLOG}" 2>/dev/null || true
    gui_error "The server did not finish starting within 60 seconds.

The game will still try to open. If it cannot connect, the reason is in:
${LOGFILE}"
  fi
fi

# ---------------------------------------------------------------------------
# Sync client version into ClassicUO settings.json.
#
# Different UO data folders are different versions (7.0.50, 7.0.103, 7.0.115,
# etc.). ModernUO auto-detects the version from the data files and logs it.
# If our settings.json's clientversion doesn't match, ClassicUO either fails
# to parse the data files (FormatException at AnimationsLoader.Load) or
# gets kicked by the server's version-restriction check.
#
# We read the version ModernUO detected and patch settings.json to match.
# ---------------------------------------------------------------------------
sync_client_version() {
  local settings_file="${CLASSICUO_DIR}/settings.json"
  [[ -f "${settings_file}" ]] || return 0
  [[ -f "${LOGFILE}" ]] || return 0

  local detected
  detected="$(grep -oE 'Automatically detected client version [0-9]+\.[0-9]+\.[0-9]+\.[0-9]+' "${LOGFILE}" \
    | tail -n1 | awk '{print $NF}')"

  if [[ -z "${detected}" ]]; then
    return 0
  fi

  local current
  current="$(grep -oE '"clientversion"[[:space:]]*:[[:space:]]*"[^"]*"' "${settings_file}" \
    | sed -E 's/.*"([^"]*)"[[:space:]]*$/\1/')"

  if [[ "${current}" == "${detected}" ]]; then
    return 0
  fi

  say "Updating ClassicUO clientversion: ${current} → ${detected}"
  sed -i -E "s/(\"clientversion\"[[:space:]]*:[[:space:]]*\")[^\"]*(\")/\1${detected}\2/" "${settings_file}"
}
sync_client_version

if [[ "${CHOICE}" == "host" ]]; then
  gui_info "$(addresses_text)"
fi

# ---------------------------------------------------------------------------
# Launch ClassicUO and wait for it.
#
# When the player closes the client, this script triggers a clean server
# shutdown so the world saves and nothing has to be done in a terminal.
#
# Override with KEEP_SERVER_RUNNING=1 ./start.sh if you want the server to
# stay up after the client exits (e.g. you're going to relaunch the client,
# or you connect from a second machine on your LAN).
# ---------------------------------------------------------------------------
CLASSICUO_BIN=""
if [[ -f "${INSTALL_ROOT}/.classicuo-bin-path" ]]; then
  CLASSICUO_BIN="$(cat "${INSTALL_ROOT}/.classicuo-bin-path")"
fi

if [[ -z "${CLASSICUO_BIN}" ]] || [[ ! -x "${CLASSICUO_BIN}" ]]; then
  for name in ClassicUO ClassicUO.bin.x86_64 cuo; do
    if [[ -x "${CLASSICUO_DIR}/${name}" ]]; then
      CLASSICUO_BIN="${CLASSICUO_DIR}/${name}"
      break
    fi
  done
fi

if [[ -z "${CLASSICUO_BIN}" ]] || [[ ! -x "${CLASSICUO_BIN}" ]]; then
  warn "ClassicUO binary not found under ${CLASSICUO_DIR}."
  warn "Server is running on 127.0.0.1:${LISTEN_PORT}. Launch your client manually."
  warn "Run ${INSTALL_ROOT}/stop.sh when you're done to save and shut down the server."
  gui_error "The game client (ClassicUO) is missing.

Looked in: ${CLASSICUO_DIR}

The server itself started and is running on 127.0.0.1:${LISTEN_PORT}.
Re-running install.sh will fetch the client again."
  exit 0
fi

# ---------------------------------------------------------------------------
# shutdown_server: SIGTERM the server, wait for clean save, fall back to kill.
# Mirrors stop.sh so behavior is identical regardless of which path closes
# the server.
# ---------------------------------------------------------------------------
shutdown_server() {
  if [[ ! -f "${PIDFILE}" ]]; then
    return
  fi
  local pid
  pid="$(cat "${PIDFILE}")"
  if ! kill -0 "${pid}" 2>/dev/null; then
    rm -f "${PIDFILE}"
    return
  fi

  say "Client closed. Saving world and shutting down server (pid ${pid})..."
  kill -TERM "${pid}"

  # ModernUO saves on SIGTERM. Populated worlds take 10-20s; allow 30.
  for _ in $(seq 1 30); do
    if ! kill -0 "${pid}" 2>/dev/null; then
      say "Server stopped cleanly."
      rm -f "${PIDFILE}"
      return
    fi
    sleep 1
  done

  warn "Server didn't stop within 30s. Forcing kill — world state since last autosave may be lost."
  kill -9 "${pid}" 2>/dev/null || true
  rm -f "${PIDFILE}"
}

# Run shutdown on script exit (including Ctrl-C) unless:
#   - the user opted out with KEEP_SERVER_RUNNING=1, or
#   - the server was already running before we got here (someone else owns it).
if [[ "${KEEP_SERVER_RUNNING:-0}" != "1" ]] && [[ "${SERVER_WAS_ALREADY_RUNNING}" == "0" ]]; then
  trap shutdown_server EXIT INT TERM
fi

say "Launching ClassicUO: ${CLASSICUO_BIN}"
cd "$(dirname "${CLASSICUO_BIN}")"

# Run in the foreground and wait. When the client window closes, the
# process exits and the EXIT trap above shuts down the server.
#
# Its output is teed into launch.log: a client that dies on startup (missing
# system library, unreadable UO data) otherwise leaves nothing behind at all
# when the desktop icon launched it.
CLIENT_START="$(date +%s)"
"./$(basename "${CLASSICUO_BIN}")" 2>&1 | tee -a "${LAUNCHLOG}"
CLIENT_RC="${PIPESTATUS[0]}"
CLIENT_RAN="$(( $(date +%s) - CLIENT_START ))"

if [[ "${CLIENT_RC}" -ne 0 ]]; then
  warn "ClassicUO exited with code ${CLIENT_RC} after ${CLIENT_RAN}s."
  gui_error "The game client closed straight away (exit code ${CLIENT_RC}).

This is usually a missing system library or a UO data folder the client
cannot read. The client's own error output is at the end of:
${LAUNCHLOG}"
elif [[ "${CLIENT_RAN}" -lt 5 ]]; then
  warn "ClassicUO exited cleanly after only ${CLIENT_RAN}s."
  gui_error "The game client opened and closed again after ${CLIENT_RAN} seconds.

Its output is at the end of:
${LAUNCHLOG}"
fi

# Explicit exit so the trap fires cleanly with a known status.
exit 0
