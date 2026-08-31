#!/usr/bin/env bash
# =========================================================================
# UO Offline (ModernUO edition) — Headless LAN Server Installer
#
# What this does:
#   1. Installs Linux prerequisites (Debian/Ubuntu/SteamOS/Fedora).
#   2. Clones ModernUO and bootstraps .NET 10 per-user.
#   3. Deploys the PlayerBots source files into the ModernUO source tree.
#   4. Builds ModernUO (including the bots) for Linux x64.
#   5. Locates your Ultima Online game data files, interactively if needed.
#   5b. Swaps in genuine T2A-era Felucca map art (intact Magincia) from the
#      UO Second Age distribution. Reversible; INSTALL_T2A_MAP=0 to skip.
#   6. Downloads Nerun's pre-T2A spawn map for world population.
#   7. Writes a ModernUO config that listens on all interfaces (LAN) with
#      auto account creation on.
#   8. Installs start/stop scripts for running the server headlessly.
#
# This installer does NOT install a game client. It builds and configures
# a ModernUO server only, meant to run on a headless Linux box (or a
# terminal) with LAN clients (ClassicUO or the official client, on their
# own machines) connecting to it over the network.
#
# After install, run start.sh to bring the server up in the foreground.
# First launch creates the owner account and populates the world.
#
# Server listens on 0.0.0.0:2593 by default — reachable from any device on
# your LAN. It is not meant to be exposed to the open internet as-is; put
# it behind a firewall/router that only allows your LAN to reach it, and
# change the default admin password after your first login.
#
# Where it installs:
#   Always ./server-runtime next to this script (the repo root). Not
#   configurable — no --dir/--install-root flag, no INSTALL_DIR/
#   INSTALL_ROOT override. Every install/update/uninstall operates on
#   exactly one, predictable, workspace-local path.
#
# Lifecycle actions:
#   ./install-server.sh install     Set up a new server in ./server-runtime
#   ./install-server.sh update      Rebuild ModernUO/PlayerBots; keeps
#                                    saves, accounts, Configuration/, and
#                                    uo-data/ untouched
#   ./install-server.sh uninstall   Remove ./server-runtime
#   ./install-server.sh             Interactive menu if no action is given
#   --force / -y                    Skip confirmation prompts
#
# Root/sudo is optional, not required:
#   - If a compatible dotnet is already on $PATH, or every native package
#     ModernUO needs is already present, the package-manager step is
#     skipped automatically.
#   - --skip-deps (or SKIP_DEPS=1) skips it unconditionally, assuming you
#     already have everything.
#   - --no-root (or NO_ROOT=1), or simply not having sudo, skips it and
#     prints the exact packages to install by hand instead of blocking.
#
# Notes:
#   - UO Classic game files are © Electronic Arts and are not distributed
#     by this installer. You provide your own, from an existing install or
#     from https://uo.com/download.
#   - ModernUO is open source (GPL-3.0) and ships no game assets.
# =========================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ---------------------------------------------------------------------------
# Pretty output (defined early - the argument parser below uses die()).
# ---------------------------------------------------------------------------
banner() { printf '\n\033[1;36m=== %s ===\033[0m\n' "$*"; }
say()    { printf '\033[0;36m--> %s\033[0m\n' "$*"; }
ok()     { printf '\033[0;32m[OK]\033[0m %s\n' "$*"; }
warn()   { printf '\033[0;33m[WARN]\033[0m %s\n' "$*" >&2; }
die()    { printf '\033[0;31m[ERROR]\033[0m %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# Lifecycle action & flags
# ---------------------------------------------------------------------------
ACTION=""
FORCE="${FORCE:-0}"
# The map editor is a builder's tool - waypoints, spawns, a live view of the
# bots - not something you need in order to play. On by default, off with
# --no-map-editor.
INSTALL_MAP_EDITOR="${INSTALL_MAP_EDITOR:-1}"
# --skip-deps / --no-root: see "Root/sudo is optional" above. install_deps
# reads these.
SKIP_DEPS="${SKIP_DEPS:-0}"
NO_ROOT="${NO_ROOT:-0}"

print_usage() {
  cat <<USAGE
Usage: $(basename "$0") [install|update|uninstall] [options]

Actions (interactive menu if omitted):
  install       Set up a new server in ./server-runtime
  update        Rebuild ModernUO/PlayerBots; keeps saves, accounts,
                Configuration/, and uo-data/ untouched
  uninstall     Remove ./server-runtime

Options:
  --force, -y      Skip confirmation prompts (install-exists / uninstall)
  --skip-deps      Skip native package installation, assume it's done
  --no-root        Don't attempt sudo; print packages to install by hand
  --no-map-editor  Don't install the browser-based map editor
  -h, --help       Show this help
USAGE
}

for arg in "$@"; do
  case "${arg}" in
    install|--install)     ACTION="install" ;;
    update|--update)        ACTION="update" ;;
    uninstall|--uninstall)  ACTION="uninstall" ;;
    --force|-y)              FORCE=1 ;;
    --skip-deps)             SKIP_DEPS=1 ;;
    --no-root)               NO_ROOT=1 ;;
    --no-map-editor)         INSTALL_MAP_EDITOR=0 ;;
    -h|--help)               print_usage; exit 0 ;;
    *) print_usage; die "Unknown argument: ${arg}" ;;
  esac
done

# ---------------------------------------------------------------------------
# Paths and URLs
# ---------------------------------------------------------------------------
# Locked to ./server-runtime next to this script (the repo root). No
# --dir/--install-root flag and no INSTALL_DIR/INSTALL_ROOT override - every
# install/update/uninstall operates on exactly this path, so it's always
# where an editor opened on the repo expects it to be.
INSTALL_ROOT="${SCRIPT_DIR}/server-runtime"
MODERNUO_REPO="https://github.com/modernuo/ModernUO.git"
MODERNUO_DIR="${INSTALL_ROOT}/ModernUO"
DIST_DIR="${MODERNUO_DIR}/Distribution"
CFG_DIR="${DIST_DIR}/Configuration"
SPAWNERS_DIR="${DIST_DIR}/Spawners/uoclassic"

# Where UO client data files get copied to - fixed, like INSTALL_ROOT.
# Never symlinked; see copy_uo_data.
UO_DATA_DIR="${INSTALL_ROOT}/uo-data"

# Nerun's pre-T2A spawn data. ModernUO's [GenerateSpawners command parses
# the .map format directly.
SPAWN_MAP_URL="https://raw.githubusercontent.com/Nerun/runuo-nerun-distro/master/Distro/Data/Nerun's%20Distro/Spawns/uoclassic/UOClassic.map"

# Genuine T2A-era Felucca map art (intact Magincia, pre-destruction world),
# pulled from the official UO Second Age (client 5.0.8.3) distribution. Most
# modern client data ships modern map art with 15+ years of EA world edits;
# swapping these three files restores the T2A look. Set INSTALL_T2A_MAP=0 to
# keep whatever map art came with your data files. See docs/T2A-MAP.md.
INSTALL_T2A_MAP=1
T2A_INSTALLER_URL="https://download.uosecondage.com/UOSA_Client_Setup.exe"
T2A_SRC_DIR="${INSTALL_ROOT}/t2a-src"

# ---------------------------------------------------------------------------
# Config defaults
# ---------------------------------------------------------------------------
EXPANSION_ID=1
EXPANSION_NAME="T2A"
OWNER_USER="admin"
OWNER_PASS="admin"
# 0.0.0.0 so LAN clients on other machines can reach the server directly.
# Override with LISTEN_ADDR=127.0.0.1:2593 ./install-server.sh to go back
# to localhost-only.
LISTEN_ADDR="${LISTEN_ADDR:-0.0.0.0:2593}"
SHARD_NAME="UO Offline"

# Per-user .NET install location. Avoids needing root and survives SteamOS
# read-only filesystem reverts. Ignored if a compatible dotnet is already
# on $PATH (see bootstrap_dotnet).
DOTNET_ROOT="${DOTNET_ROOT:-${HOME}/.dotnet}"

