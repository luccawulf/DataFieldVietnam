# Assembles the Battlefield Vietnam hooks and regenerates Bf1942ClientPatches.cs.
#
# Mirrors upstream's assemble_patches.bat, with two differences forced by BFV:
#   * .text has no code cave, so the hook bodies live in the .tls tail and the installer has to flip
#     that section to executable -- a 4-byte header patch with no assembly behind it.
#   * VA -> file offset differs per section, so offsets are computed rather than assumed:
#       .text file = VA - 0x400C00 ; .tls file = VA - 0x4E6A00

$ErrorActionPreference = "Stop"

$nasm = "C:\Users\lucas\AppData\Local\bin\NASM\nasm.exe"
$root = $PSScriptRoot
$out  = Join-Path $root "..\..\DataField42.Core\Models\Bf1942ClientPatches.cs"

$TEXT_DELTA = 0x400C00
$TLS_DELTA  = 0x4E6A00

# name, VA, VA->file delta
$targets = @(
    @{ File = "call hook sendto stash.asm";     Va = 0x8D8ED8;  Delta = $TEXT_DELTA },
    @{ File = "hook function sendto stash.asm"; Va = 0xE23018;  Delta = $TLS_DELTA  },
    @{ File = "call hook missing map.asm";      Va = 0x527CE5;  Delta = $TEXT_DELTA },
    @{ File = "hook function missing map.asm";  Va = 0xE23050;  Delta = $TLS_DELTA  }
)

if (-not (Test-Path $nasm)) { throw "nasm not found at $nasm" }

$entries = @()
foreach ($t in $targets) {
    $src = Join-Path $root $t.File
    $raw = Join-Path $env:TEMP ("bfv_" + [IO.Path]::GetFileNameWithoutExtension($t.File) + ".raw")
    Write-Host "Assembling $($t.File) @ VA 0x$('{0:X}' -f $t.Va) ..."
    # Forward slashes and no trailing backslash: a path ending in \" is read as an escaped quote by
    # the native argument parser and swallows the following argument.
    $inc = ($root -replace '\\', '/') + '/'
    & $nasm -f bin -I $inc $src -o $raw
    if ($LASTEXITCODE -ne 0) { throw "nasm failed on $($t.File)" }

    $bytes = [IO.File]::ReadAllBytes($raw)
    $fileOff = $t.Va - $t.Delta
    Write-Host ("  -> file 0x{0:X}  ({1} bytes)" -f $fileOff, $bytes.Length)
    $entries += @{ Offset = $fileOff; Bytes = $bytes; Note = $t.File }
    Remove-Item $raw -Force
}

# Make .tls executable: Characteristics 0xC0000040 (READ|WRITE|initialised data) -> add MEM_EXECUTE.
# Without this the hook bodies are mapped but not executable and the first call faults.
$entries += @{ Offset = 0x2EC; Bytes = [byte[]](0x40, 0x00, 0x00, 0xE0); Note = ".tls Characteristics -> executable" }

# Record what each edit overwrites, read from the PRISTINE RETAIL executable.
#
# It has to be the untouched retail copy, not a working one. The local BfVietnam.exe.bak already carries
# years of patches -- 255 bots, the OpenSpy master, large-address-aware -- so reading originals from it
# would record "already patched" as the original: revert would write the patched bytes back, and a stock
# exe would be misread as an unknown build. Only "Original Files" is genuinely unmodified.
#
# Both executables are the game's own and are not redistributable, so their locations are configurable
# rather than baked in. Set BFV_PRISTINE_EXE / BFV_PATCHED_EXE to run this on another machine; the
# defaults are the maintainer's layout.
$pristineExe = if ($env:BFV_PRISTINE_EXE) { $env:BFV_PRISTINE_EXE }
               else { "D:\Games\EA GAMES\Battlefield Vietnam Original Files\BfVietnam.exe" }
if (-not (Test-Path $pristineExe)) {
    throw "Pristine retail BfVietnam.exe v1.21 not found at '$pristineExe'. Set BFV_PRISTINE_EXE to point at one."
}
$pristine = [IO.File]::ReadAllBytes($pristineExe)

function New-Edit($offset, [byte[]]$bytes, $note) {
    # Force an int: a hex literal passed in argument position arrives as a string, which then formats
    # back out as "0x0x...." in the generated source.
    $off = if ($offset -is [string]) { [Convert]::ToInt32($offset, 16) } else { [int]$offset }
    $orig = New-Object byte[] $bytes.Length
    [Array]::Copy($pristine, $off, $orig, 0, $bytes.Length)
    @{ Offset = $off; Bytes = $bytes; Original = $orig; Note = $note }
}

