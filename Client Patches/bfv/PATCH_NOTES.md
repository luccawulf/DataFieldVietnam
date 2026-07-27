# Notes on the BfVietnam.exe patches

Findings from disassembling the patches in `The_Complete_Vietnam_Patch_Kit_2026` while wiring them into
DataField Vietnam's patch menu. Everything below was derived from the binaries themselves — the pristine
retail exe in `Battlefield Vietnam Original Files` and the patched one in the live game folder — and the
conclusions were each re-derived by a second pass working independently.

## A bug worth fixing in the kit: 0xE6694 jumps into the middle of an instruction

One of the six call sites of the disc check `FUN_00434000` is patched with the wrong displacement.

| | file | bytes | resolves to |
|---|---|---|---|
| retail | 0xE6694 | `0F 85 AB 00 00 00` | `jne 0x004E7345` |
| patched | 0xE6694 | `E9 B0 00 00 00 00` | `jmp 0x004E7349` |

Turning `0F 85 disp` into `E9 disp` shortens the instruction by one byte, so the displacement has to go up
by one — `0xAB` should have become `0xAC`, landing on the same `0x004E7345`. It became `0xB0` instead,
which is four bytes further on, in the middle of `A1 50 59 D4 00  mov eax,[0x00D45950]`. Disassembling
from there gives `add [ebp-0x7d],dl` followed by `in al,dx` — garbage that would fault.

Its twin one site over is done correctly and shows the intended idiom:

| | file | bytes | resolves to |
|---|---|---|---|
| retail | 0xEB8C2 | `0F 85 B2 00 00 00` | `jne 0x004EC57A` |
| patched | 0xEB8C2 | `90 E9 B2 00 00 00` | `jmp 0x004EC57A` — same target, padded with a leading NOP |

Because the stubbed check now always returns 1, the branch is always taken, so this would crash *if the
path ran*. It evidently does not: the containing function (starting around VA 0x004E6F90) has no rel32
callers and is not referenced through any pointer table, so it looks like dead or indirectly-reached code.

**DataField Vietnam ships these bytes verbatim, bug included, and that is deliberate.** The patch table
doubles as the detector: if it wrote corrected bytes, every exe already patched with the kit — including
the author's own — would match neither the "original" nor the "patched" pattern, and the whole No-CD entry
would report as unrecognised. Fixing it here would break state detection for existing users to repair a
path that appears unreachable. The right place to fix it is the kit itself; then this table follows.

## "Weapon viewmodel aspect" is misattributed

The kit lists the hook at VA 0x00A41B67 as the first-person weapon fix. The disassembly does not support
that, and the patch is very likely inert.

`0x00A41B67` pushes `0.75f` as argument 2 of a four-argument perspective build:

```
0x00A41B57  push 0x44FA0000        ; far  = 2000.0
0x00A41B62  push 0x3DCCCCCD        ; near = 0.1
0x00A41B67  push 0x3F400000        ; 1/aspect = 0.75      <-- the hook
0x00A41B77  call 0x00A3FFE0        ; BuildPerspective(fovY, h/w, near, far)
0x00A41B83  call 0x004A56C0        ; SetTransform(PROJECTION)
```

That sits inside `0x00A411A0`, the render-state manager's default-state routine, which is reached only by
a tail jump from the state-manager init at `0x00A43650` — itself called only from device create and mode
set. Its matrix builder `0x00A3FFE0` has exactly one call site in the entire image. So it seeds a state
block with a 60° / 4:3 / 0.1 / 2000 projection once, at renderer init, and a real camera projection
overwrites it before any geometry is drawn.

The projection that actually reaches the device comes from `Camera::UpdateProjectionMatrix`
(`0x00A36ED0`), which builds `m00 = cot(fov/2) * this->aspect` from the camera's own field at `+0x30`.
**That is the function `widescreen3d` patches**, and its cave substitutes the true viewport height/width
for every camera instance — so it governs the world view, the weapon, scopes and mirrors alike.

Two caveats, stated honestly:

* `0x004A56C0` is *not* the only way a projection reaches the device. There are around twelve
  `SetTransform(D3DTS_PROJECTION)` sites; most are camera-derived, but `0x00A9E0F5` builds its matrix on
  the stack and `0x00AB6922` takes the transform state as a runtime parameter. So "widescreen3d covers
  literally everything" is not established.
* With its hook reverted, the cave at `0xE25480` is provably orphaned — no branch anywhere in the file
  targets `0xE25480..0xE254FF`, the address appears nowhere as a constant, and `0xE25400` does not fall
  through into it.

The entry is still shipped, for parity with the kit, but renamed and described for what it is.

## A third aspect patch exists, and it is the HUD one

`0x0087C830` (file 0x47BC30) — the "canvas resize" the kit's own README calls broken — hooks into cave
`0xE25300`, which computes `width/height` (inverted relative to the other two caves), multiplies by
`600.0`, and stores the result as a 2D screen extent:

```
fild  [edx+0x7c]        ; width
fidiv [edx+0x80]        ; / height      -> 1.6 at 3840x2400
fmul  [0xE25380]        ; * 600.0       -> 960.0   (800.0 at 4:3)
fst   [ecx+0x10]
fmul  [0xE25384]        ; * -0.5
fstp  [ecx+8]           ; -480.0
```

The function's tail calls the camera's ortho-height setter with `600.0f` and then
`SetTransform(PROJECTION)`, so this is the 2D/HUD screen extent, not the 3D view. It remains unshipped —
the README rules it out as conflicting with bfvhudfix — but it is worth adding as a menu entry purely so
users can *remove* it, since it is live in the author's build.

There is no fourth aspect cave. `.ctfsnd` contains exactly four live stubs: the CTF script loader at
`0xE25040`/`0xE25160`, the HUD canvas at `0xE25300`, `widescreen3d` at `0xE25400`, and this one at
`0xE25480`.

## Newly shipped: the 4:3 filter on the resolution list (0x10747C)

Found by diffing every region of the patched exe against retail and subtracting the patches already
accounted for. The mode enumerator computed `height / width` and compared it against the double `0.75`
at `0xB5FC38` (= 480/640):

```
0x0050806D  fdivp st(1)              ; height / width
0x0050806F  fld   qword [0xB5FC38]   ; 0.75
0x00508075  fucompp / fnstsw / test ah,0x44
0x0050807C  jp    0x005081CC         ; not equal -> discard this mode
0x00508082  cmp   edi, 0x280         ; width  >= 640
0x0050808E  cmp   ecx, 0x1E0         ; height >= 480
```

Six NOPs over the `jp` drop the "must be exactly 4:3" test while leaving the 640×480 minimums intact.
Without it the menu never lists a widescreen mode, so it belongs with `widescreenres` — which was
incomplete without it.
