use std/assert
source ensure-native-binary.nu
assert equal (nix_build_attr linux x64) ccusage-static
assert equal (nix_build_attr linux arm64) ccusage-static
assert equal (nix_build_attr darwin x64) ccusage-darwin-x64
assert equal (nix_build_attr darwin arm64) ccusage
assert equal (nix_build_attr win32 x64) null