$openspy = [Text.Encoding]::ASCII.GetBytes("openspy.net")

# The cave-based patches are lifted verbatim from the author's own patched executable rather than
# reassembled. His caves sit in an appended .ctfsnd section based at VA 0xE25000, and a section we append
# to a stock exe lands at exactly the same address (pristine ends at .rsrc 0xE24000+0xD60, which aligns
# up to 0xE25000) -- so his code and its jump displacements are correct here without touching them.
$authorExe = if ($env:BFV_PATCHED_EXE) { $env:BFV_PATCHED_EXE }
             else { "D:\Games\EA GAMES\Battlefield Vietnam\BfVietnam.exe" }
if (-not (Test-Path $authorExe)) {
    throw "Patch-kit BfVietnam.exe not found at '$authorExe'. Set BFV_PATCHED_EXE to point at one."
}
$author = [IO.File]::ReadAllBytes($authorExe)

function Get-AuthorBytes($offset, $length) {
    $b = New-Object byte[] $length
    [Array]::Copy($author, $offset, $b, 0, $length)
    ,$b    # comma stops PowerShell unrolling the array into the pipeline
}

# Everything the menu offers. The first is assembled from the .asm files above; the rest are plain byte
# edits recovered by diffing the author's patched executables against the pristine retail one.
$patches = @(
    @{ Id = "autodownload"
       Name = "Automatic map and mod download"
       Description = "Hands missing maps and mods to DataField Vietnam instead of showing MAP NOT FOUND, so they download and the game rejoins the server by itself."
       Edits = @($entries | ForEach-Object { New-Edit $_.Offset $_.Bytes $_.Note }) }

    # One 17-byte region, not two immediates. Retail clamps with a branch
    #   cmp eax,0x40 / mov byte [ecx+0xa0],0 / jle +5 / mov eax,0x40
    # and the author's current build replaces the whole thing with a branchless clamp
    #   mov edx,0xff / cmp eax,edx / cmovg eax,edx / mov byte [ecx+0xa0],0
    # which happens to be exactly the same length. An earlier version of his patch only swapped the two
    # 0x40 immediates; defining it that way reads the middle of a `mov edx` and reports the executable as
    # unrecognised, so the region is taken verbatim from the current build instead.
    @{ Id = "playerlimit"
       Name = "255 player and bot limit"
       Description = "Raises the co-op player and bot cap from the stock 64 to 255, by replacing the clamp that holds it down."
       Edits = @(
           (New-Edit 0x5975D (Get-AuthorBytes 0x5975D 17) "64-clamp -> branchless 255-clamp")) }

    @{ Id = "openspy"
       Name = "OpenSpy master server"
       Description = "Points the server browser at OpenSpy instead of the shut-down GameSpy master. Without this there is no working server list at all."
       Edits = @(
           (New-Edit 0x7765C1 $openspy "gamespy.com -> openspy.net"),
           (New-Edit 0x77660E $openspy "gamespy.com -> openspy.net"),
           (New-Edit 0x776BE0 $openspy "gamespy.com -> openspy.net"),
           (New-Edit 0x8C2942 $openspy "gamespy.com -> openspy.net")) }

    @{ Id = "largeaddress"
       Name = "4 GB memory"
       Description = "Sets the large-address-aware flag so the game can use more than 2 GB of memory, which helps with large texture packs. The retail exe ships with a zero PE checksum, so nothing needs recomputing."
       Edits = @(
           (New-Edit 0x00016E ([byte[]](0x2F)) "COFF Characteristics += IMAGE_FILE_LARGE_ADDRESS_AWARE")) }

    # The disc check is one feature spread over five sites: the check itself (0x434000) is stubbed to
    # return "disc present", and the three places that call it have their reject branches forced. That
    # is why removing the disc requirement also fixes the crash when loading a second map offline --
    # the same check runs again mid-load.
    @{ Id = "nocd"
       Name = "No CD required"
       Description = "Starts the game without the disc, stops it crashing when it re-checks for the disc while loading a second map offline, and drops the integrity check on the mod's Mod.dll so a replaced or mod-supplied one is accepted."
       Edits = @(
           (New-Edit 0x33400 (Get-AuthorBytes 0x33400 4)  "disc check 0x434000 -> mov al,1; ret 0x1C"),
           (New-Edit 0x33464 (Get-AuthorBytes 0x33464 5)  "inner call -> mov eax,1"),
           (New-Edit 0xE6694 (Get-AuthorBytes 0xE6694 3)  "call site 0x4E7294: jne -> jmp"),
           (New-Edit 0xEB8C2 (Get-AuthorBytes 0xEB8C2 2)  "call site 0x4EC4C2: jne -> nop + jmp"),
           (New-Edit 0x12777F (Get-AuthorBytes 0x12777F 3) "call site 0x52837F: jne -> jmp"),
           # Identified by the strings around it: INSERT_CD, INSERT_CD_XPACK1, INSERT_CD_XPACK2.
           (New-Edit 0x1CF27C (Get-AuthorBytes 0x1CF27C 6) "insert-disc prompt: jne -> jmp"),
           # The other half of the disc requirement: the last gate in the startup routine at 0x440E80
           # CRCs mods/<mod>/Mod.dll and refuses to start unless it hashes to 0x3B96ECB5, the stock
           # 920,592-byte file. That is exactly the file a no-disc install replaces, so stubbing the
           # disc check alone still left the game quitting at boot. The first edit stops a failed read
           # diverting to the abort block; the second takes the success path unconditionally.
           (New-Edit 0x40D02 (Get-AuthorBytes 0x40D02 1) "Mod.dll CRC: je 0x441911 -> je +0"),
           (New-Edit 0x40D0B (Get-AuthorBytes 0x40D0B 5) "Mod.dll CRC: je 0x4419BD -> jmp 0x4419BD")) }

    @{ Id = "onlinecompat"
       Name = "Post-GameSpy online compatibility"
       Description = "Writes the fixed session constant the surviving master servers expect. Together with the OpenSpy patch this is what makes the server browser and joining work at all now."
       Edits = @(
           (New-Edit 0x11A295 (Get-AuthorBytes 0x11A295 9)  "-> mov ecx,0x886F0B2B; mov [edx],ecx"),
           (New-Edit 0x12790A (Get-AuthorBytes 0x12790A 12) "-> mov ecx,0x886F0B2B; mov [edx],ecx")) }

    @{ Id = "rentbutton"
       Name = "Rent Server button link"
       Description = "Points the menu's Rent Server button at a community site instead of the original address, which no longer exists."
       Edits = @((New-Edit 0x75CFD0 (Get-AuthorBytes 0x75CFD0 28) "rent server URL")) }

    @{ Id = "datadiffersmodal"
       Name = "Skip the 'data differs' popup"
       Description = "Hides the dialog shown when a server reports mismatched files. It only suppresses the popup -- it does not change whether the server disconnects you."
       Edits = @((New-Edit 0x103D54 (Get-AuthorBytes 0x103D54 4) "-> cmp al,0xA; je (skip the modal)")) }

    # Grouped by locality rather than by a confirmed decode: all three sit in the same routine and are
    # applied together in the author's build. The effect is his "unlocked debug commands", but the exact
    # gate each one removes has not been traced individually.
    @{ Id = "debugcommands"
       Name = "Unlocked debug commands"
       Description = "Removes the gates on the extra console and debug commands so they can be used in single player."
       Edits = @(
           (New-Edit 0x4A9CD4 (Get-AuthorBytes 0x4A9CD4 6) "je -> nops"),
           (New-Edit 0x4A9E14 (Get-AuthorBytes 0x4A9E14 1) "test al,4 -> test al,0"),
           (New-Edit 0x4A9E76 (Get-AuthorBytes 0x4A9E76 1) "jne -> jmp")) }

    # One byte, and it is a typo in the original game. BasicPhysicsSystem keeps the world wind vector
    # at +0x0C: the setter behind "physics.wind" writes there, but the getter every part of the physics
    # code calls to read it reads +0x18 instead -- a field the constructor zeroes and nothing ever
    # writes. So retail always handed out (0,0,0) and the command did nothing. Its two neighbouring
    # accessor pairs (gravity, and the float at +0x30) are both symmetric, which is what makes this one
    # stand out as a slip rather than a design. Pointing the getter back at +0x0C reconnects them.
    @{ Id = "physicswind"
       Name = "Working physics.wind command"
       Description = "Makes the physics.wind console command do something. The game stored the wind you set but read it back from the wrong place, so it was always zero -- this reconnects the two, and wind reaches the physics simulation."
       Edits = @(
           (New-Edit 0x2F0616 (Get-AuthorBytes 0x2F0616 1) "BasicPhysicsSystem::getWind: add ecx,0x18 -> 0x0C")) }

    # Each of these redirects one instruction into a 0x80-byte slice of the cave section. The slices are
    # separate so the two can be turned on and off independently, and neither overlaps the author's
    # canvas-resize cave at 0xE25300, which his own notes rule out as broken.
    @{ Id = "widescreen3d"
       Name = "Widescreen 3D aspect"
       Description = "Makes the 3D world use the real screen aspect instead of a fixed 4:3, so the view is not stretched at widescreen resolutions."
       Edits = @((New-Edit 0x636348 (Get-AuthorBytes 0x636348 5) "mov eax,1.0f -> jmp cave"))
       Cave = @{ SectionVa = 0xE25000; SectionSize = 0x800; ContentVa = 0xE25400; Contents = (Get-AuthorBytes 0x93DA00 0x80) } }

    # Two string pointers that retail leaves pointing at an empty string, aimed at the red and blue
    # control-point icon textures instead. Part of the CTF flag display work.
    @{ Id = "ctficons"
       Name = "CTF control point icons"
       Description = "Gives control points their red and blue icons, which retail leaves unset. Part of Capture The Flag support; the textures have to exist in the mod being played."
       Edits = @(
           (New-Edit 0x14ACCC (Get-AuthorBytes 0x14ACCC 4) "-> conp_Red.dds"),
           (New-Edit 0x14ACE1 (Get-AuthorBytes 0x14ACE1 4) "-> conp_Blue.dds")) }

    # Four hooks that share one block of cave code, so they ship as a single toggle: the dispatcher at
    # 0xE250C0 CALLS a helper at 0xE25040, and the hook at 0xE25190 reads state at 0xE25140/48/50.
    # Splitting them would leave a hook calling a helper that was never written. The block is taken as
    # 0xE25000..0xE25300, which covers every piece (non-zero content runs 0xE25004..0xE2524E) and stops
    # short of the canvas-resize cave at 0xE25300 that the author's own notes rule out as broken.
    @{ Id = "widescreenres"
       Name = "HD resolutions and refresh rates"
       Description = "Unlocks the resolutions and refresh rates the menu will offer, instead of the short hardcoded retail list."
       Edits = @(
           (New-Edit 0x1C6B55 (Get-AuthorBytes 0x1C6B55 7)  "jump table -> jmp cave 0xE250C0"),
           (New-Edit 0x9E98D  (Get-AuthorBytes 0x9E98D 26)  "mode compare -> jmp cave 0xE25190"),
           (New-Edit 0x14700C (Get-AuthorBytes 0x14700C 6)  "mov cl,[esi+0xBD] -> jmp cave 0xE25200"),
           (New-Edit 0x147106 (Get-AuthorBytes 0x147106 6)  "je -> jmp cave 0xE25220"),
           # A call retargeted from 0x6843E0 into the cave at 0xE25160. Without it that part of the
           # shipped block is code nothing ever reaches.
           (New-Edit 0x2D4FF7 (Get-AuthorBytes 0x2D4FF7 5)  "call 0x6843E0 -> call cave 0xE25160"),
           # Same routine as the call above, and it tests [edi+0xBD] -- the byte the cave at 0xE25200
           # also reads -- so it belongs with this family rather than standing alone.
           (New-Edit 0x2D51C7 (Get-AuthorBytes 0x2D51C7 2)  "jne -> nops ([edi+0xBD] display flag)"),
           # The menu's own mode. After the UI state machine commits a new state, it reprograms the
           # display to suit it; retail sends state 0 (BattlefieldMenu) to a stub that forces
           # 800x600x32 and zeroes the refresh rate, while every other state uses the mode the user
           # picked. Retargeting the call to the neighbouring helper makes the menu use that mode too,
           # so the front end runs at the chosen resolution and refresh rate and the mode no longer
           # flips back and forth on every menu-to-game transition.
           (New-Edit 0x905E7 (Get-AuthorBytes 0x905E7 1)    "menu mode: call 0x509E80 (800x600) -> 0x509EB0 (chosen mode)"),
           # The filter that kept every non-4:3 mode out of the list in the first place. The mode
           # enumerator computes height/width and compares it against the double 0.75 at 0xB5FC38
           # (= 480/640); anything that is not exactly 4:3 was thrown away before it ever reached the
           # menu. Six NOPs over the jump drop the test and leave the 640x480 minimums intact. This is
           # what makes a 16:9 or 16:10 mode selectable at all.
           (New-Edit 0x10747C (Get-AuthorBytes 0x10747C 6)  "resolution list: drop the 'aspect must be 4:3' filter"))
       Cave = @{ SectionVa = 0xE25000; SectionSize = 0x800; ContentVa = 0xE25000; Contents = (Get-AuthorBytes 0x93D600 0x300) } }

    # Long carried as "the weapon viewmodel fix", which the disassembly does not support. 0x00A41B67 is
    # argument 2 of a one-shot default projection built in the render-state manager's init routine
    # (0xA411A0, reached only from 0xA43650 on device create or mode set). Its matrix builder 0xA3FFE0
    # has a single call site in the whole executable, and the state block it seeds is overwritten by a
    # real camera projection before any geometry is drawn. Every projection the weapon could use comes
    # from Camera::UpdateProjectionMatrix instead -- which is what widescreen3d patches. So this most
    # likely changes nothing visible, and is kept only for parity with the author's build.
    @{ Id = "viewmodelaspect"
       Name = "Renderer startup projection aspect"
       Description = "Computes the renderer's initial projection from the real backbuffer instead of assuming 4:3. This is only the default installed when the device is created; the camera replaces it before anything is drawn, so it is unlikely to change what you see. Widescreen 3D aspect is the patch that governs the view, including the weapon."
       Edits = @((New-Edit 0x640F67 (Get-AuthorBytes 0x640F67 5) "push 0.75f -> jmp cave"))
       Cave = @{ SectionVa = 0xE25000; SectionSize = 0x800; ContentVa = 0xE25480; Contents = (Get-AuthorBytes 0x93DA80 0x80) } }
)

