#!/usr/bin/env bash
# Keeps the room service on the latest BlackPearl release (deploy/README.md, "Automatic updates").
# blackpearl-update.timer runs it every 15 minutes as root. Each run:
#   1. reads the latest stable release from GitHub and stops if it is not newer than the running build;
#   2. downloads its room service build, checks it against GitHub's SHA-256 and the release's SHA256SUMS.txt, unpacks
#      it into its own root-owned folder and asks it what it is (--describe), as nobody;
#   3. switches to it once nobody is in a room, or right away when it speaks a room protocol the running service does
#      not, or when the release has waited MAX_WAIT_HOURS;
#   4. restarts the service and goes back to the previous build if the new one isn't healthy within a minute.
# Options: --check reports what a run would do and changes nothing. --file <archive> --version <x.y.z> installs a
# local build instead of downloading one, for a first install or a test.
set -euo pipefail
umask 022

REPOSITORY=Svovoniks/BlackPearl
SERVICE=blackpearl                  # the systemd unit that runs the room service
INSTALL=/opt/blackpearl             # the folder its ExecStart runs; it becomes a link to the current release
RELEASES=/opt/blackpearl-releases   # one folder per version
DATA=/var/lib/blackpearl            # the service's BLACKPEARL_DATA, where it writes status.json
HEALTH=http://127.0.0.1:5080/health
STATE=/var/lib/blackpearl-update
MAX_WAIT_HOURS=24
KEEP=3                              # releases kept, the current one included
CONFIG=${BLACKPEARL_UPDATE_CONFIG:-/etc/blackpearl/update.conf}
# Settings above can be changed there, for a server installed under other names.
if [ -f "$CONFIG" ]; then . "$CONFIG"; fi

check=false; file=; file_version=
while [ $# -gt 0 ]; do
    case "$1" in
        --check) check=true ;;
        --file) file=$2; shift ;;
        --version) file_version=$2; shift ;;
        *) echo "Unknown option $1" >&2; exit 2 ;;
    esac
    shift
done
if [ -n "$file" ] && [ -z "$file_version" ]; then echo "--file needs --version" >&2; exit 2; fi

