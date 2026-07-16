bits 32
org 0x527CE5

%include "bfv_common.asm"

; Replaces, at the point the CheckSumEvent handler gives up on finding the level:
;     mov edx, [edi+0x1f8]       ; 6 bytes
; re-run at the end of the hook (fall-through path only -- the success path never returns).
;
; This has to be BEFORE the disconnect three instructions later:
;     lea ecx, [edi+0x1f8] / push 0xb / call [edx+0x28]
; Hooking after it instead (at 0x527CF6) was tried and broke the payload: the disconnect clears the
; connection object's strings, so [edi+0x284] came back empty, the hook emitted a short argument list,
; and DataField42 fell back to its defaults ("Map: , Mod: BF1942" and a null host). The string
; destructor at 0x527CFD is the giveaway.
;
; The cost of being here is that ExitProcess kills the game before the disconnect goes out, so the
; server keeps the session and a quick rejoin is refused with "CD key in use" until it times out.
; Fixing that properly means capturing the arguments here, letting the game disconnect, and spawning
; afterwards -- see the notes in hook function missing map.asm.

call    HOOK_MAP
nop
