#!/usr/bin/env bash
#
# Builds ShareX and installs it for the current user, or system-wide with --system.
#
# Everything lands in the directories the XDG specifications define, so the desktop picks up
# the launcher, the icon and the AppStream metadata without any further steps.

set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="Release"
runtime=""
system_wide=0
self_contained=0

usage() {
    cat <<'USAGE'
Usage: packaging/install.sh [options]

  --system            Install into /usr/local for every user (needs root).
  --debug             Build the Debug configuration instead of Release.
  --self-contained    Bundle the .NET runtime, so .NET does not need to be installed.
  --runtime <rid>     Target runtime identifier (default: the host architecture).
  --uninstall         Remove a previous installation instead of installing.
  -h, --help          Show this message.
USAGE
}

uninstall=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --system) system_wide=1 ;;
        --debug) configuration="Debug" ;;
        --self-contained) self_contained=1 ;;
        --runtime) runtime="$2"; shift ;;
        --uninstall) uninstall=1 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

if [[ -z "$runtime" ]]; then
    case "$(uname -m)" in
        x86_64) runtime="linux-x64" ;;
        aarch64|arm64) runtime="linux-arm64" ;;
        *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
    esac
fi

if (( system_wide )); then
    prefix="/usr/local"
    data_dir="$prefix/share"
else
    prefix="$HOME/.local"
    data_dir="${XDG_DATA_HOME:-$HOME/.local/share}"
fi

install_dir="$data_dir/ShareX"
bin_dir="$prefix/bin"
desktop_dir="$data_dir/applications"
metainfo_dir="$data_dir/metainfo"
icon_root="$data_dir/icons/hicolor"

remove_installation() {
    rm -rf "$install_dir"
    rm -f "$bin_dir/sharex"
    rm -f "$desktop_dir/com.getsharex.ShareX.desktop"
    rm -f "$metainfo_dir/com.getsharex.ShareX.metainfo.xml"
    for icon in "$icon_root"/*/apps/com.getsharex.ShareX.png; do
        [[ -e "$icon" ]] && rm -f "$icon"
    done
    echo "Removed ShareX from $install_dir."
}

if (( uninstall )); then
    remove_installation
    exit 0
fi

echo "Building ShareX ($configuration, $runtime)..."

publish_args=(
    publish "$repository_root/ShareX/ShareX.csproj"
    --configuration "$configuration"
    --runtime "$runtime"
    --output "$install_dir"
    -p:DebugType=embedded
)

if (( self_contained )); then
    publish_args+=(--self-contained true)
else
    publish_args+=(--self-contained false)
fi

dotnet "${publish_args[@]}"

echo "Installing to $install_dir..."

install -d "$bin_dir" "$desktop_dir" "$metainfo_dir"

# A launcher on PATH, so the desktop entry and "sharex" in a terminal agree.
cat > "$bin_dir/sharex" <<LAUNCHER
#!/usr/bin/env bash
exec "$install_dir/ShareX" "\$@"
LAUNCHER
chmod +x "$bin_dir/sharex"

install -m 644 "$repository_root/packaging/com.getsharex.ShareX.desktop" "$desktop_dir/"
install -m 644 "$repository_root/packaging/com.getsharex.ShareX.metainfo.xml" "$metainfo_dir/"

for icon in "$repository_root"/packaging/icons/com.getsharex.ShareX-*.png; do
    size="${icon##*-}"
    size="${size%.png}"
    install -D -m 644 "$icon" "$icon_root/${size}x${size}/apps/com.getsharex.ShareX.png"
done

# Refresh the caches so the launcher and icon show up without a session restart.
command -v update-desktop-database >/dev/null && update-desktop-database "$desktop_dir" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -qtf "$icon_root" 2>/dev/null || true

echo
echo "ShareX is installed. Run it with: sharex"

if ! (( system_wide )) && [[ ":$PATH:" != *":$bin_dir:"* ]]; then
    echo "Note: $bin_dir is not on your PATH."
fi
