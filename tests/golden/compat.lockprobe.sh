#!/usr/bin/env bash
# Drives `ionc lock check` over one v1 -> v2 schema edit per evolution axis and reports, for each,
# whether the lock file caught it.
#
# The wire vectors in compat.golden.json show what a runtime DOES when it meets a payload from
# another schema revision. This script shows what stops that payload from ever being written:
# ion.lock.json is the only thing in the toolchain that can reject a non-append edit, because a
# positional array cannot carry the information a reader would need to detect one.
#
# Usage, from the repository root:
#     dotnet build src/ionc/ionc.csproj
#     bash tests/golden/compat.lockprobe.sh
#
# The `lockCheck` section of compat.golden.json records the results of a run.

set -u
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
IONC="${IONC:-$REPO/src/ionc/bin/Debug/net10.0/ionc.dll}"
WORK="${WORK:-$(mktemp -d)}"

[ -f "$IONC" ] || { echo "ionc.dll not found at $IONC — run: dotnet build src/ionc/ionc.csproj"; exit 1; }

probe() {
  local name="$1" v1="$2" v2="$3"
  local dir="$WORK/$name"
  rm -rf "$dir"; mkdir -p "$dir/Contracts"; cd "$dir" || return
  printf '{ "name": "LockProbe", "features": ["std"], "generators": { "dotnet": { "features": ["models"], "outputs": "./" } } }' > ion.config.json

  printf '%s\n' "$v1" > Contracts/P.ion
  dotnet "$IONC" lock init > init.log 2>&1

  printf '%s\n' "$v2" > Contracts/P.ion
  cp ion.lock.json before.lock.json
  dotnet "$IONC" lock check > check.log 2>&1
  local code=$?

  local changed; changed=$(cmp -s before.lock.json ion.lock.json && echo "lock-kept" || echo "LOCK-REWRITTEN")
  # ionc writes UTF-16 to the console on Windows; strip the NULs before grepping.
  local diag; diag=$(tr -d '\0' < check.log | grep -oE "(error|warning)\[ION[0-9]{4}\][^|]*" | tr -s ' ' | cut -c1-130 | sort -u)
  local first; first=$(printf '%s\n' "${diag:-(no ION diagnostic)}" | head -1)
  printf '%-24s exit=%-4s %-14s %s\n' "$name" "$code" "$changed" "$first"
  if [ -n "$diag" ]; then
    printf '%s\n' "$diag" | tail -n +2 | sed 's/^/                                                 /'
  fi
  return 0
}

S='service S() { Do(m: M): i4; }'

probe append              "msg M { a: i4; }
$S"                                                       "msg M { a: i4; b: string; }
$S"
probe optional-add        "msg M { a: i4; }
$S"                                                       "msg M { a: i4; b: string?; }
$S"
probe insert-middle       "msg M { a: i4; c: i4; }
$S"                                                       "msg M { a: i4; b: i4; c: i4; }
$S"
probe remove-field        "msg M { a: i4; b: i4; }
$S"                                                       "msg M { a: i4; }
$S"
probe reorder-fields      "msg M { a: i4; b: string; }
$S"                                                       "msg M { b: string; a: i4; }
$S"
probe retype-field        "msg M { a: i4; b: string; }
$S"                                                       "msg M { a: i4; b: i4; }
$S"
probe widen-field         "msg M { a: i4; }
$S"                                                       "msg M { a: i8; }
$S"
probe rename-field        "msg M { a: i4; b: string; }
$S"                                                       "msg M { a: i4; z: string; }
$S"
probe optional-tighten    "msg M { a: i4; b: string?; }
$S"                                                       "msg M { a: i4; b: string; }
$S"
probe array-to-fixed      "msg M { xs: i4[]; }
$S"                                                       "msg M { xs: i4[3]; }
$S"
probe fixed-resize        "msg M { xs: i4[3]; }
$S"                                                       "msg M { xs: i4[4]; }
$S"
probe msg-remove          "msg M { a: i4; }
msg N { b: i4; }
service S() { Do(m: M): i4; Two(n: N): i4; }"             "msg M { a: i4; }
$S"

probe enum-add            "enum E : u1 { A = 0 }
msg M { e: E; }
$S"                                                       "enum E : u1 { A = 0, B = 1 }
msg M { e: E; }
$S"
probe enum-remove         "enum E : u1 { A = 0, B = 1 }
msg M { e: E; }
$S"                                                       "enum E : u1 { A = 0 }
msg M { e: E; }
$S"
probe enum-renumber       "enum E : u1 { A = 0, B = 1 }
msg M { e: E; }
$S"                                                       "enum E : u1 { A = 1, B = 0 }
msg M { e: E; }
$S"
probe enum-basetype       "enum E : u1 { A = 0 }
msg M { e: E; }
$S"                                                       "enum E : u4 { A = 0 }
msg M { e: E; }
$S"
probe enum-rename-member  "enum E : u1 { A = 0, B = 1 }
msg M { e: E; }
$S"                                                       "enum E : u1 { A = 0, Renamed = 1 }
msg M { e: E; }
$S"

probe union-add-case      "union U { A(x: i4), B(y: i4) }
msg M { u: U; }
$S"                                                       "union U { A(x: i4), B(y: i4), C(z: i4) }
msg M { u: U; }
$S"
probe union-reorder-cases "union U { A(x: i4), B(y: i4) }
msg M { u: U; }
$S"                                                       "union U { B(y: i4), A(x: i4) }
msg M { u: U; }
$S"
probe union-rename-case   "union U { A(x: i4), B(y: i4) }
msg M { u: U; }
$S"                                                       "union U { Renamed(x: i4), B(y: i4) }
msg M { u: U; }
$S"
probe union-case-append   "union U { A(x: i4) }
msg M { u: U; }
$S"                                                       "union U { A(x: i4, y: i4) }
msg M { u: U; }
$S"
probe union-case-insert   "union U { A(x: i4, z: i4) }
msg M { u: U; }
$S"                                                       "union U { A(x: i4, y: i4, z: i4) }
msg M { u: U; }
$S"
probe union-case-retype   "union U { A(x: i4) }
msg M { u: U; }
$S"                                                       "union U { A(x: string) }
msg M { u: U; }
$S"

probe method-add-arg      "msg M { a: i4; }
$S"                                                       "msg M { a: i4; }
service S() { Do(m: M, extra: i4): i4; }"
probe method-arg-retype   "msg M { a: i4; }
service S() { Do(m: M, k: i4): i4; }"                     "msg M { a: i4; }
service S() { Do(m: M, k: string): i4; }"
probe method-retype       "msg M { a: i4; }
$S"                                                       "msg M { a: i4; }
service S() { Do(m: M): i8; }"
probe method-remove       "msg M { a: i4; }
service S() { Do(m: M): i4; Gone(m: M): i4; }"            "msg M { a: i4; }
$S"
probe method-rename       "msg M { a: i4; }
$S"                                                       "msg M { a: i4; }
service S() { Renamed(m: M): i4; }"

echo
echo "work dir: $WORK"
