#!/usr/bin/env bash
set -euo pipefail

# ╔══════════════════════════════════════════════════════════════════════════════╗
# ║  G-HELPER LINUX — LOCAL DEPLOYMENT SEQUENCE                                  ║
# ║  Installs from local build (dist/) produced by build.sh                      ║
# ║  100% idempotent — safe to re-run infinitely                                 ║
# ║                                                                              ║
# ║  Install:    ./build.sh && sudo ./install/install-local.sh                   ║
# ║  AppImage:   sudo ./install/install-local.sh --appimage                      ║
# ║  Uninstall:  sudo ./install/install-local.sh --uninstall                     ║
# ╚══════════════════════════════════════════════════════════════════════════════╝

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
DIST_DIR="$PROJECT_DIR/dist"

# ── Mode selection ─────────────────────────────────────────────────────────────
MODE="install"
case "${1:-}" in
    --uninstall) MODE="uninstall" ;;
    --appimage)  MODE="appimage" ;;
    --help|-h)
        echo "Usage: $0 [--appimage|--uninstall|--help]"
        echo ""
        echo "  (default)     Full install: deploy binary + udev + permissions + desktop"
        echo "  --appimage    AppImage support: udev rules + sysfs permissions only (no binary)"
        echo "  --uninstall   Remove all installed files"
        exit 0
        ;;
esac

# ── ANSI color matrix ──────────────────────────────────────────────────────────
if [[ -t 1 ]] || [[ "${FORCE_COLOR:-}" == "1" ]]; then
    RED=$'\033[0;91m'
    GREEN=$'\033[0;92m'
    YELLOW=$'\033[0;93m'
    BLUE=$'\033[0;94m'
    MAGENTA=$'\033[0;95m'
    CYAN=$'\033[0;96m'
    DIM=$'\033[2m'
    BOLD=$'\033[1m'
    RESET=$'\033[0m'
else
    RED="" GREEN="" YELLOW="" BLUE="" MAGENTA="" CYAN="" DIM="" BOLD="" RESET=""
fi

# ── Configuration ──────────────────────────────────────────────────────────────
INSTALL_DIR="/opt/ghelper"
UDEV_DEST="/etc/udev/rules.d/90-ghelper.rules"
DESKTOP_DEST="/usr/share/applications/ghelper.desktop"

if [[ -w "/usr/share/applications" ]] 2>/dev/null; then
    DESKTOP_DEST="/usr/share/applications/ghelper.desktop"
else
    DESKTOP_DEST="$HOME/.local/share/applications/ghelper.desktop"
    mkdir -p "$HOME/.local/share/applications" 2>/dev/null || true
fi

# ── Counters ───────────────────────────────────────────────────────────────────
INJECTED=0
SKIPPED=0
UPDATED=0
CHMOD_APPLIED=0
CHMOD_SKIPPED=0
REMOVED=0

# ── Status display functions ───────────────────────────────────────────────────
_inject()  { echo "  ${GREEN}[INJECT]${RESET}  $1"; ((INJECTED++)) || true; }
_update()  { echo "  ${CYAN}[UPDATE]${RESET}  $1"; ((UPDATED++)) || true; }
_skip()    { echo "  ${DIM}[SKIP]${RESET}    ${DIM}$1${RESET}"; ((SKIPPED++)) || true; }
_chmod()   { echo "  ${MAGENTA}[CHMOD]${RESET}  $1"; ((CHMOD_APPLIED++)) || true; }
_chok()    { echo "  ${DIM}[OK]${RESET}      ${DIM}$1${RESET}"; ((CHMOD_SKIPPED++)) || true; }
_fail()    { echo "  ${RED}[FAIL]${RESET}    $1"; }
_info()    { echo "  ${BLUE}[INFO]${RESET}    $1"; }
_warn()    { echo "  ${YELLOW}[WARN]${RESET}    $1"; }
_remove()  { echo "  ${RED}[REMOVE]${RESET}  $1"; ((REMOVED++)) || true; }
_gone()    { echo "  ${DIM}[GONE]${RESET}    ${DIM}$1 (not present)${RESET}"; }

