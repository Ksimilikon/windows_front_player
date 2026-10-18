<#
Builds the player core (rs_dream_player) in release for this frontend.

The core has source files named `con.rs`; CON is a reserved device name on
Windows, so a Windows checkout of the core lacks them and doesn't build. This
script exports a git ref of the core into a build folder, stores every
`con.rs` as `con_win.rs` (the module is pointed at it with `#[path]`) and
builds there. The core repository itself is not modified, only its
`target\release\dream_player.dll` is replaced (the previous one is kept as
`.bak`) — where DreamPlayer.csproj takes it from.

  .\scripts\build-core.ps1                      # origin/main, fetched first
  .\scripts\build-core.ps1 -Ref ksimilikon/tui  # another ref
#>
param(
    [string]$Ref = "origin/main",
    [string]$Core = (Join-Path $PSScriptRoot "..\..\rs_dream_player"),
    # sources + cargo cache; kept between runs so rebuilds are incremental.
    [string]$BuildDir = (Join-Path $env:LOCALAPPDATA "DreamPlayer\core-build")
)

$ErrorActionPreference = "Stop"
$Core = (Resolve-Path $Core).Path
$src = Join-Path $BuildDir "src"
$target = Join-Path $BuildDir "target"

function Invoke-Git { & git.exe -C $Core @args; if ($LASTEXITCODE -ne 0) { throw "git $args failed" } }

if ($Ref -like "origin/*") {
    Write-Host "fetching origin..."
    Invoke-Git fetch origin
}
$commit = (Invoke-Git rev-parse --short $Ref)
Write-Host "core $Ref ($commit) -> $src"

# export the ref without the files Windows can't create.
if (Test-Path $src) { Remove-Item -Recurse -Force $src }
New-Item -ItemType Directory -Force $src | Out-Null
$reserved = @(Invoke-Git ls-tree -r --name-only $Ref | Where-Object { $_ -match '(^|/)con\.rs$' })
$tar = Join-Path $BuildDir "core.tar"
Invoke-Git archive --format=tar -o $tar $Ref
$exclude = $reserved | ForEach-Object { "--exclude=$_" }
& tar -xf $tar -C $src @exclude
if ($LASTEXITCODE -ne 0) { throw "tar failed" }
Remove-Item $tar

# each con.rs -> con_win.rs, and `mod con;` next to it gets a #[path].
$utf8 = New-Object System.Text.UTF8Encoding $false
foreach ($file in $reserved) {
    $dir = Join-Path $src (Split-Path $file -Parent)
    # cmd redirection keeps the bytes as they are (PowerShell 5 would re-encode).
    cmd /c "git -C `"$Core`" show `"${Ref}:$file`" > `"$(Join-Path $dir 'con_win.rs')`""
    if ($LASTEXITCODE -ne 0) { throw "git show $file failed" }
    foreach ($name in "lib.rs", "mod.rs", "main.rs") {
        $parent = Join-Path $dir $name
        if (-not (Test-Path $parent)) { continue }
        $text = [IO.File]::ReadAllText($parent)
        $patched = [regex]::Replace($text, '(?m)^([ \t]*)((pub(\([^)]*\))?\s+)?mod con;)',
            "`$1#[path = `"con_win.rs`"]`n`$1`$2")
        if ($patched -ne $text) {
            [IO.File]::WriteAllText($parent, $patched, $utf8)
            Write-Host "  $file -> con_win.rs ($name)"
        }
    }
}

Write-Host "cargo build --release..."
$env:CARGO_TARGET_DIR = $target
& cargo build --release --manifest-path (Join-Path $src "Cargo.toml")
if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }

$dll = Join-Path $target "release\dream_player.dll"
$out = Join-Path $Core "target\release"
New-Item -ItemType Directory -Force $out | Out-Null
$dest = Join-Path $out "dream_player.dll"
if (Test-Path $dest) { Copy-Item $dest "$dest.bak" -Force }
Copy-Item $dll $dest -Force
Write-Host "done: $dest (core $commit)"
