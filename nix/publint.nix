{
  buildNpmPackage,
  lib,
  makeWrapper,
  nodejs,
  runCommand,
}:
buildNpmPackage (finalAttrs: {
  pname = "publint";
  version = "0.3.12";

  # `publint` is consumed only as a standalone CLI (dev shell + flake checks),
  # so we install it through a thin wrapper package that depends on the pinned
  # release rather than adding it to our own pnpm workspace. `version` is the
  # single source of truth: it feeds the wrapper's `dependencies` entry below
  # and the committed `publint-package-lock.json` captures the exact runtime
  # closure. Bump both at once with `just update-publint [<version>]`, which
  # regenerates the lockfile and refreshes `npmDepsHash` via `nix-update`.
  src = runCommand "publint-npm-src" { } ''
    mkdir -p "$out"
    cat > "$out/package.json" <<EOF
    {"name":"publint-nix","version":"0.0.0","private":true,"dependencies":{"publint":"${finalAttrs.version}"}}
    EOF
    cp ${./publint-package-lock.json} "$out/package-lock.json"
  '';

  npmDepsHash = "sha256-ePXOE6HgUiLJ5FN7DBbULGhypS+U/1phCkp6VTrFSnE=";

  # The wrapper has nothing to compile; npm only needs to materialize
  # node_modules from the lockfile, and publint ships no install scripts.
  dontNpmBuild = true;
  npmFlags = [ "--ignore-scripts" ];
  makeCacheWritable = true;

  nativeBuildInputs = [ makeWrapper ];

  installPhase = ''
    runHook preInstall

    toolRoot="$out/lib/publint"
    mkdir -p "$toolRoot" "$out/bin"
    cp -R node_modules "$toolRoot/node_modules"
    makeWrapper "$toolRoot/node_modules/.bin/publint" "$out/bin/publint" \
      --prefix PATH : ${lib.makeBinPath [ nodejs ]}

    runHook postInstall
  '';

  meta = {
    description = "Lint packaging errors";
    homepage = "https://publint.dev";
    license = lib.licenses.mit;
    mainProgram = "publint";
    platforms = lib.platforms.all;
  };
})