$lines = @(
    '/// <summary>'
    '/// The executable patches DataField Vietnam can apply to BfVietnam.exe.'
    '/// </summary>'
    '/// <remarks>'
    '/// This file was generated by assemble_patches_bfv.ps1. Do not edit manually.'
    '///'
    '/// Unlike BF1942, BFV has no code cave in .text, so the hook bodies sit in the .tls tail and one'
    '/// edit flips that section to executable. Each edit carries the bytes it overwrites as well as the'
    '/// ones it writes, so the client can verify the executable, revert cleanly, and notice a hook left'
    '/// over from an older build.'
    '/// </remarks>'
    'internal static class Bf1942ClientPatches'
    '{'
    '    internal static readonly GamePatch[] All ='
    '    {'
)
foreach ($p in $patches) {
    $lines += '        new GamePatch('
    $lines += "            Id: `"$($p.Id)`","
    $lines += "            Name: `"$($p.Name)`","
    $lines += "            Description: `"$($p.Description)`","
    $lines += '            Edits: new[]'
    $lines += '            {'
    foreach ($e in $p.Edits) {
        $patched  = ($e.Bytes    | ForEach-Object { '0x{0:X2}' -f $_ }) -join ', '
        $original = ($e.Original | ForEach-Object { '0x{0:X2}' -f $_ }) -join ', '
        $lines += "                // $($e.Note)"
        $lines += "                new PatchEdit(0x$('{0:X}' -f $e.Offset),"
        $lines += "                    Original: new byte[] { $original },"
        $lines += "                    Patched:  new byte[] { $patched }),"
    }
    if ($p.Cave) {
        $c = $p.Cave
        $contents = ($c.Contents | ForEach-Object { '0x{0:X2}' -f $_ }) -join ', '
        $lines += '            },'
        $lines += '            Cave: new CaveSection('
        $lines += '                Name: ".dfvcave",'
        $lines += "                SectionVirtualAddress: 0x$('{0:X}' -f $c.SectionVa),"
        $lines += "                SectionSize: 0x$('{0:X}' -f $c.SectionSize),"
        $lines += "                ContentVirtualAddress: 0x$('{0:X}' -f $c.ContentVa),"
        $lines += "                Contents: new byte[] { $contents })),"
    } else {
        $lines += '            }),'
    }
}
$lines += '    };'
$lines += '}'

[IO.File]::WriteAllLines((Resolve-Path $out), $lines, [Text.Encoding]::UTF8)
Write-Host ""
$editCount = ($patches | ForEach-Object { $_.Edits.Count } | Measure-Object -Sum).Sum
Write-Host "Written $out ($($patches.Count) patches, $editCount edits)"
Write-Host ""
Write-Host "NEXT: rebuild and redeploy the client BEFORE running 'DataField42.exe install'." -ForegroundColor Yellow
Write-Host "This table is compiled into DataField42.exe. Regenerating it here changes nothing on disk"  -ForegroundColor Yellow
Write-Host "until the client is republished, and 'install' will happily write the previous table."       -ForegroundColor Yellow
Write-Host "  dotnet publish DataField42\DataField42.csproj -c Release"                                  -ForegroundColor Yellow
Write-Host "  copy the published DataField42.exe into the game folder, then run it with 'install'"       -ForegroundColor Yellow
Write-Host "(Close any open DataField42 window first -- it self-elevates and will hold the file.)"       -ForegroundColor Yellow