log() { echo "$*"; }
newer() { [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -n 1)" = "$1" ]; }
json() { python3 -c "import json,sys; d=json.load(sys.stdin); print($1)"; }

if ! $check; then
    mkdir -p "$STATE" "$RELEASES"
    exec 9>"$STATE/lock"
    flock -n 9 || { log "Another update is running."; exit 0; }
fi

# The running build. Builds installed by hand before this updater have no version file, so any release is newer.
current=$(cat "$INSTALL/.blackpearl-version" 2>/dev/null || echo 0.0.0)
work=$(mktemp -d /var/tmp/blackpearl-update.XXXXXX)
trap 'rm -rf "$work"' EXIT

if [ -n "$file" ]; then
    version=$file_version
    [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Version must be x.y.z" >&2; exit 2; }
    archive=$file
else
    curl -fsS --max-time 60 -H 'Accept: application/vnd.github+json' -H 'User-Agent: blackpearl-update' \
        "https://api.github.com/repos/$REPOSITORY/releases/latest" -o "$work/release.json"
    tag=$(json "d['tag_name'] if not d.get('draft') and not d.get('prerelease') else ''" < "$work/release.json")
    [[ "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]] || { log "The latest release has no usable version tag."; exit 0; }
    version=${tag#v}
    if ! newer "$version" "$current"; then log "Up to date: $current (latest release $version)."; exit 0; fi
    if [ -e "$STATE/failed-$version" ]; then log "$version failed to start here before; waiting for a newer release."; exit 0; fi
    name="BlackPearl-Server-$version-linux-x64.tar.gz"
    # Name, download link and GitHub's digest of the room service build, and the link to SHA256SUMS.txt.
    read -r url digest sums < <(json "' '.join(next(([a['browser_download_url'], a.get('digest') or '-'] for a in d['assets'] if a['name'] == '$name'), ['-', '-']) + [next((a['browser_download_url'] for a in d['assets'] if a['name'] == 'SHA256SUMS.txt'), '-')])" < "$work/release.json")
    if [ "$url" = - ]; then log "Release $version has no room service build ($name)."; exit 0; fi
    expected="https://github.com/$REPOSITORY/releases/download/$tag/"
    [ "${url#"$expected"}" = "$name" ] && [ "${sums#"$expected"}" = SHA256SUMS.txt ] || { echo "Unexpected download links in release $version" >&2; exit 1; }
    [[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "Release $version gives no SHA-256 for $name" >&2; exit 1; }
    archive=$work/$name
fi

target=$RELEASES/$version
if $check; then
    log "Running $current; would install $version."
else
    if [ ! -f "$target/.blackpearl-version" ]; then
        if [ -z "$file" ]; then
            log "Downloading $version."
            curl -fsSL --max-time 600 --retry 3 -H 'User-Agent: blackpearl-update' "$url" -o "$archive"
            curl -fsSL --max-time 60 --retry 3 -H 'User-Agent: blackpearl-update' "$sums" -o "$work/SHA256SUMS.txt"
            hash=$(sha256sum "$archive" | cut -d ' ' -f 1)
            [ "sha256:$hash" = "$digest" ] || { echo "$name does not match GitHub's SHA-256" >&2; exit 1; }
            awk -v h="$hash" -v n="$name" '{ sub(/\r$/, "") } $1 == h && ($2 == n || $2 == "*" n) { found = 1 } END { exit !found }' "$work/SHA256SUMS.txt" ||
                { echo "$name does not match SHA256SUMS.txt" >&2; exit 1; }
        fi
        staged=$RELEASES/.$version.staging
        rm -rf "$staged"; mkdir -p "$staged"
        tar -xzf "$archive" -C "$staged" --no-same-owner --no-same-permissions
        [ -f "$staged/BlackPearl.Server.dll" ] || { echo "The archive holds no BlackPearl.Server.dll" >&2; rm -rf "$staged"; exit 1; }
        chown -R root:root "$staged"; chmod -R u=rwX,go=rX "$staged"
        described=$(cd /tmp && HOME=/tmp DOTNET_CLI_TELEMETRY_OPTOUT=1 timeout 60 setpriv --reuid=nobody --regid=nogroup --clear-groups -- \
            dotnet "$staged/BlackPearl.Server.dll" --describe) || { echo "Build $version does not start: $described" >&2; rm -rf "$staged"; exit 1; }
        reported=$(json "d['version']" <<< "$described")
        [ "$reported" = "$version" ] || { echo "Build reports version $reported, expected $version" >&2; rm -rf "$staged"; exit 1; }
        echo "$version" > "$staged/.blackpearl-version"
        echo "$described" > "$staged/.blackpearl-describe.json"
        rm -rf "$target"; mv "$staged" "$target"
        log "Installed $version in $target."
    fi
fi

# When to switch. A restart disconnects everyone in a room; their apps enter again by themselves.
pending=$STATE/pending-$version
[ -e "$pending" ] || $check || date +%s > "$pending"
# The wait counts from the first release still waiting, so releases that follow each other quickly can't put it off.
since=$(cat "$STATE"/pending-* 2>/dev/null | sort -n | head -n 1)
since=${since:-$(date +%s)}
running_protocols=$(curl -fsS --max-time 10 "$HEALTH" 2>/dev/null | json "' '.join(map(str, d.get('protocols', [])))" 2>/dev/null || true)
new_protocols=$( (cat "$target/.blackpearl-describe.json" 2>/dev/null || echo '{"protocols":[]}') | json "' '.join(map(str, d['protocols']))")
reason=
# A service that doesn't answer now gives no protocols to compare; whether anyone is in a room decides then.
if [ -n "$running_protocols" ]; then
    for protocol in $new_protocols; do
        [[ " $running_protocols " == *" $protocol "* ]] || reason="it speaks room protocol $protocol, which the running service does not"
    done
fi
if [ -z "$reason" ]; then
    people=$(python3 - "$DATA/status.json" <<'EOF' 2>/dev/null || echo unknown
import json, sys, time
status = json.load(open(sys.argv[1]))
print(status['roomConnections'] if time.time() - status['written'] < 120 else 'unknown')
EOF
)
    if [ "$people" = 0 ]; then reason="nobody is in a room"
    elif [ "$people" = unknown ]; then reason="the running service reports no current status"
    elif [ $(( $(date +%s) - since )) -ge $(( MAX_WAIT_HOURS * 3600 )) ]; then reason="it has waited $MAX_WAIT_HOURS hours for rooms to empty"
    else log "Waiting to switch to $version: $people connected to rooms."; exit 0
    fi
fi
if $check; then log "Would switch to $version now: $reason."; exit 0; fi

log "Switching $SERVICE from $current to $version: $reason."
previous=
if [ -L "$INSTALL" ]; then previous=$(readlink -f "$INSTALL")
elif [ -d "$INSTALL" ]; then
    # The first run keeps the build installed by hand as a release to go back to.
    previous=$RELEASES/manual-$(date +%Y%m%d%H%M%S)
    mv "$INSTALL" "$previous"
fi
link() { ln -sfn "$1" "$INSTALL.new"; mv -Tf "$INSTALL.new" "$INSTALL"; }
# Whether the service answers within a minute, and with the expected version when one is given.
healthy() {
    for _ in $(seq 1 30); do
        sleep 2
        curl -fsS --max-time 5 "$HEALTH" > /dev/null 2>&1 || continue
        [ -z "${1:-}" ] || [ "$(json "d['version']" < "$DATA/status.json" 2>/dev/null)" = "$1" ] || continue
        return 0
    done
    return 1
}
link "$target"
systemctl restart "$SERVICE"
if ! healthy "$version"; then
    echo "Version $version did not become healthy; going back to $current." >&2
    touch "$STATE/failed-$version"
    # The failed build's own last lines name the cause.
    journalctl -u "$SERVICE" --no-pager -n 20 >&2 || true
    rm -f "$pending"
    if [ -z "$previous" ]; then echo "There is no earlier build to go back to." >&2; exit 1; fi
    link "$previous"; systemctl restart "$SERVICE"
    rm -rf "$target"
    if healthy; then echo "Back on $current." >&2; else echo "$current did not come back either; see journalctl -u $SERVICE." >&2; fi
    exit 1
fi
rm -f "$STATE"/pending-*
log "Running $version."

# Keep the newest releases, the current and previous ones always among them.
keep=$(readlink -f "$INSTALL")
{ ls -1dt "$RELEASES"/*/ 2>/dev/null || true; } | sed 's#/$##' | tail -n +$((KEEP + 1)) | while read -r old; do
    [ "$old" = "$keep" ] || [ "$old" = "$previous" ] || { rm -rf "$old"; log "Removed $old."; }
done
exit 0
