#!/bin/sh
# Fuzzes the code that reads what anyone in radio range sends: OWL's receive
# path, as patched (from build/awdl/owl, which configuring awdl/ makes), and
# the frame fixup, service bridge, host frame and TCP merge code in awdl/win.
# Linux and clang only, with AddressSanitizer and UndefinedBehaviorSanitizer.
# Two kinds of undefined behaviour are left out: OWL reads fields through
# unaligned pointers, and radiotap shifts 1 into the sign bit; both do what
# they mean on x86-64.
#   awdl/fuzz/run.sh [seconds per target, default 60]
set -e
seconds=${1:-60}
root=$(cd "$(dirname "$0")/../.." && pwd)
owl=$root/build/awdl/owl
win=$root/awdl/win
out=$root/build/fuzz
[ -d "$owl/src" ] || { echo "configure awdl/ first: cmake -S awdl -B build/awdl" >&2; exit 2; }
mkdir -p "$out"
cd "$out"
flags="-g -O1 -fsanitize=fuzzer,address,undefined -fno-sanitize=alignment -fno-sanitize-recover=undefined"

clang -g -O1 -c -fsanitize=fuzzer-no-link,address,undefined -fno-sanitize=shift,alignment -w \
	-I"$owl/radiotap" "$owl/radiotap/radiotap.c" -o radiotap.o
owl_sources=""
for name in log state election sync channel schedule tx rx frame crc32 wire peers version \
	hashmap siphash24 circular_buffer; do
	owl_sources="$owl_sources $owl/src/$name.c"
done
# shellcheck disable=SC2086
clang $flags -w -I"$owl/src" -I"$owl/radiotap" "$root/awdl/fuzz/fuzz_owl.c" $owl_sources radiotap.o \
	-o fuzz_owl
# shellcheck disable=SC2086
clang $flags -I"$win" "$root/awdl/fuzz/fuzz_frames.c" "$win/frame_fixup.c" \
	"$win/service_bridge.c" "$win/host_frame.c" -o fuzz_frames
# shellcheck disable=SC2086
clang $flags -I"$win" "$root/awdl/fuzz/fuzz_merge.c" "$win/tcp_merge.c" -o fuzz_merge

mkdir -p corpus/owl corpus/frames corpus/merge
cp "$root"/awdl/fuzz/seeds/owl/* corpus/owl/
./fuzz_owl -max_total_time="$seconds" -use_value_profile=1 corpus/owl
./fuzz_frames -max_total_time="$seconds" -use_value_profile=1 corpus/frames
./fuzz_merge -max_total_time="$seconds" -use_value_profile=1 -max_len=70000 corpus/merge