# ---------------------------------------------------------------------------
# Step 1 — Pre-flight checks
# ---------------------------------------------------------------------------
preflight() {
  banner "Pre-flight checks"

  [[ "$(uname -s)" == "Linux" ]] || die "Linux-only installer."
  [[ "${EUID}" -ne 0 ]]         || die "Run as your normal user, not root. sudo will be invoked when needed."

  command -v curl   >/dev/null || die "curl is required."
  command -v sudo   >/dev/null \
    || warn "sudo not found — will fall back to printing the packages you need instead of installing them. See --skip-deps/--no-root."

  # Check writability here rather than failing several steps later with a
  # confusing message.
  mkdir -p "${INSTALL_ROOT}" 2>/dev/null \
    || die "Cannot create ${INSTALL_ROOT}. Pick a folder you can write to."
  [[ -w "${INSTALL_ROOT}" ]] \
    || die "${INSTALL_ROOT} is not writable. Pick a folder you own."

  ok "Install root: ${INSTALL_ROOT}"
}

# ---------------------------------------------------------------------------
# Step 2 — Native dependencies
#
# Server-only: no unzip/unar/unrar (those existed only to unpack a
# downloaded game-client installer, which this installer doesn't do — see
# resolve_uo_data). p7zip stays; the optional T2A map-art swap uses 7z on
# an NSIS archive.
#
# Root/sudo is optional. install_deps tries, in order:
#   1. Is a compatible dotnet already on $PATH? If so there's nothing to
#      install at all - skip the package manager entirely.
#   2. --skip-deps / SKIP_DEPS=1: trust the caller, skip unconditionally.
#   3. Are git, curl and the native libs already present? Skip.
#   4. --no-root / NO_ROOT=1, or no sudo and not root: can't install
#      packages here. Print exactly what's needed and move on instead of
#      blocking on a sudo prompt that will never come.
#   5. Otherwise, install via the distro's package manager as before.
# ---------------------------------------------------------------------------
APT_PKGS=(libicu-dev libdeflate-dev zstd libargon2-dev liburing-dev libgdiplus p7zip-full build-essential git)
PACMAN_PKGS=(icu libdeflate zstd argon2 liburing libgdiplus p7zip base-devel git)
DNF_PKGS=(libicu libdeflate-devel zstd libargon2-devel liburing-devel libgdiplus p7zip @development-tools git)

# Version of dotnet already on $PATH, if it satisfies major version 8+.
# Printed on stdout, empty (and non-zero exit) otherwise.
dotnet_on_path_version() {
  command -v dotnet >/dev/null 2>&1 || return 1
  local ver="" major=""
  ver="$(dotnet --version 2>/dev/null | head -n1)"
  major="${ver%%.*}"
  [[ "${major}" =~ ^[0-9]+$ ]] || return 1
  [[ "${major}" -ge 8 ]] || return 1
  printf '%s' "${ver}"
}

# Heuristic: git, curl, a usable dotnet, and (where ldconfig exists) a
# couple of the native libs ModernUO links against. Not exhaustive - good
# enough to tell "this box already has a working toolchain" from "needs
# the package manager step".
deps_already_satisfied() {
  command -v git  >/dev/null 2>&1 || return 1
  command -v curl >/dev/null 2>&1 || return 1
  dotnet_on_path_version >/dev/null || return 1
  if command -v ldconfig >/dev/null 2>&1; then
    ldconfig -p 2>/dev/null | grep -q 'libicuuc'   || return 1
    ldconfig -p 2>/dev/null | grep -q 'libdeflate' || return 1
  fi
  return 0
}

print_manual_dep_instructions() {
  warn "ModernUO needs these native packages to build and run:"
  if command -v apt-get >/dev/null 2>&1; then
    warn "    sudo apt-get install -y ${APT_PKGS[*]}"
  elif command -v pacman >/dev/null 2>&1; then
    warn "    sudo pacman -S --needed ${PACMAN_PKGS[*]}"
  elif command -v dnf >/dev/null 2>&1; then
    warn "    sudo dnf install -y ${DNF_PKGS[*]}"
  else
    warn "    git, libicu, libdeflate, zstd, libargon2, liburing, libgdiplus, p7zip, and a C build toolchain"
  fi
  warn "Install them yourself (or re-run with sudo available) and re-run this installer."
}

install_deps() {
  banner "Installing native dependencies"

  local existing_dotnet=""
  if existing_dotnet="$(dotnet_on_path_version)"; then
    ok "Found .NET ${existing_dotnet} already on PATH ($(command -v dotnet)) — skipping the package manager step entirely."
    return
  fi

  if [[ "${SKIP_DEPS}" == "1" ]]; then
    say "Skipping dependency installation (--skip-deps). Assuming required tools are already present."
    return
  fi

  if deps_already_satisfied; then
    ok "Required tooling already present (git, curl, dotnet, native libs). Skipping the package manager step."
    return
  fi

  if [[ "${NO_ROOT}" == "1" ]]; then
    say "Skipping automatic package installation (--no-root)."
    print_manual_dep_instructions
    return
  fi

  if ! command -v sudo >/dev/null 2>&1; then
    warn "sudo is not available and some required packages are missing."
    print_manual_dep_instructions
    return
  fi

  if command -v apt-get >/dev/null; then
    say "Debian-family distro detected. Using apt."
    sudo apt-get update -y
    sudo apt-get install -y "${APT_PKGS[@]}"
  elif command -v pacman >/dev/null; then
    say "Arch-family distro detected. Using pacman."
    if [[ -f /etc/os-release ]] && grep -qi steamos /etc/os-release; then
      warn "SteamOS detected. If you haven't already, run:"
      warn "    sudo steamos-readonly disable"
      warn "    sudo pacman-key --init && sudo pacman-key --populate"
      warn "Press Ctrl-C now to abort, or any key to continue."
      read -r -n 1 -s
    fi
    sudo pacman -S --needed --noconfirm "${PACMAN_PKGS[@]}"
  elif command -v dnf >/dev/null; then
    say "Fedora-family distro detected. Using dnf."
    sudo dnf install -y "${DNF_PKGS[@]}"
  else
    warn "Unsupported package manager."
    print_manual_dep_instructions
    return
  fi

  command -v git >/dev/null || warn "git is still missing after the dependency step. Steps that need it will fail until it's installed."

  ok "Dependencies installed."
}

# ---------------------------------------------------------------------------
# Step 3 — Clone ModernUO (full history, required by Nerdbank.GitVersioning)
# ---------------------------------------------------------------------------
fetch_modernuo() {
  banner "Fetching ModernUO source"

  if [[ -d "${MODERNUO_DIR}/.git" ]]; then
    say "ModernUO already cloned."
    cd "${MODERNUO_DIR}"

    if [[ -f .git/shallow ]]; then
      say "Unshallowing existing clone..."
      git fetch --unshallow || git fetch --depth=2147483647
    fi

    # --force because upstream moves tags (build-tool-latest is re-pointed
    # every release); without it the fetch fails with "would clobber existing
    # tag". None of this is fatal - a clone that will not update still builds,
    # and local edits to tracked files (the stock-file patches in
    # INTEGRATION-NOTES.txt) are the usual reason a pull refuses.
    git fetch --all --tags --force || warn "git fetch failed; using the checkout on disk."
    git checkout main               || warn "git checkout main failed; using the current branch."
    git pull --ff-only              || warn "git pull failed; using the checkout on disk."
  else
    say "Cloning ModernUO (full history)..."
    git clone "${MODERNUO_REPO}" "${MODERNUO_DIR}"
  fi

  ok "ModernUO source at ${MODERNUO_DIR}"
}

