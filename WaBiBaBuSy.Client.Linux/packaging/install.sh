#!/usr/bin/env bash
# Install the WaBiBaBuSy Linux client for the current user (no root needed).
#
#   ./packaging/install.sh [--args "<client options>"] [--no-autostart] [--gnome-extension]
#   ./packaging/install.sh --uninstall
#
# Run it from inside the published folder (dotnet publish ... -o <folder>).
set -euo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST="${XDG_DATA_HOME:-$HOME/.local/share}/wabibabusy-client"
BIN_LINK="$HOME/.local/bin/wabibabusy-client"
AUTOSTART="${XDG_CONFIG_HOME:-$HOME/.config}/autostart/wabibabusy-client.desktop"
EXT_UUID="wabibabusy-wallpaper@bibabu"
EXT_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/gnome-shell/extensions/$EXT_UUID"

CLIENT_ARGS=""
AUTOSTART_ENABLED=1
GNOME_EXT=0
UNINSTALL=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --args) CLIENT_ARGS="$2"; shift 2 ;;
        --no-autostart) AUTOSTART_ENABLED=0; shift ;;
        --gnome-extension) GNOME_EXT=1; shift ;;
        --uninstall) UNINSTALL=1; shift ;;
        -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

if [[ $UNINSTALL -eq 1 ]]; then
    pkill -x wabibabusy-clie 2>/dev/null || true
    rm -rf "$DEST" "$BIN_LINK" "$AUTOSTART"
    if [[ -d "$EXT_DIR" ]]; then
        gnome-extensions disable "$EXT_UUID" 2>/dev/null || true
        rm -rf "$EXT_DIR"
    fi
    echo "WaBiBaBuSy client removed."
    exit 0
fi

if [[ ! -x "$SRC/wabibabusy-client" ]]; then
    echo "wabibabusy-client not found next to packaging/ — run this from the published folder." >&2
    exit 1
fi

# ── Native libraries the client loads at runtime ──
# Read the cache once: piping ldconfig into grep -q under pipefail fails spuriously
# (grep exits on the first match, ldconfig gets SIGPIPE, the pipeline reports 141).
ld_cache="$(ldconfig -p 2>/dev/null || true)"
missing=()
for lib in libX11.so.6 libXrandr.so.2 libXext.so.6 libICE.so.6 libSM.so.6 libfontconfig.so.1; do
    grep -qF "$lib" <<<"$ld_cache" || missing+=("$lib")
done
if [[ ${#missing[@]} -gt 0 ]]; then
    echo "WARNING: missing libraries: ${missing[*]}"
    echo "  Fedora:        sudo dnf install libX11 libXrandr libXext libICE libSM fontconfig"
    echo "  Ubuntu/Debian: sudo apt install libx11-6 libxrandr2 libxext6 libice6 libsm6 libfontconfig1"
fi

# ── Copy the program ──
if [[ "$SRC" != "$DEST" ]]; then
    rm -rf "$DEST"
    mkdir -p "$DEST"
    cp -a "$SRC/." "$DEST/"
fi
chmod +x "$DEST/wabibabusy-client"
mkdir -p "$(dirname "$BIN_LINK")"
ln -sf "$DEST/wabibabusy-client" "$BIN_LINK"
echo "Installed to $DEST (command: wabibabusy-client)"

# ── Start with the desktop session ──
if [[ $AUTOSTART_ENABLED -eq 1 ]]; then
    mkdir -p "$(dirname "$AUTOSTART")"
    cat > "$AUTOSTART" <<EOF
[Desktop Entry]
Type=Application
Name=WaBiBaBuSy Client
Comment=Synchronized animated wallpaper (Linux client)
Exec=$DEST/wabibabusy-client $CLIENT_ARGS
X-GNOME-Autostart-enabled=true
X-KDE-autostart-after=panel
NoDisplay=true
EOF
    echo "Autostart entry: $AUTOSTART"
fi

# ── Optional GNOME Shell fallback extension ──
if [[ $GNOME_EXT -eq 1 ]]; then
    mkdir -p "$EXT_DIR"
    cp -a "$DEST/packaging/gnome-extension/$EXT_UUID/." "$EXT_DIR/"
    gnome-extensions enable "$EXT_UUID" 2>/dev/null \
        && echo "GNOME extension enabled." \
        || echo "GNOME extension copied — log out and back in, then run: gnome-extensions enable $EXT_UUID"
fi

cat <<EOF

Next steps:
  * Firewall: allow mDNS (UDP 5353) so the server is found automatically
      Fedora: sudo firewall-cmd --permanent --add-service=mdns && sudo firewall-cmd --reload
      Ubuntu: sudo ufw allow 5353/udp
    or skip discovery with: wabibabusy-client --server <server-ip>
  * Check placement without a server: wabibabusy-client --demo   (Ctrl+C to quit)
EOF
