#!/usr/bin/env bash
# =========================================================================
# update-check-server.sh - "is there a newer UO Offline?" check for the
# headless LAN server, run at start.sh launch.
#
# Server-only variant of update-check.sh (the desktop/ClassicUO checker).
# That one shows a kdialog/zenity popup or an interactive terminal prompt
# and can drive a self-update, because it's launched from a desktop icon
# with no terminal attached and the player needs some way to be asked.
# None of that applies here:
#
#   - No GUI. A headless box has no $DISPLAY/$WAYLAND_DISPLAY, and even on
#     one that does, a background server process popping a dialog nobody
#     is watching is exactly the kind of hang this script exists to avoid.
#   - No interactive terminal prompt either. start.sh runs the same way
#     whether an operator is watching or it's a service/cron/tmux session
#     with no one attached; a `read` here would hang either way.
#   - No self-driven download-and-rebuild. install-server.sh already has
#     a proper `update` action for that, run by the operator on their own
#     schedule - this script's only job is to tell them one exists.
#
# Same "never cost the operator their session" rules as the desktop
# checker:
#   - No internet, GitHub down, rate limited, anything at all goes wrong:
#     say nothing and let the server start.
#   - Already up to date: say nothing. No output.
#   - Behind: print one notice line to stderr and continue.
#
# Always exits 0 - start.sh proceeds to launch the server either way.
# =========================================================================
set -uo pipefail

INSTALL_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STAMP="${INSTALL_ROOT}/uo-offline-version.json"
TIMEOUT=6

# Nothing to compare against, or no way to check: start the server.
[[ -f "${STAMP}" ]] || exit 0
command -v curl >/dev/null 2>&1 || exit 0

json_field() { # <field> <text>
  printf '%s' "$2" \
    | grep -oE "\"$1\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" \
    | head -n1 \
    | sed -E 's/.*"([^"]*)"[[:space:]]*$/\1/'
}

STAMP_TEXT="$(cat "${STAMP}" 2>/dev/null)" || exit 0
LOCAL_SHA="$(json_field Sha    "${STAMP_TEXT}")"
REPO="$(json_field Repo        "${STAMP_TEXT}")"
BRANCH="$(json_field Branch    "${STAMP_TEXT}")"

[[ -n "${LOCAL_SHA}" && -n "${REPO}" && -n "${BRANCH}" ]] || exit 0

API="https://api.github.com/repos/${REPO}"
UA="User-Agent: uo-offline-server-launcher"

HEAD_JSON="$(curl -fsSL --max-time "${TIMEOUT}" -H "${UA}" "${API}/commits/${BRANCH}" 2>/dev/null)"
[[ -n "${HEAD_JSON}" ]] || exit 0

REMOTE_SHA="$(printf '%s' "${HEAD_JSON}" \
  | grep -oE '"sha"[[:space:]]*:[[:space:]]*"[0-9a-f]{40}"' \
  | head -n1 | sed -E 's/.*"([0-9a-f]{40})".*/\1/')"

[[ -n "${REMOTE_SHA}" ]] || exit 0

# Up to date. Say nothing at all.
[[ "${REMOTE_SHA}" != "${LOCAL_SHA}" ]] || exit 0

REPO_ROOT="$(dirname "${INSTALL_ROOT}")"
printf '[NOTICE] A server update is available (%s -> %s). Run %s/install-server.sh update to apply.\n' \
  "${LOCAL_SHA:0:7}" "${REMOTE_SHA:0:7}" "${REPO_ROOT}" >&2

exit 0