# ── Typing effect ──────────────────────────────────────────────────────────────
_typeout() {
    local text="$1" delay="${2:-0.02}"
    for ((i=0; i<${#text}; i++)); do
        printf "%s" "${text:$i:1}"
        sleep "$delay"
    done
    echo ""
}

# ── Hex step header ───────────────────────────────────────────────────────────
_step() {
    local hex=$1 title="$2"
    echo ""
    echo "${MAGENTA}  ┌──────────────────────────────────────────────────────┐${RESET}"
    echo "${MAGENTA}  │${RESET} ${BOLD}${CYAN}[0x$(printf '%02X' "$hex")]${RESET} ${BOLD}$title${RESET}"
    echo "${MAGENTA}  └──────────────────────────────────────────────────────┘${RESET}"
}

# ── Idempotent file install ────────────────────────────────────────────────────
# Returns 0 if skipped (unchanged), 1 if changed/new.
_install_file() {
    local src="$1" dest="$2" mode="$3" label="$4"
    if [[ -f "$dest" ]]; then
        if cmp -s "$src" "$dest"; then
            _skip "$label → already deployed at $dest"
            return 0
        else
            install -m "$mode" "$src" "$dest"
            _update "$label → $dest"
            return 1
        fi
    else
        install -m "$mode" "$src" "$dest"
        _inject "$label → $dest"
        return 1
    fi
}

# ── Idempotent chmod ───────────────────────────────────────────────────────────
_ensure_chmod() {
    local file="$1"
    [[ -f "$file" ]] || return 0
    local current
    current=$(stat -c '%a' "$file" 2>/dev/null || echo "000")
    if [[ "$current" == "666" ]]; then
        _chok "$file"
    else
        chmod 0666 "$file" 2>/dev/null && _chmod "$file" || true
    fi
}

# ── Safe remove helper ─────────────────────────────────────────────────────────
_safe_remove() {
    local path="$1" label="$2"
    if [[ -e "$path" || -L "$path" ]]; then
        rm -rf "$path"
        _remove "$label → $path"
    else
        _gone "$label"
    fi
}

# ══════════════════════════════════════════════════════════════════════════════
#  BANNER
# ══════════════════════════════════════════════════════════════════════════════

clear 2>/dev/null || true
echo ""
echo "${CYAN}${BOLD}"
cat << 'BANNER'
     ██████╗       ██╗  ██╗███████╗██╗     ██████╗ ███████╗██████╗
    ██╔════╝       ██║  ██║██╔════╝██║     ██╔══██╗██╔════╝██╔══██╗
    ██║  ███╗█████╗███████║█████╗  ██║     ██████╔╝█████╗  ██████╔╝
    ██║   ██║╚════╝██╔══██║██╔══╝  ██║     ██╔═══╝ ██╔══╝  ██╔══██╗
    ╚██████╔╝      ██║  ██║███████╗███████╗██║     ███████╗██║  ╚██╗
     ╚═════╝       ╚═╝  ╚═╝╚══════╝╚══════╝╚═╝     ╚══════╝╚═╝   ╚═╝
BANNER
echo "${RESET}"
echo "${DIM}    ░▒▓█████████████████████████████████████████████████████▓▒░${RESET}"
echo ""

if [[ "$MODE" == "uninstall" ]]; then
    echo "${RED}${BOLD}    ╔══════════════════════════════════════════════════════╗${RESET}"
    echo "${RED}${BOLD}    ║${RESET}  ${BOLD}UNINSTALL SEQUENCE${RESET}                    ${DIM}rev 1.0${RESET}       ${RED}${BOLD}║${RESET}"
    echo "${RED}${BOLD}    ║${RESET}  ${DIM}PROTOCOL: TERMINATE → PURGE → CLEAN${RESET}                 ${RED}${BOLD}║${RESET}"
    echo "${RED}${BOLD}    ╚══════════════════════════════════════════════════════╝${RESET}"
elif [[ "$MODE" == "appimage" ]]; then
    echo "${YELLOW}${BOLD}    ╔══════════════════════════════════════════════════════╗${RESET}"
    echo "${YELLOW}${BOLD}    ║${RESET}  ${BOLD}APPIMAGE SUPPORT MODE${RESET}                 ${DIM}rev 1.0${RESET}       ${YELLOW}${BOLD}║${RESET}"
    echo "${YELLOW}${BOLD}    ║${RESET}  ${DIM}PROTOCOL: INJECT RULES → ARM PERMISSIONS${RESET}            ${YELLOW}${BOLD}║${RESET}"
    echo "${YELLOW}${BOLD}    ╚══════════════════════════════════════════════════════╝${RESET}"
else
    echo "${MAGENTA}${BOLD}    ╔══════════════════════════════════════════════════════╗${RESET}"
    echo "${MAGENTA}${BOLD}    ║${RESET}  ${BOLD}LOCAL DEPLOYMENT SEQUENCE${RESET}              ${DIM}rev 1.0${RESET}       ${MAGENTA}${BOLD}║${RESET}"
    echo "${MAGENTA}${BOLD}    ║${RESET}  ${DIM}PROTOCOL: VERIFY → INJECT → ARM → ACTIVATE${RESET}          ${MAGENTA}${BOLD}║${RESET}"
    echo "${MAGENTA}${BOLD}    ╚══════════════════════════════════════════════════════╝${RESET}"
fi
echo ""
sleep 0.3

# ── Root check ─────────────────────────────────────────────────────────────────
if [[ $EUID -ne 0 ]]; then
    echo ""
    echo "${RED}${BOLD}  ╔══[ ACCESS DENIED ]═══════════════════════════════════╗${RESET}"
    echo "${RED}${BOLD}  ║${RESET}  ${RED}INSUFFICIENT PRIVILEGES :: EUID=$EUID${RESET}"
    echo "${RED}${BOLD}  ║${RESET}  ${DIM}This payload requires root access.${RESET}"
    echo "${RED}${BOLD}  ║${RESET}  ${YELLOW}Re-run with:${RESET} sudo $0"
    echo "${RED}${BOLD}  ╚═════════════════════════════════════════════════════╝${RESET}"
    exit 1
fi

REAL_USER=$(logname 2>/dev/null || echo "${SUDO_USER:-}")

# ══════════════════════════════════════════════════════════════════════════════
#  UNINSTALL MODE
# ══════════════════════════════════════════════════════════════════════════════

if [[ "$MODE" == "uninstall" ]]; then
    echo "${GREEN}  ▸ ROOT ACCESS${RESET} ${DIM}........................${RESET} ${GREEN}CONFIRMED${RESET}"
    echo "${GREEN}  ▸ USER${RESET} ${DIM}..............................${RESET} ${CYAN}${REAL_USER:-unknown}${RESET}"
    echo ""

    echo "${RED}${BOLD}  ╔══[ CONFIRMATION REQUIRED ]════════════════════════════╗${RESET}"
    echo "${RED}${BOLD}  ║${RESET}  This will remove G-Helper Linux and all system files."
    echo "${RED}${BOLD}  ║${RESET}  ${DIM}User config (~/.config/ghelper) will NOT be removed.${RESET}"
    echo "${RED}${BOLD}  ╚═════════════════════════════════════════════════════════╝${RESET}"
    echo ""
    printf "  ${BOLD}Type ${RED}YES${RESET}${BOLD} to confirm uninstall: ${RESET}"
    read -r confirm
    if [[ "$confirm" != "YES" ]]; then
        echo ""
        echo "  ${YELLOW}Aborted.${RESET}"
        exit 0
    fi
    echo ""

    # ── Stop running process ──
    _step 1 "TERMINATING RUNNING INSTANCES"
    if pgrep -x ghelper &>/dev/null; then
        pkill -x ghelper 2>/dev/null && _info "ghelper process terminated" || _warn "could not kill ghelper"
        sleep 0.5
    else
        _info "${DIM}no running ghelper process found${RESET}"
    fi

    # ── Remove files ──
    _step 2 "PURGING INSTALLED FILES"

    # Disable systemd boot service BEFORE removing its unit file
    # (systemctl disable fails if unit file is already gone — orphaned symlink)
    if systemctl is-enabled ghelper-gpu-boot.service 2>/dev/null; then
        systemctl disable ghelper-gpu-boot.service 2>/dev/null
        _info "ghelper-gpu-boot.service disabled"
    fi

    _safe_remove "$INSTALL_DIR"                          "install directory ($INSTALL_DIR)"
    _safe_remove "/usr/local/bin/ghelper"                 "symlink"
    _safe_remove "$UDEV_DEST"                             "udev rules"
    _safe_remove "/etc/tmpfiles.d/90-ghelper.conf"        "tmpfiles config"
    _safe_remove "/etc/systemd/system/ghelper-gpu-boot.service" "GPU boot systemd unit"
    _safe_remove "/usr/local/lib/ghelper"                 "ghelper lib directory"
    _safe_remove "/etc/sudoers.d/ghelper-gpu"             "sudoers rule"
    _safe_remove "/etc/modprobe.d/ghelper-gpu-block.conf"    "dGPU block (modprobe)"
    _safe_remove "/etc/udev/rules.d/50-ghelper-remove-dgpu.rules" "dGPU block (udev)"
    _safe_remove "/etc/ghelper"                           "ghelper config dir"
    _safe_remove "$DESKTOP_DEST"                          "desktop entry (system)"

    # User-local desktop entry
    if [[ -n "$REAL_USER" ]]; then
        _safe_remove "/home/$REAL_USER/.local/share/applications/ghelper.desktop" "desktop entry (user)"
        _safe_remove "/home/$REAL_USER/.config/autostart/ghelper.desktop"          "autostart entry"
    fi

    # Icons
    _safe_remove "/usr/share/icons/hicolor/64x64/apps/ghelper.png" "icon (system, png)"
    _safe_remove "/usr/share/icons/hicolor/64x64/apps/ghelper.ico" "icon (system, ico)"
    if [[ -n "$REAL_USER" ]]; then
        _safe_remove "/home/$REAL_USER/.local/share/icons/hicolor/64x64/apps/ghelper.png" "icon (user, png)"
        _safe_remove "/home/$REAL_USER/.local/share/icons/hicolor/64x64/apps/ghelper.ico" "icon (user, ico)"
    fi

    # ── Reload daemons ──
    _step 3 "RELOADING SYSTEM DAEMONS"
    systemctl daemon-reload 2>/dev/null && _info "systemd daemon-reload" || true
    udevadm control --reload-rules 2>/dev/null && _info "udev daemon reloaded" || true

    # ── Summary ──
    echo ""
    echo ""
    echo "${RED}${BOLD}  ╔════════════════════════════════════════════════════════════════╗${RESET}"
    echo "${RED}${BOLD}  ║                                                                ║${RESET}"
    echo "${RED}${BOLD}  ║  ▓▓▓ UNINSTALL COMPLETE ▓▓▓                                    ║${RESET}"
    echo "${RED}${BOLD}  ║                                                                ║${RESET}"
    echo "${RED}${BOLD}  ╠════════════════════════════════════════════════════════════════╣${RESET}"
    echo "${RED}${BOLD}  ║${RESET}  ${RED}REMOVED: $REMOVED files/directories${RESET}"
    echo "${RED}${BOLD}  ║${RESET}  ${DIM}User config preserved at ~/.config/ghelper/${RESET}"
    echo "${RED}${BOLD}  ║${RESET}  ${DIM}sysfs permissions will reset on next reboot${RESET}"
    echo "${RED}${BOLD}  ║                                                                ║${RESET}"
    echo "${RED}${BOLD}  ╚════════════════════════════════════════════════════════════════╝${RESET}"
    echo ""
    exit 0
fi

# ══════════════════════════════════════════════════════════════════════════════
#  INSTALL / APPIMAGE MODE — common setup
# ══════════════════════════════════════════════════════════════════════════════

echo "${GREEN}  ▸ ROOT ACCESS${RESET} ${DIM}........................${RESET} ${GREEN}CONFIRMED${RESET}"
echo "${GREEN}  ▸ USER${RESET} ${DIM}..............................${RESET} ${CYAN}${REAL_USER:-unknown}${RESET}"
if [[ "$MODE" == "install" ]]; then
    echo "${GREEN}  ▸ SOURCE${RESET} ${DIM}............................${RESET} ${CYAN}$DIST_DIR/${RESET}"
    echo "${GREEN}  ▸ TARGET${RESET} ${DIM}............................${RESET} ${CYAN}$INSTALL_DIR/${RESET}"
fi

# ══════════════════════════════════════════════════════════════════════════════
#  [0x01] VERIFY LOCAL BUILD (install mode only)
# ══════════════════════════════════════════════════════════════════════════════

if [[ "$MODE" == "install" ]]; then
    _step 1 "SCANNING LOCAL BUILD ARTIFACTS"

    # Binary + native libs are all required — loader expects the two .so files
    # next to the binary, or it falls back to stale cache / system libs.
    for f in ghelper libSkiaSharp.so libHarfBuzzSharp.so; do
        if [[ ! -f "$DIST_DIR/$f" ]]; then
            echo ""
            echo "${RED}${BOLD}  ╔══[ BUILD NOT FOUND ]═════════════════════════════════╗${RESET}"
            echo "${RED}${BOLD}  ║${RESET}  ${RED}Missing artifact:${RESET} $DIST_DIR/$f"
            echo "${RED}${BOLD}  ║${RESET}  ${YELLOW}Run ./build.sh first${RESET}"
            echo "${RED}${BOLD}  ║${RESET}  ${DIM}...or use install.sh to download latest release${RESET}"
            echo "${RED}${BOLD}  ╚═════════════════════════════════════════════════════╝${RESET}"
            exit 1
        fi
    done

    BINARY_SIZE=$(du -sh "$DIST_DIR/ghelper" | cut -f1)
    _info "Binary located: ${BOLD}$DIST_DIR/ghelper${RESET} ${DIM}(${BINARY_SIZE})${RESET}"

    # Count dist files
    DIST_FILES=("$DIST_DIR"/*)
    _info "Artifacts found: ${GREEN}${#DIST_FILES[@]} files${RESET}"
fi

# ══════════════════════════════════════════════════════════════════════════════
#  [0x02] INJECT BINARIES (install mode only)
# ══════════════════════════════════════════════════════════════════════════════

if [[ "$MODE" == "install" ]]; then
    _step 2 "INJECTING BINARIES INTO TARGET"

    mkdir -p "$INSTALL_DIR"

    _install_file "$DIST_DIR/ghelper"             "$INSTALL_DIR/ghelper"             755 "ghelper binary"     || true
    _install_file "$DIST_DIR/libSkiaSharp.so"     "$INSTALL_DIR/libSkiaSharp.so"     755 "libSkiaSharp.so"    || true
    _install_file "$DIST_DIR/libHarfBuzzSharp.so" "$INSTALL_DIR/libHarfBuzzSharp.so" 755 "libHarfBuzzSharp.so" || true

    # Symlink (ln -sf is already idempotent but we report status)
    if [[ "$(readlink -f /usr/local/bin/ghelper 2>/dev/null)" == "$INSTALL_DIR/ghelper" ]]; then
        _skip "symlink → /usr/local/bin/ghelper already targets $INSTALL_DIR/ghelper"
    else
        ln -sf "$INSTALL_DIR/ghelper" /usr/local/bin/ghelper
        _inject "symlink → /usr/local/bin/ghelper"
    fi

    # Fix ownership so the real user can run ghelper and self-update without root
    if [[ -n "$REAL_USER" ]]; then
        chown -R "$REAL_USER:$REAL_USER" "$INSTALL_DIR"
        _info "ownership → ${BOLD}$REAL_USER:$REAL_USER${RESET} on $INSTALL_DIR/"
    fi
else
    _info "${DIM}AppImage mode — skipping binary installation${RESET}"
fi

# ══════════════════════════════════════════════════════════════════════════════
#  [0x03] GPU BLOCK HELPER (passwordless sudo for Eco mode transitions)
# ══════════════════════════════════════════════════════════════════════════════

_step 3 "GPU BLOCK HELPER + BOOT SERVICE + SUDOERS RULE"

HELPER_DIR="/usr/local/lib/ghelper"
HELPER_DEST="$HELPER_DIR/gpu-block-helper.sh"
SUDOERS_DEST="/etc/sudoers.d/ghelper-gpu"

mkdir -p "$HELPER_DIR"
_install_file "$SCRIPT_DIR/gpu-block-helper.sh" "$HELPER_DEST" 755 "GPU block helper" || true

# sudoers rule — allow ALL users to run the helper without password.
# The helper only does 2 things: write specific config files, or remove them.
SUDOERS_CONTENT="# G-Helper: allow passwordless GPU block file management
ALL ALL=(root) NOPASSWD: $HELPER_DEST"

if [[ -f "$SUDOERS_DEST" ]] && echo "$SUDOERS_CONTENT" | cmp -s - "$SUDOERS_DEST"; then
    _skip "sudoers rule → already deployed at $SUDOERS_DEST"
else
    echo "$SUDOERS_CONTENT" > "$SUDOERS_DEST"
    chmod 0440 "$SUDOERS_DEST"
    # Validate — if visudo rejects it, remove immediately
    if visudo -c -f "$SUDOERS_DEST" &>/dev/null; then
        _inject "sudoers rule → $SUDOERS_DEST"
    else
        rm -f "$SUDOERS_DEST"
        _fail "sudoers rule — syntax error, removed (GPU block will use pkexec fallback)"
    fi
fi

# ── GPU Boot Service (apply pending GPU mode at boot before display manager) ──
if [[ "$MODE" != "appimage" ]]; then
    BOOT_SCRIPT_SRC="$SCRIPT_DIR/ghelper-gpu-boot.sh"
    BOOT_SCRIPT_DEST="$HELPER_DIR/ghelper-gpu-boot.sh"
    BOOT_UNIT_SRC="$SCRIPT_DIR/ghelper-gpu-boot.service"
    BOOT_UNIT_DEST="/etc/systemd/system/ghelper-gpu-boot.service"

    _install_file "$BOOT_SCRIPT_SRC" "$BOOT_SCRIPT_DEST" 755 "GPU boot script" || true
    _install_file "$BOOT_UNIT_SRC" "$BOOT_UNIT_DEST" 644 "GPU boot systemd unit" || true

    # Reload systemd so it picks up the new/changed unit file
    systemctl daemon-reload 2>/dev/null || true
    _info "systemd daemon-reload"

    # Enable the service (idempotent — safe to re-run)
    if systemctl enable ghelper-gpu-boot.service 2>/dev/null; then
        _info "ghelper-gpu-boot.service enabled"
    else
        _warn "failed to enable ghelper-gpu-boot.service (systemd may not be running)"
    fi
fi

# ══════════════════════════════════════════════════════════════════════════════
#  [0x04] DEPLOY UDEV RULESET
# ══════════════════════════════════════════════════════════════════════════════

_step 4 "DEPLOYING UDEV RULESET"

# Always write + reload + trigger udev rules unconditionally.
# The rules list may grow between releases, and the daemon may not have
# loaded them even if the file on disk looks the same.
install -m 644 "$SCRIPT_DIR/90-ghelper.rules" "$UDEV_DEST"
_inject "udev rules → $UDEV_DEST"

udevadm control --reload-rules
_info "udev daemon reloaded"

udevadm trigger
_info "udev trigger fired — re-applying all RUN commands"

# ── Remove stale tmpfiles.d config from previous versions ──
# The 90-ghelper.conf tmpfiles config was redundant with udev rules and
# risked kernel deadlocks if 'w' directives were ever added. Removed in v2.
TMPFILES_STALE="/etc/tmpfiles.d/90-ghelper.conf"
if [[ -f "$TMPFILES_STALE" ]]; then
    rm -f "$TMPFILES_STALE"
    _info "removed stale tmpfiles config → $TMPFILES_STALE"
else
    _info "${DIM}no stale tmpfiles config found (good)${RESET}"
fi

# ══════════════════════════════════════════════════════════════════════════════
#  [0x05] ESTABLISH SYSFS ACCESS LAYER
# ══════════════════════════════════════════════════════════════════════════════

_step 5 "ESTABLISHING SYSFS ACCESS LAYER"

echo "  ${DIM}(permissions reset on reboot — always re-applied)${RESET}"

# Fixed-path sysfs nodes
for f in \
    /sys/devices/platform/asus-nb-wmi/throttle_thermal_policy \
    /sys/devices/platform/asus-nb-wmi/panel_od \
    /sys/devices/platform/asus-nb-wmi/ppt_pl1_spl \
    /sys/devices/platform/asus-nb-wmi/ppt_pl2_sppt \
    /sys/devices/platform/asus-nb-wmi/ppt_fppt \
    /sys/devices/platform/asus-nb-wmi/nv_dynamic_boost \
    /sys/devices/platform/asus-nb-wmi/nv_temp_target \
    /sys/bus/platform/devices/asus-nb-wmi/dgpu_disable \
    /sys/bus/platform/devices/asus-nb-wmi/gpu_mux_mode \
    /sys/bus/platform/devices/asus-nb-wmi/mini_led_mode \
    /sys/module/pcie_aspm/parameters/policy \
    /sys/firmware/acpi/platform_profile \
    /sys/devices/system/cpu/intel_pstate/no_turbo \
    /sys/devices/system/cpu/cpufreq/boost \
    /sys/class/leds/asus::kbd_backlight/brightness \
    /sys/class/leds/asus::kbd_backlight/multi_intensity \
    /sys/class/leds/asus::kbd_backlight/kbd_rgb_mode \
    /sys/class/leds/asus::kbd_backlight/kbd_rgb_state; do
    _ensure_chmod "$f"
done

# ── ASUS firmware-attributes (asus-armoury, kernel 6.8+) ──
_fa_count=0
for f in /sys/class/firmware-attributes/asus-armoury/attributes/*/current_value; do
    [[ -f "$f" ]] && { _ensure_chmod "$f"; ((_fa_count++)) || true; }
done
if [[ $_fa_count -gt 0 ]]; then
    _info "firmware-attributes (asus-armoury): ${GREEN}${_fa_count} attrs${RESET} processed"
else
    _info "${DIM}no asus-armoury firmware-attributes found (using legacy sysfs)${RESET}"
fi

# ── Battery charge limit ──
_bat_count=0
for f in /sys/class/power_supply/BAT*/charge_control_end_threshold; do
    [[ -f "$f" ]] && { _ensure_chmod "$f"; ((_bat_count++)) || true; }
done
[[ $_bat_count -eq 0 ]] && _info "${DIM}no battery charge_control_end_threshold found${RESET}"

# ── Backlight ──
_bl_count=0
for f in /sys/class/backlight/*/brightness; do
    [[ -f "$f" ]] && { _ensure_chmod "$f"; ((_bl_count++)) || true; }
done
[[ $_bl_count -eq 0 ]] && _info "${DIM}no backlight brightness nodes found${RESET}"

# ── CPU online/offline ──
_cpu_count=0
for f in /sys/devices/system/cpu/cpu*/online; do
    [[ -f "$f" ]] && { _ensure_chmod "$f"; ((_cpu_count++)) || true; }
done
if [[ $_cpu_count -gt 0 ]]; then
    _info "CPU core online/offline: ${GREEN}${_cpu_count} nodes${RESET} processed"
else
    _info "${DIM}no CPU online/offline nodes found${RESET}"
fi

# ── Fan curves (hwmon) ──
_hwmon_found=0
for hwmon in /sys/class/hwmon/hwmon*; do
    name=$(cat "$hwmon/name" 2>/dev/null || echo "")
    if [[ "$name" == "asus_nb_wmi" || "$name" == "asus_custom_fan_curve" ]]; then
        _fan_count=0
        for f in "$hwmon"/pwm*_auto_point* "$hwmon"/pwm*_enable; do
            [[ -f "$f" ]] && { _ensure_chmod "$f"; ((_fan_count++)) || true; }
        done
        _info "${CYAN}$(basename "$hwmon")${RESET} (${BOLD}$name${RESET}) — ${GREEN}${_fan_count} fan curve nodes${RESET}"
        ((_hwmon_found++)) || true
    fi
done
[[ $_hwmon_found -eq 0 ]] && _info "${DIM}no asus fan curve hwmon devices found${RESET}"

echo ""
_info "sysfs summary: ${GREEN}${CHMOD_APPLIED} armed${RESET} / ${DIM}${CHMOD_SKIPPED} already 0666${RESET}"

# ══════════════════════════════════════════════════════════════════════════════
#  [0x06] DESKTOP INTEGRATION (install mode only)
# ══════════════════════════════════════════════════════════════════════════════

if [[ "$MODE" == "install" ]]; then
    _step 6 "DESKTOP INTEGRATION LAYER"

    if install -m 644 "$SCRIPT_DIR/ghelper.desktop" "$DESKTOP_DEST" 2>/dev/null; then
        _inject "desktop entry → $DESKTOP_DEST"
    else
        _warn "desktop entry → $DESKTOP_DEST (read-only, using autostart instead)"
    fi

    # Icon
    ICON_SRC="$PROJECT_DIR/src/UI/Assets/favicon.ico"
    if [[ -w "/usr/share/icons/hicolor/64x64/apps" ]] 2>/dev/null; then
        ICON_DEST="/usr/share/icons/hicolor/64x64/apps"
    else
        ICON_DEST="$HOME/.local/share/icons/hicolor/64x64/apps"
        mkdir -p "$ICON_DEST" 2>/dev/null || true
    fi
    if [[ -f "$ICON_SRC" ]]; then
        mkdir -p "$ICON_DEST"
        if command -v convert &>/dev/null; then
            # ImageMagick available — convert ICO → PNG
            if [[ -f "$ICON_DEST/ghelper.png" ]]; then
                # Generate temp conversion and compare
                ICON_TMP=$(mktemp /tmp/ghelper-icon-XXXXXX.png)
                convert "$ICON_SRC[0]" "$ICON_TMP" 2>/dev/null
                if cmp -s "$ICON_TMP" "$ICON_DEST/ghelper.png"; then
                    _skip "icon → already deployed at $ICON_DEST/ghelper.png"
                else
                    mv "$ICON_TMP" "$ICON_DEST/ghelper.png"
                    _update "icon → $ICON_DEST/ghelper.png"
                fi
                rm -f "$ICON_TMP" 2>/dev/null || true
            else
                convert "$ICON_SRC[0]" "$ICON_DEST/ghelper.png" 2>/dev/null
                _inject "icon → $ICON_DEST/ghelper.png"
            fi
        else
            # No ImageMagick — copy ICO directly
            if [[ -f "$ICON_DEST/ghelper.ico" ]] && cmp -s "$ICON_SRC" "$ICON_DEST/ghelper.ico"; then
                _skip "icon → already deployed at $ICON_DEST/ghelper.ico"
            else
                cp "$ICON_SRC" "$ICON_DEST/ghelper.ico"
                # Patch desktop entry to use absolute path
                sed -i "s|Icon=ghelper|Icon=$ICON_DEST/ghelper.ico|" \
                    "$DESKTOP_DEST" 2>/dev/null || true
                _inject "icon → $ICON_DEST/ghelper.ico ${DIM}(no ImageMagick — raw ICO)${RESET}"
            fi
        fi
        gtk-update-icon-cache "$ICON_DEST" 2>/dev/null || true
    else
        _warn "No icon source found at $ICON_SRC"
    fi
fi

# ══════════════════════════════════════════════════════════════════════════════
#  [0x07] AUTOSTART IMPLANT (install mode only)
# ══════════════════════════════════════════════════════════════════════════════

if [[ "$MODE" == "install" ]]; then
    _step 7 "AUTOSTART IMPLANT"

    if [[ -n "$REAL_USER" ]]; then
        AUTOSTART_DIR="/home/$REAL_USER/.config/autostart"
        AUTOSTART_DEST="$AUTOSTART_DIR/ghelper.desktop"
        # Create dir as the real user so ownership is correct from the start
        su -c "mkdir -p '$AUTOSTART_DIR'" "$REAL_USER"
        install -m 644 "$SCRIPT_DIR/ghelper.desktop" "$AUTOSTART_DEST"
        chown "$REAL_USER:$REAL_USER" "$AUTOSTART_DEST"
        _inject "autostart for user ${BOLD}$REAL_USER${RESET} → $AUTOSTART_DEST"
    else
        _warn "Could not determine real user — skipping autostart"
    fi
fi

# ══════════════════════════════════════════════════════════════════════════════
#  DEPLOYMENT COMPLETE
# ══════════════════════════════════════════════════════════════════════════════

echo ""
echo ""

if [[ "$MODE" == "appimage" ]]; then
    echo "${YELLOW}${BOLD}  ╔════════════════════════════════════════════════════════════════╗${RESET}"
    echo "${YELLOW}${BOLD}  ║                                                                ║${RESET}"
    echo "${YELLOW}${BOLD}  ║  ▓▓▓ APPIMAGE SUPPORT DEPLOYED ▓▓▓                             ║${RESET}"
    echo "${YELLOW}${BOLD}  ║                                                                ║${RESET}"
    echo "${YELLOW}${BOLD}  ╠════════════════════════════════════════════════════════════════╣${RESET}"
    echo "${YELLOW}${BOLD}  ║                                                                ║${RESET}"
    echo "${YELLOW}${BOLD}  ║${RESET}  ${CYAN}0xF0${RESET}  udev      → $UDEV_DEST"
    echo "${YELLOW}${BOLD}  ║${RESET}  ${CYAN}0xF1${RESET}  cleanup   → removed stale tmpfiles (if any)"
    echo "${YELLOW}${BOLD}  ║                                                                ║${RESET}"
    echo "${YELLOW}${BOLD}  ╠════════════════════════════════════════════════════════════════╣${RESET}"
    echo "${YELLOW}${BOLD}  ║                                                                ║${RESET}"
    echo "${YELLOW}${BOLD}  ║${RESET}  ${GREEN}INJECTED: $INJECTED${RESET}   ${CYAN}UPDATED: $UPDATED${RESET}   ${DIM}SKIPPED: $SKIPPED${RESET}"
    echo "${YELLOW}${BOLD}  ║${RESET}  ${MAGENTA}CHMOD: $CHMOD_APPLIED armed${RESET}   ${DIM}$CHMOD_SKIPPED already set${RESET}"
    echo "${YELLOW}${BOLD}  ║                                                                ║${RESET}"
    echo "${YELLOW}${BOLD}  ╚════════════════════════════════════════════════════════════════╝${RESET}"
    echo ""
    _typeout "${YELLOW}${BOLD}  > HARDWARE ACCESS LAYER READY :: Launch your AppImage now${RESET}" 0.03
else
    echo "${GREEN}${BOLD}  ╔════════════════════════════════════════════════════════════════╗${RESET}"
    echo "${GREEN}${BOLD}  ║                                                                ║${RESET}"
    echo "${GREEN}${BOLD}  ║  ▓▓▓ DEPLOYMENT SEQUENCE COMPLETE ▓▓▓                          ║${RESET}"
    echo "${GREEN}${BOLD}  ║                                                                ║${RESET}"
    echo "${GREEN}${BOLD}  ╠════════════════════════════════════════════════════════════════╣${RESET}"
    echo "${GREEN}${BOLD}  ║                                                                ║${RESET}"
    echo "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF0${RESET}  Binary    → $INSTALL_DIR/ghelper"
    echo "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF1${RESET}  Symlink   → /usr/local/bin/ghelper"
    echo "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF2${RESET}  udev      → $UDEV_DEST"
    echo "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF3${RESET}  Desktop   → $DESKTOP_DEST"
    echo "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF4${RESET}  Autostart → ~/.config/autostart/ghelper.desktop"
    echo "${GREEN}${BOLD}  ║                                                                ║${RESET}"
    echo "${GREEN}${BOLD}  ╠════════════════════════════════════════════════════════════════╣${RESET}"
    echo "${GREEN}${BOLD}  ║                                                                ║${RESET}"
    echo "${GREEN}${BOLD}  ║${RESET}  ${GREEN}INJECTED: $INJECTED${RESET}   ${CYAN}UPDATED: $UPDATED${RESET}   ${DIM}SKIPPED: $SKIPPED${RESET}"
    echo "${GREEN}${BOLD}  ║${RESET}  ${MAGENTA}CHMOD: $CHMOD_APPLIED armed${RESET}   ${DIM}$CHMOD_SKIPPED already set${RESET}"
    echo "${GREEN}${BOLD}  ║                                                                ║${RESET}"
    echo "${GREEN}${BOLD}  ╚════════════════════════════════════════════════════════════════╝${RESET}"
    echo ""
    _typeout "${GREEN}${BOLD}  > NEURAL LINK ESTABLISHED :: LAUNCH WITH: ghelper${RESET}" 0.03
fi

echo ""