# ---------------------------------------------------------------------------
# Step 4 — Bootstrap .NET SDK per-user
# ---------------------------------------------------------------------------
bootstrap_dotnet() {
  banner "Bootstrapping .NET SDK"

  local channel="LTS"
  local gj="${MODERNUO_DIR}/global.json"
  if [[ -f "${gj}" ]]; then
    local sdk_ver
    sdk_ver="$(grep -oE '"version"[[:space:]]*:[[:space:]]*"[^"]+"' "${gj}" \
      | head -n1 | sed -E 's/.*"([^"]+)".*/\1/' || true)"
    if [[ -n "${sdk_ver}" ]]; then
      channel="$(echo "${sdk_ver}" | awk -F. '{print $1"."$2}')"
      say "ModernUO wants SDK ${sdk_ver}; using channel ${channel}."
    fi
  fi

  # Already on $PATH (system package, another install, a container base
  # image) and it has the channel ModernUO wants? Use it as-is - no sudo,
  # no download, no per-user copy.
  if command -v dotnet >/dev/null 2>&1 \
     && dotnet --list-sdks 2>/dev/null | grep -qE "^${channel}\."; then
    ok "Found compatible SDK already on PATH: $(command -v dotnet)"
    DOTNET_ROOT="$(dirname "$(command -v dotnet)")"
    export DOTNET_ROOT
    return
  fi

  if [[ -x "${DOTNET_ROOT}/dotnet" ]] \
     && "${DOTNET_ROOT}/dotnet" --list-sdks 2>/dev/null | grep -qE "^${channel}\."; then
    ok "Found compatible SDK at ${DOTNET_ROOT}"
    export PATH="${DOTNET_ROOT}:${PATH}"
    export DOTNET_ROOT
    return
  fi

  say "Downloading dotnet-install.sh..."
  local tmp="${INSTALL_ROOT}/.dotnet-install.sh"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${tmp}"
  chmod +x "${tmp}"

  say "Installing .NET SDK ${channel} into ${DOTNET_ROOT}..."
  "${tmp}" --channel "${channel}" --install-dir "${DOTNET_ROOT}"
  rm -f "${tmp}"

  export PATH="${DOTNET_ROOT}:${PATH}"
  export DOTNET_ROOT

  [[ -x "${DOTNET_ROOT}/dotnet" ]] || die "dotnet not installed at ${DOTNET_ROOT}/dotnet."
  ok "Installed: $(${DOTNET_ROOT}/dotnet --version)"
}

# ---------------------------------------------------------------------------
# Step 5 — Build ModernUO
# ---------------------------------------------------------------------------
# Delete every Projects/*/obj and /bin. Pure build output - restore and the
# next build regenerate all of it.
clear_build_artifacts() {
  local projects_dir="${MODERNUO_DIR}/Projects"
  [[ -d "${projects_dir}" ]] || return 0

  local removed=0
  local d
  for d in "${projects_dir}"/*/obj "${projects_dir}"/*/bin; do
    if [[ -d "${d}" ]]; then
      rm -rf "${d}"
      removed=$((removed + 1))
    fi
  done
  say "Cleared ${removed} stale build output folder(s)."
}

build_modernuo() {
  banner "Building ModernUO"

  export PATH="${DOTNET_ROOT}:${PATH}"
  export DOTNET_ROOT

  if [[ -f "${DIST_DIR}/ModernUO.dll" ]]; then
    say "ModernUO already built. Skipping (delete ${DIST_DIR}/ModernUO.dll to force rebuild)."
    return
  fi

  cd "${MODERNUO_DIR}"
  chmod +x ./publish.sh
  ./publish.sh release linux x64 || true

  if [[ ! -f "${DIST_DIR}/ModernUO.dll" ]]; then
    # A build can fail on stale intermediate output left behind by a
    # DIFFERENT .NET SDK - if a distro package or another install is on PATH,
    # whichever ran last wins. The giveaway is the build tool reporting
    # "'Cleaning project' failed with exit code 1", with a
    # ResolvePackageAssets NullReferenceException buried in the output.
    # Clearing obj/ and bin/ makes restore regenerate them, so try once
    # before giving up.
    warn "Build produced no ModernUO.dll. Clearing stale build output and retrying once..."
    clear_build_artifacts
    ./publish.sh release linux x64 || true
  fi

  [[ -f "${DIST_DIR}/ModernUO.dll" ]] || die "Build produced no ModernUO.dll. Check output above."
  ok "Build artifacts at ${DIST_DIR}"
}

# ---------------------------------------------------------------------------
# Step 5b — Fix Felucca season
# ---------------------------------------------------------------------------
# ModernUO ships with Felucca's season set to 4 (Desolation) — a Renaissance-era
# lore choice that makes all trees on Felucca render leafless. For an offline
# single-player experience we want a leafy world year-round. Change to 1 (Summer).
#
# Idempotent: re-running on an already-fixed file is a no-op.
# ---------------------------------------------------------------------------
fix_felucca_season() {
 banner "Setting Felucca season to Summer"

 local mapdef="${MODERNUO_DIR}/Distribution/Data/map-definitions.json"

 if [[ ! -f "${mapdef}" ]]; then
   warn "map-definitions.json not found at ${mapdef}. Skipping season fix."
   return
 fi

 if grep -A 5 '"name": "Felucca"' "${mapdef}" | grep -q '"season": 1'; then
   say "Felucca already set to Summer. Skipping."
   return
 fi

 cp "${mapdef}" "${mapdef}.original"
 sed -i '/"name": "Felucca"/,/"rules"/ s/"season": 4/"season": 1/' "${mapdef}"

 if grep -A 5 '"name": "Felucca"' "${mapdef}" | grep -q '"season": 1'; then
   ok "Felucca season set to Summer (leafy trees)."
 else
   warn "Season fix may not have applied. Check ${mapdef} manually."
 fi
}

# ---------------------------------------------------------------------------
# Step 6 — UO game data: interactive detection
# ---------------------------------------------------------------------------
# Is this folder actually a usable UO data set? Checks for the handful of
# files ModernUO cannot run without, accepting either the classic .mul name
# or the newer UOP-packed equivalent (clients since ~7.0.60 ship art and map
# data as .uop files instead of the old .mul ones).
#
# Returns 0 if the folder looks usable, 1 otherwise (reason on stderr via
# the global UO_DATA_PROBLEM).
# ---------------------------------------------------------------------------
uo_data_problem() {
  local dir="$1"
  local group name
  UO_DATA_PROBLEM=""

  for group in \
    "tiledata.mul tiledataLegacyMUL.uop" \
    "map0.mul map0LegacyMUL.uop" \
    "art.mul artLegacyMUL.uop"
  do
    local found=0
    for name in ${group}; do
      if [[ -s "${dir}/${name}" ]]; then
        found=1
        break
      fi
    done
    if [[ "${found}" -eq 0 ]]; then
      UO_DATA_PROBLEM="none of these were found (or they're empty): ${group}"
      return 1
    fi
  done

  return 0
}

