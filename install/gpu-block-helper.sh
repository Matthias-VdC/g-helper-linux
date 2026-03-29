#!/bin/bash
# ╔══════════════════════════════════════════════════════════════════════╗
# ║  G-HELPER GPU BLOCK HELPER                                          ║
# ║  Manages GPU block artifacts (vendor-aware: nvidia + amdgpu)         ║
# ║  for Eco mode boot transitions.                                      ║
# ║  Called by ghelper via sudo (NOPASSWD via /etc/sudoers.d/ghelper).   ║
# ║                                                                      ║
# ║  Usage:                                                              ║
# ║    sudo /usr/local/lib/ghelper/gpu-block-helper.sh write SRC1 SRC2  ║
# ║    sudo /usr/local/lib/ghelper/gpu-block-helper.sh clean             ║
# ╚══════════════════════════════════════════════════════════════════════╝
set -euo pipefail

MODPROBE_DEST="/etc/modprobe.d/ghelper-gpu-block.conf"
UDEV_DEST="/etc/udev/rules.d/50-ghelper-remove-dgpu.rules"
TRIGGER_DIR="/etc/ghelper"
TRIGGER_DEST="$TRIGGER_DIR/pending-gpu-mode"

# ── Modprobe Block Rules ----------------------------------------------------------
MODPROBE_CONTENT="# ghelper: block dGPU driver modules so dGPU can be safely disabled on next boot
# Auto-generated — will be removed after Eco mode is applied
# Uses 'install /bin/false' (strongest block — prevents loading by ANY means)
# NVIDIA modules
install nvidia /bin/false
install nvidia_drm /bin/false
install nvidia_modeset /bin/false
install nvidia_uvm /bin/false
install nvidia_wmi_ec_backlight /bin/false
# Open-source NVIDIA driver
install nouveau /bin/false
# AMD dGPU driver
install amdgpu /bin/false"

# ── Udev Device Removal Rules ----------------------------------------------------------
UDEV_CONTENT="# ghelper: remove dGPU PCI devices so no driver can bind
# Auto-generated — will be removed after Eco mode is applied
# Remove NVIDIA VGA controller
ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x030000\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"
# Remove NVIDIA 3D controller
ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x030200\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"
# Remove NVIDIA Audio devices (HDMI audio on dGPU)
ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x040300\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"
# Remove NVIDIA USB xHCI Host Controller
ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x0c0330\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"
# Remove NVIDIA USB Type-C UCSI devices
ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x0c8000\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"
# Remove AMD dGPU VGA controller (boot_vga!=1 protects the iGPU)
ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x1002\", ATTR{class}==\"0x030000\", ATTR{boot_vga}!=\"1\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"
# Remove AMD dGPU 3D controller
ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x1002\", ATTR{class}==\"0x030200\", ATTR{boot_vga}!=\"1\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"
# Remove AMD dGPU Audio devices
ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x1002\", ATTR{class}==\"0x040300\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\""

case "${1:-}" in
    write)
        # Args: $2 = temp modprobe file, $3 = temp udev file, $4 = mode name (optional)
        if [[ -z "${2:-}" || -z "${3:-}" ]]; then
            echo "Usage: $0 write <modprobe-src> <udev-src> [mode]" >&2
            exit 1
        fi
        MODE="${4:-eco}"
        mkdir -p "$TRIGGER_DIR"
        install -m 644 "$2" "$MODPROBE_DEST"
        install -m 644 "$3" "$UDEV_DEST"
        echo "$MODE" > "$TRIGGER_DEST"
        ;;
    clean)
        rm -f "$MODPROBE_DEST" "$UDEV_DEST" "$TRIGGER_DEST"
        ;;
    *)
        echo "Usage: $0 {write|clean}" >&2
        exit 1
        ;;
esac
