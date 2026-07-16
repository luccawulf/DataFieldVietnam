bits 32
org 0xE23018

%include "bfv_common.asm"

; Hook A -- stash the server address on its way out.
;
; BFV never stores the server IP anywhere the MAP_NOT_FOUND path can reach: it is not on the
; connection object, not in .data, and not a string anywhere in memory. It only exists as a
; sockaddr_in inside the network object, handed to ws2_32!sendto. So we copy it out as packets go by
; and read it back in hook B.
;
; Patched over 0x8D8ED8 (7 bytes):
;     mov edx, [esp+0x10]
;     mov ecx, [esi+4]
; At that point eax already points at the chosen sockaddr_in (the caller picks between netObj+0x08
; and netObj+0x1A on the flag at netObj+0x19), so we take whichever one it settled on.
;
; sockaddr_in: +0x00 sin_family, +0x02 sin_port, +0x04 sin_addr -- port and addr both network order,
; which is what we want: it is the order the hex string should read in.
;
; Runs per packet, so it stays tiny and touches no flags (the caller's jne has already been taken by
; the time we are entered, and nothing downstream reads flags before the sendto call).

hook_stash:
    push    ecx
    mov     ecx, [eax+2]                ; sin_port + first half of sin_addr
    mov     [STASH], ecx
    mov     cx, [eax+6]                 ; second half of sin_addr
    mov     [STASH+4], cx
    pop     ecx

    ; Re-run what the call displaced. The +0x14 is deliberate: the original read [esp+0x10], and our
    ; call pushed a return address, so esp sits 4 lower here.
    mov     edx, [esp+0x14]
    mov     ecx, [esi+4]
    ret
