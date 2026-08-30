#!/usr/bin/env bash
# =========================================================================
# start-server.sh — Launch the ModernUO headless LAN server.
#
# Behavior:
#   - First run: also creates the owner account by feeding scripted answers
#     to the server over stdin.
#   - Runs the server in the foreground. Ctrl-C (or stop.sh from another
#     terminal) shuts it down cleanly, saving the world first.
#   - No game client involved — this starts the server process only.
#     Connect to it with ClassicUO (or the official client) from any
#     machine on the LAN.
# =========================================================================
set -uo pipefail

say()  { printf '\033[0;36m--> %s\033[0m\n' "$*"; }
warn() { printf '\033[0;33m[WARN]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[0;31m[ERROR]\033[0m %s\n' "$*" >&2; exit 1; }

# install-server.sh copies this script into the install root as start.sh,
# but it's also runnable straight from the repo (./scripts/start-server.sh)
# for testing. Either way the server root is locked to ./server-runtime
# next to the repo root - never wherever this particular copy happens to
# sit - so resolve it the same way install-server.sh does: one directory
# above wherever this script's own directory is. Both scripts/ (the
# checked-in copy) and server-runtime/ (the deployed copy) sit directly
# under the repo root, so "one level up from here" lands on the repo root
# in both cases, and REPO_ROOT/server-runtime always lands on the real
# install.
REPO_ROOT="$(cd "$(dirname "$(dirname "${BASH_SOURCE[0]}")")" && pwd)"
INSTALL_ROOT="${REPO_ROOT}/server-runtime"
DIST_DIR="${INSTALL_ROOT}/ModernUO/Distribution"
PIDFILE="${INSTALL_ROOT}/modernuo.pid"
LOGFILE="${INSTALL_ROOT}/modernuo.log"
MARKER="${INSTALL_ROOT}/.needs-owner-account"

OWNER_USER="admin"
OWNER_PASS="admin"
LISTEN_PORT=2593

# Find a usable dotnet: prefer one already on PATH (a system install, a
# container base image, or install-server.sh's own PATH-first check at
# bootstrap time), and only fall back to the per-user copy install-server.sh
# bootstraps into ~/.dotnet when nothing is already resolvable.
if command -v dotnet >/dev/null 2>&1; then
  DOTNET_ROOT="$(dirname "$(command -v dotnet)")"
else
  DOTNET_ROOT="${HOME}/.dotnet"
fi
export DOTNET_ROOT
export PATH="${DOTNET_ROOT}:${PATH}"

[[ -f "${DIST_DIR}/ModernUO.dll" ]] || die "ModernUO not built at ${DIST_DIR}/ModernUO.dll. Run: ${REPO_ROOT}/install-server.sh install"
command -v dotnet >/dev/null 2>&1 || die "dotnet not found (looked in \$PATH and ${DOTNET_ROOT}). Run the installer first."

# ---------------------------------------------------------------------------
# Ask GitHub whether there is a newer UO Offline before starting anything.
#
# The deployed update-check.sh is the headless server variant (see
# scripts/update-check-server.sh) - no GUI dialog, no interactive terminal
# prompt, no display-server dependency, and no self-driven update. It only
# ever prints a one-line [NOTICE] to stderr if a newer version exists, or
# says nothing at all (no internet, GitHub down, rate limited, already up
# to date), and always returns so the server starts regardless. Updating
# is a deliberate, separate step: run install-server.sh update.
# ---------------------------------------------------------------------------
UPDATER="${INSTALL_ROOT}/update-check.sh"
[[ -x "${UPDATER}" ]] && "${UPDATER}"

if [[ -f "${PIDFILE}" ]] && kill -0 "$(cat "${PIDFILE}")" 2>/dev/null; then
  die "Server already running (pid $(cat "${PIDFILE}")). Use stop.sh to stop it first."
fi

cd "${DIST_DIR}"

# ---------------------------------------------------------------------------
# shutdown_server: SIGTERM the server, wait for clean save, fall back to kill.
# Mirrors stop.sh so behavior is identical regardless of which path closes
# the server.
# ---------------------------------------------------------------------------
shutdown_server() {
  [[ -f "${PIDFILE}" ]] || return
  local pid
  pid="$(cat "${PIDFILE}")"
  if ! kill -0 "${pid}" 2>/dev/null; then
    rm -f "${PIDFILE}"
    return
  fi

  say "Shutting down. Saving world (pid ${pid})..."
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
trap shutdown_server EXIT INT TERM

if [[ -f "${MARKER}" ]]; then
  # ---------------------------------------------------------------------
  # First-launch wizard answers.
  #
  # On a fresh install, ModernUO walks an interactive wizard:
  #   1. "Please enter the name of your shard: [ModernUO]>"  → press Enter
  #      to accept the default. (modernuo.json's serverListing.name
  #      doesn't suppress this prompt; the wizard always runs once.)
  #   2. If expansion.json is missing, an expansion-selection prompt
  #      runs here. The installer pre-writes expansion.json so this is
  #      skipped.
  #   3. "This server has no accounts."
  #      "Do you want to create the owner account now? (y/n):"  → y
  #   4. "Input Username:"  → admin
  #   5. "Input Password:"  → admin
  #
  # The answers have to arrive down a TERMINAL, not a pipe. ModernUO sets
  # Core.Headless from Console.IsInputRedirected, and
  # ConsoleInputHandler.ReadLine THROWS when headless -- the throw is not
  # caught, so the server kills itself the moment it asks the question.
  # Piping the answers in is the one thing that guarantees they can never
  # be read. That is why this runs under script(1), which puts the server
  # on a pseudo-terminal: stdin is a tty, the prompts wait like they would
  # for a person, and we still get the output in the log.
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
    say "Owner account created: ${OWNER_USER} / ${OWNER_PASS}"
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
    warn "username and password (admin / admin is fine on a LAN-only box)."
    warn "Wait for the Listening line, then press Ctrl+C to stop it."
    warn "After that:"
    warn ""
    warn "    rm -f ${MARKER}"
    warn ""
    warn "and start the server normally. Full log: ${LOGFILE}"
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
fi

LAN_IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
say "Server running (pid ${SERVER_PID})."
if [[ -n "${LAN_IP}" ]]; then
  say "LAN clients connect to: ${LAN_IP}:${LISTEN_PORT}"
fi
say "Logs: ${LOGFILE}"
say "Press Ctrl-C to stop (saves the world), or run stop.sh from another terminal."

# Block here until the server exits, whether on its own or via the trap
# above (Ctrl-C, TERM, or this script's own exit).
wait "${SERVER_PID}"