# Common locations an existing UO client install shows up in.
uo_data_candidates() {
  cat <<CANDIDATES
${HOME}/.steam/steam/steamapps/compatdata/*/pfx/drive_c/Program Files (x86)/Electronic Arts/Ultima Online Classic
${HOME}/Games/Ultima Online Classic
${HOME}/Ultima Online Classic
${HOME}/Desktop/Electronic Arts/Ultima Online Classic
${HOME}/Desktop/Ultima Online Classic
${HOME}/Documents/Ultima Online Classic
${HOME}/.wine/drive_c/Program Files/EA Games/Ultima Online Classic
${HOME}/.wine/drive_c/Program Files (x86)/Electronic Arts/Ultima Online Classic
${UO_DATA_DIR}
/mnt/uo
CANDIDATES
}

autodetect_uo_data() {
  local pattern c
  while IFS= read -r pattern; do
    [[ -n "${pattern}" ]] || continue
    for c in ${pattern}; do
      [[ -d "${c}" ]] || continue
      if uo_data_problem "${c}"; then
        UO_DATA="${c}"
        return 0
      fi
    done
  done < <(uo_data_candidates)
  return 1
}

# copy_uo_data — full recursive copy of the UO client tree at $1 into
# ${INSTALL_ROOT}/uo-data (UO_DATA_DIR). No symlinking: ModernUO's data
# directory ends up a real, self-contained copy inside the workspace.
#
# Copies everything, not a fixed extension allow-list. A filtered copy has
# already silently broken things once (cliloc.* wasn't in the original
# pattern, so Localization.GetText() always came back null - see
# patches/0006) and the same shape of bug is just as possible for any other
# file ModernUO or a future feature reads that doesn't happen to end in
# .mul/.uop/.idx/.def - music, fonts, gump art, speech tables, extra
# language files. Simplest way to stop guessing the list is to not filter
# at all.
#
# "${src}/." (not "${src}") copies the directory's *contents* into
# UO_DATA_DIR - without that trailing "/.", cp would nest the whole client
# folder one level deeper (UO_DATA_DIR/UOClient/...) instead of landing its
# files directly in UO_DATA_DIR. "${1%/}" strips any trailing slash the
# caller passed first, so this is correct either way. -a preserves
# permissions/timestamps/symlinks and recurses through every subdirectory.
#
# Purely additive: nothing already in UO_DATA_DIR is removed first, so a
# re-run (re-install, or pointing at a newer client) overwrites same-path
# files in place and leaves everything else - including
# _backup-modern-map/, which swap_t2a_map keeps inside this same
# directory - untouched.
# ---------------------------------------------------------------------------
copy_uo_data() {
  local src="${1%/}"
  mkdir -p "${UO_DATA_DIR}"

  # Already exactly the target location (the default "no data yet" path
  # has the operator drop files straight into UO_DATA_DIR) - nothing to copy.
  if [[ "$(cd "${src}" 2>/dev/null && pwd)" == "$(cd "${UO_DATA_DIR}" && pwd)" ]]; then
    UO_DATA="${UO_DATA_DIR}"
    return
  fi

  if [[ ! -d "${src}" ]]; then
    warn "UO data source ${src} is not a directory; nothing copied."
    return
  fi

  say "Copying full UO client tree: ${src} -> ${UO_DATA_DIR}"

  # A whole client directory can contain a stray unreadable/broken-symlink
  # file (permissions, a dead Wine prefix link); one bad file shouldn't
  # abort the entire install under set -e, so this is best-effort.
  if ! cp -a "${src}/." "${UO_DATA_DIR}/"; then
    warn "cp reported errors copying some files from ${src} - continuing with what did copy."
  fi

  local count
  count="$(find "${UO_DATA_DIR}" -type f 2>/dev/null | wc -l)"
  ok "Copied ${count} file(s) into ${UO_DATA_DIR}."
  UO_DATA="${UO_DATA_DIR}"
}

# ---------------------------------------------------------------------------
# resolve_uo_data — interactive UO data-file routine.
#
# ModernUO needs original UO data files (.mul / .uop) to load maps, statics
# and tile data. This installer does not, and will not, download them: the
# game files are © Electronic Arts. Instead:
#
#   1. Try the common install locations silently first.
#   2. If nothing turns up, ask whether the operator already has a client
#      or data files somewhere, and either take a path from them or point
#      them at https://uo.com/download and wait.
#
# Either way, the matching files end up physically copied into UO_DATA_DIR
# (see copy_uo_data) - a real copy inside the workspace, not a symlink out
# to wherever the original client lives.
# ---------------------------------------------------------------------------
resolve_uo_data() {
  banner "Locating UO game data"

  if autodetect_uo_data; then
    ok "Found existing UO data: ${UO_DATA}"
    copy_uo_data "${UO_DATA}"
    return
  fi

  say "No existing UO data found in the usual locations."
  echo ""

  local answer
  read -r -p "Do you already have an existing Ultima Online client installed or data files available? (y/n): " answer

  case "${answer}" in
    [Yy]*)
      while true; do
        read -r -p "Enter the absolute path to your UO client/data directory: " UO_DATA
        UO_DATA="${UO_DATA/#\~/${HOME}}"

        if [[ "${UO_DATA}" != /* ]]; then
          warn "That's not an absolute path. Try again (e.g. /home/you/UltimaOnline)."
          continue
        fi
        if [[ ! -d "${UO_DATA}" ]]; then
          warn "${UO_DATA} is not a directory."
          continue
        fi
        if uo_data_problem "${UO_DATA}"; then
          ok "UO data looks good: ${UO_DATA}"
          break
        fi
        warn "That folder doesn't look like a complete UO data set:"
        warn "  ${UO_DATA_PROBLEM}"
        read -r -p "Try a different path? (y/n): " retry
        [[ "${retry}" =~ ^[Yy] ]] || die "No usable UO data directory given. Re-run this installer once you have one."
      done
      ;;
    *)
      mkdir -p "${UO_DATA_DIR}"
      echo ""
      say "Please download and install the official classic client from:"
      say "    https://uo.com/download"
      say "(natively, or via Wine, or on another machine — however is easiest)"
      say "then copy the game's .mul / .uop data files into:"
      say "    ${UO_DATA_DIR}"
      echo ""
      while true; do
        read -r -p "Press Enter once the files are there, or type 'exit' to quit and finish this later: " response
        if [[ "${response}" == "exit" ]]; then
          say "Exiting. Re-run this installer once the UO data files are in ${UO_DATA_DIR}."
          exit 0
        fi
        if uo_data_problem "${UO_DATA_DIR}"; then
          UO_DATA="${UO_DATA_DIR}"
          ok "UO data looks good: ${UO_DATA}"
          break
        fi
        warn "Nothing usable in ${UO_DATA_DIR} yet:"
        warn "  ${UO_DATA_PROBLEM}"
      done
      ;;
  esac

  copy_uo_data "${UO_DATA}"
}

# ---------------------------------------------------------------------------
# Step 6b — Swap in genuine T2A-era Felucca map art
#
# The UO data dir feeds the server directly, so swapping map0/statics0/
# staidx0 here updates server-side collision/spawn with no client to desync
# from. radarcol/tiledata are left as-is (stable across eras). Fully
# reversible — the original files are backed up to _backup-modern-map/
# first. See docs/T2A-MAP.md.
# ---------------------------------------------------------------------------
swap_t2a_map() {
  banner "Installing T2A-era map art"
  [[ "${INSTALL_T2A_MAP}" == "1" ]] || { say "INSTALL_T2A_MAP off; keeping the map art you supplied."; return; }
  [[ -n "${UO_DATA:-}" ]]           || { warn "UO data dir not resolved; skipping T2A map swap."; return; }

  local backup_dir="${UO_DATA}/_backup-modern-map"
  if [[ -f "${backup_dir}/map0.mul" ]]; then
    say "T2A map already swapped (backup exists). Skipping."
    return
  fi

  if [[ ! -f "${UO_DATA}/map0.mul" ]] || [[ ! -f "${UO_DATA}/statics0.mul" ]] || [[ ! -f "${UO_DATA}/staidx0.mul" ]]; then
    warn "Your UO data uses .uop map files, not classic .mul ones; skipping T2A map swap (it only patches .mul)."
    return
  fi

  command -v 7z >/dev/null || { warn "7z not found; skipping T2A map swap (install p7zip and re-run)."; return; }

  # 1. Obtain the UOSA installer (cached so re-runs don't re-download ~349 MB).
  mkdir -p "${T2A_SRC_DIR}"
  local uosa_exe="${T2A_SRC_DIR}/UOSA_Client_Setup.exe"
  if [[ ! -f "${uosa_exe}" ]]; then
    say "Downloading UO Second Age client (~349 MB, EA content via uosecondage.com) for its T2A map art..."
    curl -fL --progress-bar \
      -A "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36" \
      -o "${uosa_exe}" "${T2A_INSTALLER_URL}"
  else
    say "UOSA installer already cached at ${uosa_exe}."
  fi

  # 2. Extract the three map files (7z reads the NSIS archive directly).
  local extract_dir="${T2A_SRC_DIR}/uosa-install"
  mkdir -p "${extract_dir}"
  say "Extracting T2A map files with 7z..."
  7z x -y "-o${extract_dir}" "${uosa_exe}" map0.mul statics0.mul staidx0.mul >/dev/null || true

  # Locate them (the NSIS layout may nest the files).
  local missing=0 f src
  declare -A src_path
  for f in map0.mul statics0.mul staidx0.mul; do
    src="$(find "${extract_dir}" -maxdepth 4 -name "${f}" -print -quit 2>/dev/null || true)"
    if [[ -z "${src}" ]]; then warn "T2A ${f} not found after extract."; missing=1; else src_path[${f}]="${src}"; fi
  done
  [[ "${missing}" == "0" ]] || { warn "Aborting T2A swap; your map art is kept."; return; }

  # 3. Back up the original files (the 3 swapped + radarcol/tiledata for safety).
  mkdir -p "${backup_dir}"
  for f in map0.mul statics0.mul staidx0.mul radarcol.mul tiledata.mul; do
    [[ -f "${UO_DATA}/${f}" ]] && cp -f "${UO_DATA}/${f}" "${backup_dir}/${f}"
  done
  ok "Backed up original map -> ${backup_dir}"

  # 4. Copy the T2A files over the live data dir.
  for f in map0.mul statics0.mul staidx0.mul; do
    cp -f "${src_path[${f}]}" "${UO_DATA}/${f}"
  done
  ok "T2A map art installed (intact Magincia). Revert: cp ${backup_dir}/* back over the data dir."
}

# ---------------------------------------------------------------------------
# Step 7 — Download Nerun's pre-T2A spawn map
# ---------------------------------------------------------------------------
fetch_spawn_map() {
  banner "Fetching Nerun's pre-T2A spawn map"

  mkdir -p "${SPAWNERS_DIR}"
  local target="${SPAWNERS_DIR}/UOClassic.map"

  if [[ -f "${target}" ]] && [[ -s "${target}" ]]; then
    say "Spawn map already present: ${target}"
    return
  fi

  say "Downloading from Nerun's repository..."
  curl -fL --progress-bar -o "${target}" "${SPAWN_MAP_URL}"

  # Sanity check: ensure we got the .map file, not a GitHub error page.
  if head -1 "${target}" | grep -qi '<!doctype\|<html'; then
    rm -f "${target}"
    die "Downloaded file looks like HTML, not a spawn map. Check ${SPAWN_MAP_URL}"
  fi

  ok "Spawn map: ${target} ($(wc -l < "${target}") lines)"
}

# ---------------------------------------------------------------------------
# Step 8 — Write ModernUO config (LAN listener, auto account creation)
# ---------------------------------------------------------------------------
write_modernuo_json() {
  # Keep a shard name that is already set. Renaming an existing shard makes
  # returning players' saved settings look like they belong to a different
  # server. Only a fresh install (or one missing this file) gets ours.
  #
  # The grep is allowed to find nothing - an existing modernuo.json that
  # predates this key, or one the server itself rewrote with a different
  # key set, is a normal case, not an error. Without the `|| true` here,
  # grep's exit 1 on no-match propagates through the pipeline (pipefail)
  # into the `local _prev=$(...)` assignment, and set -e treats that
  # assignment's failure as fatal - the function would die right here,
  # silently, on every install/update whose existing config just doesn't
  # happen to have this exact key yet.
  RESOLVED_SHARD_NAME="${SHARD_NAME}"
  local _cfg="${CFG_DIR}/modernuo.json"
  if [[ -f "${_cfg}" ]]; then
    local _prev=""
    _prev="$(grep -oE '"serverListing\.serverName"[[:space:]]*:[[:space:]]*"[^"]*"' "${_cfg}" 2>/dev/null \
      | head -n1 | sed -E 's/.*"([^"]*)"[[:space:]]*$/\1/' || true)"
    if [[ -n "${_prev}" ]]; then
      RESOLVED_SHARD_NAME="${_prev}"
      say "Keeping this install's existing shard name: ${_prev}"
    fi
  fi

  mkdir -p "${CFG_DIR}"

  # UO_DATA is only ever resolved (by resolve_uo_data / copy_uo_data)
  # during a fresh install; ensure_modernuo_config can also reach this
  # function from `update`, where nothing set it and set -u would
  # otherwise make the heredoc below die on an unbound variable. Fall
  # back to the fixed uo-data location every install already copies data
  # into (UO_DATA_DIR, set once at script start-up regardless of action).
  local _data_dir="${UO_DATA:-${UO_DATA_DIR}}"

  # modernuo.json — server runtime config.
  #
  # listeners binds to LISTEN_ADDR (0.0.0.0:2593 by default) so LAN clients
  # can connect directly. serverList.autoDetect makes the login handshake
  # echo back whatever address the client actually connected on — the
  # right behavior on a LAN box, where "the server's address" depends on
  # which interface/IP a given client reached it through, and a fixed
  # 127.0.0.1 would send every remote client back to their own machine.
  cat > "${_cfg}" <<EOF
{
  "assemblyDirectories": ["./Assemblies"],
  "dataDirectories": ["${_data_dir}"],
  "listeners": ["${LISTEN_ADDR}"],
  "settings": {
    "accountHandler.maxAccountsPerIP": "10",
    "autosave.enabled": "true",
    "autosave.saveDelay": "00:05:00",
    "serverList.autoDetect": "true",
    "serverListing.name": "${RESOLVED_SHARD_NAME}",
    "serverListing.serverName": "${RESOLVED_SHARD_NAME}",
    "accountHandler.enableAutoAccountCreation": "True",
    "pathfinding.prebakeMaps": "True",
    "network.sendBufferSize": "2097152"
  }
}
EOF
  ok "Wrote modernuo.json (listening on ${LISTEN_ADDR}, auto account creation on)"
}

write_expansion_json() {
  # expansion.json — the REAL schema, capitalized keys, all flags spelled out.
  # T2A gets Felucca map only, ExpansionT2A flag on, LiveAccount on.
  mkdir -p "${CFG_DIR}"
  cat > "${CFG_DIR}/expansion.json" <<EOF
{
  "Id": ${EXPANSION_ID},
  "ClientFlags": "None",
  "SupportedFeatures": {
    "ExpansionT2A": true,
    "T2A": true,
    "UOR": false,
    "UOTD": false,
    "LBR": false,
    "AOS": false,
    "SixthCharacterSlot": false,
    "SE": false,
    "ML": false,
    "EighthAge": false,
    "NinthAge": false,
    "TenthAge": false,
    "IncreasedStorage": false,
    "SeventhCharacterSlot": false,
    "RoleplayFaces": false,
    "TrialAccount": false,
    "LiveAccount": true,
    "SA": false,
    "HS": false,
    "Gothic": false,
    "Rustic": false,
    "Jungle": false,
    "Shadowguard": false,
    "TOL": false,
    "EJ": false
  },
  "CharacterListFlags": {
    "Unk1": false,
    "OverwriteConfigButton": false,
    "OneCharacterSlot": false,
    "ExpansionNone": false,
    "ExpansionUOTD": false,
    "ExpansionLBR": false,
    "ExpansionT2A": true,
    "ExpansionUOR": false,
    "ContextMenus": true,
    "SlotLimit": false,
    "AOS": false,
    "SixthCharacterSlot": false,
    "SE": false,
    "ML": false,
    "KR": false,
    "UO3DClientType": false,
    "Unk3": false,
    "SeventhCharacterSlot": false,
    "Unk4": false,
    "NewMovementSystem": false,
    "NewFeluccaAreas": false
  },
  "HousingFlags": {
    "AOS": false,
    "HousingAOS": false,
    "SE": false,
    "ML": false,
    "Crystal": false,
    "SA": false,
    "HS": false,
    "Gothic": false,
    "Rustic": false,
    "Jungle": false,
    "Shadowguard": false,
    "TOL": false,
    "EJ": false
  },
  "MobileStatusVersion": 0,
  "MapSelectionFlags": {
    "Felucca": true,
    "Trammel": false,
    "Ilshenar": false,
    "Malas": false,
    "Tokuno": false,
    "TerMur": false
  }
}
EOF
  ok "Wrote expansion.json (T2A, Felucca-only)"
}

write_feature_flags_json() {
  # FeatureFlags/flags.json - the Young player system is a UO:R-era feature
  # that did not exist in T2A. Left on, young characters also get a
  # Trammel-only public moongate list, which filters down to nothing on this
  # Felucca-only shard and makes the city moongates silently do nothing for
  # every non-staff player.
  mkdir -p "${CFG_DIR}/FeatureFlags"
  cat > "${CFG_DIR}/FeatureFlags/flags.json" <<'EOF'
[
  {
    "Key": "young_player_system",
    "Description": "UO:R-era new player (Young) system. Off for T2A: no (Young) name suffix, no young monster protection, no Haven transport, no New Player Ticket, and no Trammel-only public moongate list.",
    "Enabled": false,
    "DefaultEnabled": true,
    "Category": "Content",
    "LastModified": "2026-08-23T00:00:00Z",
    "LastModifiedBy": "T2A ruleset"
  }
]
EOF
  ok "Wrote FeatureFlags/flags.json (Young player system off - not a T2A feature)"
}

# All three configs, unconditionally. Used by a fresh install, where
# there's nothing yet to preserve.
write_modernuo_config() {
  banner "Writing ModernUO configuration"
  write_modernuo_json
  write_expansion_json
  write_feature_flags_json
}

# Fills in only what's actually missing. Used by `update`, which otherwise
# deliberately never touches Configuration/ (see do_update) - this exists
# for the one case that isn't "leave it alone": a config directory that's
# missing a file outright (an interrupted previous run, a manual delete,
# a still-default install this repo's own history has hit) would
# otherwise leave the server stuck at a first-run wizard prompt instead of
# booting headless. An existing file, however incomplete its keys, is left
# untouched - regenerating it wholesale is exactly the clobber do_update
# promises not to do.
ensure_modernuo_config() {
  local wrote_any=0

  if [[ ! -f "${CFG_DIR}/modernuo.json" ]]; then
    banner "Regenerating missing ModernUO configuration"
    write_modernuo_json
    wrote_any=1
  fi

  if [[ ! -f "${CFG_DIR}/expansion.json" ]]; then
    [[ "${wrote_any}" == "1" ]] || banner "Regenerating missing ModernUO configuration"
    write_expansion_json
    wrote_any=1
  fi

  if [[ ! -f "${CFG_DIR}/FeatureFlags/flags.json" ]]; then
    [[ "${wrote_any}" == "1" ]] || banner "Regenerating missing ModernUO configuration"
    write_feature_flags_json
    wrote_any=1
  fi

  if [[ "${wrote_any}" == "0" ]]; then
    say "Configuration/ already has modernuo.json, expansion.json, and FeatureFlags/flags.json. Leaving them as they are."
  fi
}

# A narrow, deliberate exception to ensure_modernuo_config's "existing file,
# however incomplete, is left untouched" rule above: this one key's engine
# default (262144 bytes / 256KB, DefaultSendBufferSize in
# NetState.Network.cs) is too small for OrganicMarket's world-seeding tools
# (Scripts/Custom/OrganicMarket/WorldHouseSeeder.cs), which can burst enough
# multi/item/mobile update packets to a nearby GM to exhaust it and
# disconnect the client ("send buffer exhausted" in the server log). A
# config file that predates this key (the server's own
# ServerConfiguration.GetOrUpdateSetting writes the 256KB default back into
# modernuo.json the first time anything reads the setting, same as any
# other setting) gets it raised in place; a value already at or above the
# new floor - an operator's own deliberate setting - is left alone.
ensure_send_buffer_size() {
  local target=2097152 # 2 MB, a power of two (required - see NetState.Network.cs)
  local cfg="${CFG_DIR}/modernuo.json"
  [[ -f "${cfg}" ]] || return 0

  local current
  current="$(grep -oE '"network\.sendBufferSize"[[:space:]]*:[[:space:]]*"[0-9]+"' "${cfg}" 2>/dev/null \
    | grep -oE '[0-9]+' | head -n1 || true)"

  if [[ -z "${current}" ]]; then
    sed -i "s/\"settings\": {/\"settings\": {\n    \"network.sendBufferSize\": \"${target}\",/" "${cfg}"
    ok "Set network.sendBufferSize to ${target} (2 MB) in modernuo.json"
  elif [[ "${current}" -lt "${target}" ]]; then
    sed -i -E "s/(\"network\.sendBufferSize\"[[:space:]]*:[[:space:]]*\")[0-9]+(\")/\1${target}\2/" "${cfg}"
    ok "Raised network.sendBufferSize from ${current} to ${target} (2 MB) in modernuo.json"
  else
    say "network.sendBufferSize already ${current} (>= ${target}); leaving as-is."
  fi
}

# ---------------------------------------------------------------------------
# Step 9 — Install runtime scripts
# ---------------------------------------------------------------------------
install_runtime_scripts() {
  banner "Installing launcher scripts"

  local src_dir="${SCRIPT_DIR}/scripts"
  [[ -d "${src_dir}" ]] || die "Cannot find scripts directory at ${src_dir}"
  [[ -f "${src_dir}/start-server.sh" ]] || die "Cannot find ${src_dir}/start-server.sh"

  cp "${src_dir}/start-server.sh"       "${INSTALL_ROOT}/start.sh"
  cp "${src_dir}/stop.sh"               "${INSTALL_ROOT}/stop.sh"
  cp "${src_dir}/reset-first-launch.sh" "${INSTALL_ROOT}/reset-first-launch.sh"

  # The launcher's update checker is optional - an install without it just
  # never offers updates, which is the quiet way to fail. Deploy the
  # headless server variant (no GUI dialogs, no interactive terminal
  # prompt, no self-driven download - it only ever prints a one-line
  # notice and returns) under the same update-check.sh name start.sh
  # looks for, so the desktop checker's own dialog/prompt logic never
  # runs on a server install.
  if [[ -f "${src_dir}/update-check-server.sh" ]]; then
    cp "${src_dir}/update-check-server.sh" "${INSTALL_ROOT}/update-check.sh"
    chmod +x "${INSTALL_ROOT}/update-check.sh"
    ok "Installed update-check.sh (headless variant)"
  fi

  write_version_stamp

  chmod +x "${INSTALL_ROOT}/start.sh" \
           "${INSTALL_ROOT}/stop.sh" \
           "${INSTALL_ROOT}/reset-first-launch.sh"

  ok "Installed start.sh, stop.sh, reset-first-launch.sh"
}

# ---------------------------------------------------------------------------
# Version stamp - what the launcher's update check compares against.
#
# Prefer the git sha of the source we are installing FROM, because that is
# exactly what the player has on disk. Downloaded zips carry no sha, so for
# those we fall back to the current branch head, which is accurate to within
# however long ago the zip was downloaded.
#
# Failing to write this is not an install failure. It only means the launcher
# will not offer updates, which is the quiet, safe direction to fail in.
# ---------------------------------------------------------------------------
write_version_stamp() {
  local repo="Klein187/uo-offline"
  local branch="main"
  local sha=""
  local api="https://api.github.com/repos/${repo}/commits/${branch}"

  if command -v git >/dev/null 2>&1 && [[ -d "${SCRIPT_DIR}/.git" ]]; then
    sha="$(git -C "${SCRIPT_DIR}" rev-parse HEAD 2>/dev/null || true)"
  fi

  if [[ -z "${sha}" ]] && command -v curl >/dev/null 2>&1; then
    sha="$(curl -fsSL --max-time 10 -H "User-Agent: uo-offline-installer" "${api}" 2>/dev/null | grep -oE '"sha"[[:space:]]*:[[:space:]]*"[0-9a-f]{40}"' | head -n1 | grep -oE '[0-9a-f]{40}' || true)"
  fi

  if [[ -z "${sha}" ]]; then
    warn "Could not determine the source version; the launcher will not check for updates."
    return 0
  fi

  cat > "${INSTALL_ROOT}/uo-offline-version.json" <<EOF
{
  "Repo": "${repo}",
  "Branch": "${branch}",
  "Sha": "${sha}",
  "InstalledUtc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
EOF
  ok "Version stamp: ${sha:0:7}"
}

# ---------------------------------------------------------------------------
# Step 10 — Mark for first-launch wizard
# ---------------------------------------------------------------------------
arm_first_launch() {
  touch "${INSTALL_ROOT}/.needs-owner-account"
  ok "Owner account will be created on first launch: ${OWNER_USER} / ${OWNER_PASS}"
}

# ---------------------------------------------------------------------------
# Step 10b — Drop a world-population cheat sheet next to start.sh
# ---------------------------------------------------------------------------
install_cheatsheet() {
  cat > "${INSTALL_ROOT}/POPULATE-WORLD.txt" <<'EOF'
After your first character is created and you're standing in Britannia,
the world will be empty — no NPCs, no signs, no monsters. To populate it,
open the in-game chat and type these six commands, one at a time.

Each command takes a few seconds and prints a progress message in chat.

  [Decorate
       Places fences, lamp posts, walls, plants, ~55,000 decoration items.

  [SignGen
       Hangs shop signs on all the buildings.

  [TelGen
       Places teleporters between cities and dungeons.

  [MoonGen
       Places the public moongate network (the blue swirly portals).
       One in each major city. Double-click to fast travel.

  [TownCriers
       Spawns town crier NPCs (the ones that read announcements).

  [GenerateSpawners Spawners/uoclassic/UOClassic.map
       The big one. Spawns ~1700 spawn points across Britannia: orcs in
       the orc fort, deer in forests, dragons in dungeons, vendors in
       every town. Takes about 3 seconds. This is the moment the world
       comes alive.

You only do this once. The state saves with the world and persists
forever. If you ever want to start fresh, run reset-first-launch.sh and
the world goes back to empty — then redo these commands.

Tip: type [help in-game for the full command list. Useful admin commands:

  [where           Show your X/Y/Z coordinates.
  [go britain      Teleport to Britain's center.
  [go destard      Teleport to a dragon dungeon.
  [m               Toggle GM movement (walk through walls).
  [invul           Toggle invulnerability.
  [password new    Change your admin password.
EOF
  ok "World-population cheat sheet: ${INSTALL_ROOT}/POPULATE-WORLD.txt"
}

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------
finish() {
  banner "Install complete"

  local lan_ip
  lan_ip="$(hostname -I 2>/dev/null | awk '{print $1}')"

  cat <<EOF

Install root:   ${INSTALL_ROOT}
Server:         ${DIST_DIR}
UO data:        ${UO_DATA}
Expansion:      ${EXPANSION_NAME} (id=${EXPANSION_ID})
Listener:       ${LISTEN_ADDR}  (LAN — reachable from other machines)
Owner login:    ${OWNER_USER} / ${OWNER_PASS}

To start:       ${INSTALL_ROOT}/start.sh
                (runs in the foreground; Ctrl-C stops it and saves the world)
To stop:        ${INSTALL_ROOT}/stop.sh   (from another terminal)

LAN clients (running ClassicUO or the official client on their own
machine) connect to:
EOF
  if [[ -n "${lan_ip}" ]]; then
    echo "    ${lan_ip}:2593"
  else
    echo "    <this machine's LAN IP>:2593  (run 'hostname -I' to find it)"
  fi
  cat <<EOF

Security note: the server is bound to 0.0.0.0, so anything on your LAN can
reach it, and account creation is automatic. Put it behind a firewall/router
that only your LAN can reach, and change the owner password after logging
in the first time ([password new in-game).

First launch flow:
  1. Run start.sh. The server starts and the owner account is created
     automatically (~10s), then the process waits in the foreground.
  2. From a client machine, connect to the address above and log in:
     ${OWNER_USER} / ${OWNER_PASS}.
  3. Create a character, pick a starting city, enter the world.
  4. The world is empty at first. To populate it, read:
       ${INSTALL_ROOT}/POPULATE-WORLD.txt
     and run the six [-commands shown there in chat.
  5. Done. World state saves automatically every 5 minutes.

EOF
}

# ---------------------------------------------------------------------------
# Step 4c — Map editor: install the browser-based waypoint/zone/arrival editor
# ---------------------------------------------------------------------------
install_map_editor() {
  banner "Installing map editor"

  if [[ "${INSTALL_MAP_EDITOR}" != "1" ]]; then
    say "Skipped by choice (--no-map-editor)."
    return
  fi

  local src_dir="${SCRIPT_DIR}/tools/map"
  if [[ ! -d "${src_dir}" ]]; then
    say "No tools/map/ in repo; skipping map editor (optional)."
    return
  fi

  if ! command -v python3 >/dev/null; then
    warn "The map editor needs python3, which is not installed. Skipping."
    warn "Install python3 and re-run to get it."
    return
  fi

  local map_dir="${INSTALL_ROOT}/map-editor"
  mkdir -p "${map_dir}"

  # Everything but the debris a working checkout collects.
  local f
  for f in "${src_dir}"/*; do
    case "$(basename "${f}")" in
      __pycache__|*.bak-*) continue ;;
    esac
    cp -r "${f}" "${map_dir}/"
  done

  # Generated, not copied: it has to know where this install actually is, and
  # serve_map.py reads both roots from the environment.
  cat > "${map_dir}/uo-map-launch.sh" <<EOF
#!/bin/bash
# Starts the map editor server if it is not already up, then opens it.
export UO_MAP_DIR="${map_dir}"
export UO_SHARD_ROOT="${INSTALL_ROOT}"
URL="http://localhost:8777/map.html"
LOG="${map_dir}/serve_map.log"

if ! curl -s -o /dev/null --max-time 1 "\${URL}"; then
    nohup python3 "${map_dir}/serve_map.py" >"\${LOG}" 2>&1 &
    for _ in \$(seq 1 10); do
        sleep 0.5
        curl -s -o /dev/null --max-time 1 "\${URL}" && break
    done
fi

xdg-open "\${URL}"
EOF
  chmod +x "${map_dir}/uo-map-launch.sh"

  say "Map editor installed to ${map_dir}."
  say "Run ${map_dir}/uo-map-launch.sh to serve it on http://localhost:8777"
  ok "Map editor ready."
}

# ---------------------------------------------------------------------------
# Step 4b — PlayerBots: deploy bot source files into the ModernUO source tree
#
# This runs BEFORE build_modernuo so the bot code is compiled into the same
# build pass. The bot files live in this repo at ./playerbots/.
# ---------------------------------------------------------------------------
# ---------------------------------------------------------------------------
# Engine patches.
#
# Two stock ModernUO files need a small change for the bots to work
# properly, and they cannot live in CustomBots/ because they ARE engine
# files. They ship as unified diffs under patches/ and go on with
# git apply, which every install already has because it clones ModernUO.
#
# Never fatal. An upstream that has moved on will refuse a patch, and a
# shard missing them still runs - it just loses bot housing across
# restarts. INTEGRATION-NOTES.txt describes both by hand.
# ---------------------------------------------------------------------------
apply_engine_patches() {
  banner "Applying engine patches"

  local patch_dir="${SCRIPT_DIR}/patches"
  if [[ ! -d "${patch_dir}" ]]; then
    say "No patches directory; nothing to apply."
    return 0
  fi

  shopt -s nullglob
  local patches=("${patch_dir}"/*.patch)
  shopt -u nullglob

  if [[ ${#patches[@]} -eq 0 ]]; then
    say "No patches to apply."
    return 0
  fi

  local patch name
  for patch in "${patches[@]}"; do
    name="$(basename "${patch}")"

    # Already applied? Reversing it cleanly is the test.
    if git -C "${MODERNUO_DIR}" apply --reverse --check "${patch}" 2>/dev/null; then
      ok "${name} (already applied)"
      continue
    fi

    if ! git -C "${MODERNUO_DIR}" apply --check "${patch}" 2>/dev/null; then
      warn "${name} does not apply to this ModernUO checkout - skipping."
      warn "See INTEGRATION-NOTES.txt if you need it applied by hand."
      continue
    fi

    git -C "${MODERNUO_DIR}" apply "${patch}"
    ok "${name} applied"
  done
}

install_playerbots() {
  banner "Installing PlayerBots"

  local src_dir="${SCRIPT_DIR}/playerbots"
  if [[ ! -d "${src_dir}" ]]; then
    warn "No playerbots/ directory next to this installer; skipping bot install."
    return
  fi

  local src_target="${MODERNUO_DIR}/Projects/UOContent/CustomBots"

  # Hash the source we're about to deploy so we know whether to force a
  # rebuild. If the hash matches what's already deployed, skip the touch
  # of ModernUO.dll so build_modernuo can skip cleanly.
  local new_hash
  new_hash="$(find "${src_dir}/source" "${src_dir}/data" -type f -exec sha256sum {} + 2>/dev/null \
    | sort | sha256sum | cut -d' ' -f1)"
  local hash_file="${src_target}/.deployed-hash"
  local prev_hash=""
  [[ -f "${hash_file}" ]] && prev_hash="$(cat "${hash_file}")"

  if [[ -d "${src_target}" && "${new_hash}" == "${prev_hash}" ]]; then
    say "PlayerBot sources unchanged. Skipping deploy."
    return
  fi

  say "Deploying bot source -> ${src_target}"
  mkdir -p "${src_target}"
  cp -rT "${src_dir}/source/CustomBots" "${src_target}"
  echo "${new_hash}" > "${hash_file}"

  # Deploy every bot data directory present in the repo. The bots need
  # Destinations (where to go), Waypoints (the road graph), Zones (painted
  # areas + portals for arrival), Navigation (field caches), and
  # PlayerBotChat (speech lines). Whole-dir copy so new dirs are picked up
  # automatically.
  for sub in Destinations Waypoints Zones PlayerBotChat; do
    if [[ -d "${src_dir}/data/${sub}" ]]; then
      say "Deploying ${sub} -> ${DIST_DIR}/Data/${sub}"
      mkdir -p "${DIST_DIR}/Data/${sub}"
      cp -rT "${src_dir}/data/${sub}" "${DIST_DIR}/Data/${sub}"
    fi
  done
  # Navigation/fields_cache.bin is a generated distance-field cache; the
  # bots rebuild it on first run. Not shipped (would be stale for a fresh
  # world). Just ensure the dir exists for them to write into.
  mkdir -p "${DIST_DIR}/Data/Navigation"

  # Clean up any legacy files from older bot system versions
  local legacy_files=(
    "${src_target}/Behaviors/RouteRegistry.cs"
    "${src_target}/Behaviors/ReloadRoutesCommand.cs"
    "${src_target}/Behaviors/DestinationRegistry.cs"
  )
  for f in "${legacy_files[@]}"; do
    [[ -f "$f" ]] && rm -f "$f"
  done

  local legacy_dirs=(
    "${DIST_DIR}/Data/Routes"
  )
  for d in "${legacy_dirs[@]}"; do
    [[ -d "$d" ]] && rm -rf "$d"
  done

  # Force a rebuild on next build_modernuo by removing the marker file.
  if [[ -f "${DIST_DIR}/ModernUO.dll" ]]; then
    say "Bot sources changed — clearing build cache to trigger rebuild"
    rm -f "${DIST_DIR}/ModernUO.dll"
  fi

  ok "PlayerBots deployed (will be compiled by the next ModernUO build)"
}

# ---------------------------------------------------------------------------
# Organic Market admin tool: deploy Scripts/Custom/OrganicMarket/ into the
# ModernUO source tree. The path is a straight copy under Projects/UOContent/
# (an SDK-style csproj compiles any .cs file under its own directory by
# default, same as CustomBots), so no .csproj edit and no core-file edit is
# needed for it to build.
# ---------------------------------------------------------------------------
install_organicmarket() {
  banner "Installing Organic Market admin tool"

  local src_dir="${SCRIPT_DIR}/Scripts/Custom/OrganicMarket"
  if [[ ! -d "${src_dir}" ]]; then
    say "No Scripts/Custom/OrganicMarket/ next to this installer; skipping (optional)."
    return
  fi

  local dest_dir="${MODERNUO_DIR}/Projects/UOContent/Scripts/Custom/OrganicMarket"
  local changed=0
  local new_hash prev_hash="" hash_file="${dest_dir}/.deployed-hash"

  new_hash="$(find "${src_dir}" -type f -exec sha256sum {} + 2>/dev/null | sort | sha256sum | cut -d' ' -f1)"
  [[ -f "${hash_file}" ]] && prev_hash="$(cat "${hash_file}")"

  if [[ -d "${dest_dir}" && "${new_hash}" == "${prev_hash}" ]]; then
    say "Organic Market source unchanged. Skipping deploy."
    return
  fi

  # Sync, not just copy-additive: a file removed/renamed on the source
  # side (e.g. a class replaced by a new one) has to disappear from the
  # deployed copy too, or the stale copy keeps compiling alongside its
  # replacement - which either duplicates a type or, worse, still calls
  # an old method signature that no longer exists.
  mkdir -p "${dest_dir}"
  find "${dest_dir}" -maxdepth 1 -name '*.cs' -delete
  cp -f "${src_dir}"/*.cs "${dest_dir}/"
  echo "${new_hash}" > "${hash_file}"
  changed=1

  if [[ "${changed}" == "1" ]] && [[ -f "${DIST_DIR}/ModernUO.dll" ]]; then
    say "Organic Market source changed — clearing build cache to trigger rebuild"
    rm -f "${DIST_DIR}/ModernUO.dll"
  fi

  ok "Organic Market admin tool deployed -> ${dest_dir}"
}

# ---------------------------------------------------------------------------
# Lifecycle: install
# ---------------------------------------------------------------------------
do_install() {
  if [[ -d "${INSTALL_ROOT}" ]]; then
    warn "${INSTALL_ROOT} already exists."
    if [[ "${FORCE}" != "1" ]]; then
      local answer
      read -r -p "Continue installing into it anyway? Existing files may be reused or overwritten. [y/N]: " answer
      [[ "${answer}" =~ ^[Yy] ]] || { say "Aborted. Nothing changed."; exit 0; }
    fi
  fi

  preflight
  install_deps
  fetch_modernuo
  bootstrap_dotnet
  apply_engine_patches
  install_playerbots
  install_organicmarket
  install_map_editor
  build_modernuo
  fix_felucca_season
  resolve_uo_data
  swap_t2a_map
  fetch_spawn_map
  write_modernuo_config
  ensure_send_buffer_size
  install_runtime_scripts
  arm_first_launch
  install_cheatsheet
  finish
}

# ---------------------------------------------------------------------------
# Lifecycle: update
#
# Rebuilds ModernUO and PlayerBots and refreshes the launcher scripts, but
# never touches uo-data/ (no resolve_uo_data / copy_uo_data call at all)
# and defends Configuration/ and Saves/ around the rebuild: both are moved
# aside before fetch/build and moved back afterward, so nothing a fresh
# `dotnet publish` drops into Distribution/ can clobber a hand-edited
# modernuo.json, existing accounts, or world state.
# ---------------------------------------------------------------------------
do_update() {
  [[ -d "${INSTALL_ROOT}" ]] || die "No install found at ${INSTALL_ROOT}. Run: $(basename "$0") install"

  banner "Updating UO Offline server"
  preflight
  install_deps

  local backup_dir cfg_backed_up=0 saves_backed_up=0
  backup_dir="$(mktemp -d "${INSTALL_ROOT}/.update-backup.XXXXXX")"

  if [[ -d "${CFG_DIR}" ]]; then
    mv "${CFG_DIR}" "${backup_dir}/Configuration"
    cfg_backed_up=1
  fi
  if [[ -d "${DIST_DIR}/Saves" ]]; then
    mv "${DIST_DIR}/Saves" "${backup_dir}/Saves"
    saves_backed_up=1
  fi

  fetch_modernuo
  bootstrap_dotnet
  apply_engine_patches
  install_playerbots
  install_organicmarket

  say "Forcing a rebuild against the updated source..."
  rm -f "${DIST_DIR}/ModernUO.dll"
  build_modernuo
  fix_felucca_season

  if [[ "${cfg_backed_up}" == "1" ]]; then
    rm -rf "${CFG_DIR}"
    mv "${backup_dir}/Configuration" "${CFG_DIR}"
    ok "Restored Configuration/ (modernuo.json and friends untouched)."
  fi
  if [[ "${saves_backed_up}" == "1" ]]; then
    rm -rf "${DIST_DIR}/Saves"
    mv "${backup_dir}/Saves" "${DIST_DIR}/Saves"
    ok "Restored Saves/ (world state and accounts untouched)."
  fi
  rmdir "${backup_dir}" 2>/dev/null || rm -rf "${backup_dir}"

  # Fills in modernuo.json/expansion.json/FeatureFlags/flags.json only if
  # one is actually missing (never true after a normal restore above) -
  # a safety net against a config directory that never had all three to
  # begin with, not a way around the "never clobber hand edits" promise.
  ensure_modernuo_config
  ensure_send_buffer_size

  fetch_spawn_map
  install_runtime_scripts
  install_cheatsheet

  banner "Update complete"
  ok "ModernUO and PlayerBots rebuilt. uo-data/, Configuration/, and Saves/ were left as they were."
  say "Run ${INSTALL_ROOT}/start.sh to launch the updated server."
}

# ---------------------------------------------------------------------------
# Lifecycle: uninstall
# ---------------------------------------------------------------------------
do_uninstall() {
  banner "Uninstall"

  if [[ ! -d "${INSTALL_ROOT}" ]]; then
    say "Nothing to remove: ${INSTALL_ROOT} does not exist."
    return
  fi

  if [[ "${FORCE}" != "1" ]]; then
    local answer
    read -r -p "Are you sure you want to remove ${INSTALL_ROOT}? [y/N]: " answer
    [[ "${answer}" =~ ^[Yy] ]] || { say "Aborted. Nothing removed."; exit 0; }
  fi

  if [[ -x "${INSTALL_ROOT}/stop.sh" ]]; then
    say "Stopping the server first..."
    "${INSTALL_ROOT}/stop.sh" || true
  fi

  rm -rf "${INSTALL_ROOT}"
  ok "Removed ${INSTALL_ROOT}."
}

# ---------------------------------------------------------------------------
# Interactive action menu, used only when no install|update|uninstall
# action was given on the command line.
# ---------------------------------------------------------------------------
resolve_action() {
  [[ -z "${ACTION}" ]] || return 0

  echo ""
  echo "What would you like to do?"
  echo "  1) install    - set up a new server in ./server-runtime"
  echo "  2) update     - rebuild ModernUO/PlayerBots; keep saves/config/uo-data"
  echo "  3) uninstall  - remove ./server-runtime"
  echo ""
  local choice
  read -r -p "Choose [1-3] (or type install/update/uninstall): " choice
  case "${choice}" in
    1|install)   ACTION="install" ;;
    2|update)    ACTION="update" ;;
    3|uninstall) ACTION="uninstall" ;;
    *) die "Unrecognized choice: ${choice}" ;;
  esac
}

main() {
  resolve_action
  case "${ACTION}" in
    install)   do_install ;;
    update)    do_update ;;
    uninstall) do_uninstall ;;
  esac
}

main "$@"
