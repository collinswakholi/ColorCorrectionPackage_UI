#!/bin/sh
# Prove the Linux package works on a freshly installed system.
#
#   linux_clean_image_check.sh PORTABLE.tar.gz [PACKAGE.deb]
#
# Run as root inside a bare image (docker ubuntu:24.04, ubuntu:22.04, debian:13,
# debian:12) or a fresh WSL distro. It:
#   1. extracts the portable archive and requires check-system to FAIL and name
#      what is missing (the 0.4.0 check passed here, then the app core-dumped);
#   2. runs install-dependencies --install --yes and requires check-system to pass;
#   3. starts the engine, then the desktop app (WSLg/X display if present,
#      otherwise Xvfb), and waits for diagnostics/startup.json (engine ready);
#   4. reads /proc/<pid>/maps of the running app and fails if any loaded system
#      library comes from a package install-dependencies did not install;
#   5. optionally installs the .deb with apt on top and runs its check-system.
set -eu
tarball=${1:?usage: linux_clean_image_check.sh PORTABLE.tar.gz [PACKAGE.deb]}
deb=${2:-}
export DEBIAN_FRONTEND=noninteractive
step() { printf '\n== %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
. /etc/os-release
step "System: ${PRETTY_NAME:-unknown}, glibc $(getconf GNU_LIBC_VERSION | awk '{print $2}'), $(uname -m)"

app=/opt/ccp-clean-check
rm -rf "$app" && mkdir -p "$app"
tar -xzf "$tarball" -C "$app"
[ -x "$app/check-system" ] || app=$(dirname "$(find "$app" -name check-system -type f | head -1)")
cd "$app"

step "1. check-system on the bare system must fail and say what is missing"
if out=$(./check-system 2>&1); then
    printf '%s\n' "$out"
    fail "check-system passed on a bare system; it no longer detects missing libraries"
fi
printf '%s\n' "$out"
printf '%s\n' "$out" | grep -q 'is missing' || fail "check-system failed without naming a missing library"

step "2. install-dependencies --install --yes, then check-system must pass"
dpkg-query -W -f='${Package}\n' | sort > /tmp/ccp-packages-before
./install-dependencies --install --yes
dpkg-query -W -f='${Package}\n' | sort > /tmp/ccp-packages-ours
./check-system || fail "check-system still fails after install-dependencies"

step "3a. The engine starts"
printf '{"cmd":"quit"}\n' | timeout 120 ./ccp-engine-launch > /tmp/ccp-engine.out 2>&1 || true
grep -q '"event":"ready"' /tmp/ccp-engine.out || { cat /tmp/ccp-engine.out; fail "the engine did not report ready"; }
echo "engine ready"

step "3b. The desktop app opens and its engine becomes ready"
xvfb=''
if [ -z "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; then
    # Installed only now, after the dependency list has been proven complete.
    apt-get install -y -qq --no-install-recommends xvfb xauth > /dev/null
    xvfb='xvfb-run -a'
fi
data=$(mktemp -d)
COLORCORRECT_DATA_DIR=$data CCP_REDUCED_MOTION=1 $xvfb ./ColorCorrect-Studio > /tmp/ccp-app.log 2>&1 &
launcher=$!
ready=''
for _ in $(seq 1 120); do
    [ -f "$data/diagnostics/startup.json" ] && ready=1 && break
    kill -0 "$launcher" 2>/dev/null || break
    sleep 1
done
# Bare images have no procps (pgrep); scan /proc directly.
pid=''
for entry in /proc/[0-9]*; do
    exe=$(readlink "$entry/exe" 2>/dev/null || true)
    [ "$exe" = "$app/app/ColorCorrect.App" ] && pid=${entry#/proc/} && break
done
if [ -z "$ready" ]; then
    cat /tmp/ccp-app.log
    [ -n "$pid" ] && kill "$pid" 2>/dev/null
    fail "the app did not reach 'Engine ready' within 120 s"
fi
cat "$data/diagnostics/startup.json"; echo

step "4. Every system library the running app loaded comes from an installed dependency"
[ -n "$pid" ] || fail "could not find the running app process"
foreign=''
for lib in $(awk '$6 ~ /\.so/ {print $6}' "/proc/$pid/maps" | sort -u); do
    case "$lib" in "$app"/*|/tmp/*|/memfd:*) continue ;; esac
    package=$(dpkg -S "$lib" 2>/dev/null | head -1 | cut -d: -f1 || true)
    [ -n "$package" ] || package=$(dpkg -S "$(readlink -f "$lib")" 2>/dev/null | head -1 | cut -d: -f1 || true)
    [ -n "$package" ] || continue
    grep -qx "$package" /tmp/ccp-packages-ours || foreign="$foreign $lib($package)"
done
kill "$pid" 2>/dev/null || true
wait "$launcher" 2>/dev/null || true
[ -z "$foreign" ] || fail "the app loaded libraries from packages that install-dependencies did not install:$foreign"
echo "all loaded system libraries come from the dependency list"

if [ -n "$deb" ]; then
    step "5. The .deb installs with apt and its check-system passes"
    apt-get install -y -qq "$deb" > /dev/null
    /opt/colorcorrect-studio/check-system || fail "check-system fails after installing the .deb"
fi
step "PASS on ${PRETTY_NAME:-this system}"
