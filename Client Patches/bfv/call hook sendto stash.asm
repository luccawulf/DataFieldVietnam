bits 32
org 0x8D8ED8

%include "bfv_common.asm"

; Replaces, in the sendto caller:
;     mov edx, [esp+0x10]        ; 4 bytes
;     mov ecx, [esi+4]           ; 3 bytes
; Both are re-run at the end of the hook. 0x8D8ED8 is also the target of the jne at 0x8D8ED3, which
; is fine -- it lands on the call.

call    HOOK_STASH
nop
nop
