; Shared layout for the Battlefield Vietnam hooks.
;
; BFV has no code cave in .text (biggest padding run is 117 bytes), so the hooks live in the tail of
; .tls: 503 zero bytes at VA 0xE23009 (file 0x93C609). .tls is safe to use -- its VirtualSize is 9,
; its contents are zero, and the real TLS directory struct lives in .rdata at RVA 0x7B72D4. The
; installer also flips .tls to executable by writing 0xE0000040 over its Characteristics at file 0x2EC.
;
; VA -> file offset differs per section (there is no single formula):
;   .text   file = VA - 0x400C00
;   .rdata  file = VA - 0x400E00
;   .tls    file = VA - 0x4E6A00

; --- cave layout -------------------------------------------------------------------------------
STASH       equ 0xE23010        ; 6 bytes: sin_port (2, network order) then sin_addr (4, network order)
HOOK_STASH  equ 0xE23018        ; hook A body
HOOK_MAP    equ 0xE23050        ; hook B body

; --- imports (IAT slots in BfVietnam.exe v1.21) ------------------------------------------------
; BFV statically links the CRT, so upstream's _spawnl does not exist here.
SHELLEXECUTEA equ 0xB422E0
EXITPROCESS   equ 0xB4225C

; --- game structures ---------------------------------------------------------------------------
; MSVC 7.1 std::string, sizeof 0x1C: { +0x00 pad, +0x04 buf[16]/ptr, +0x14 size, +0x18 capacity }
STR_BUF     equ 0x04
STR_CAP     equ 0x18

CONN_LEVEL  equ 0x284           ; conn -> std::string level name (set at the CheckSumEvent handler head)
GAME_OBJ    equ 0xd444c4        ; -> game object; +0x738 is the addModPath vector<std::string>
MODVEC_OFF  equ 0x738 + 4       ; vector begin pointer -> &first std::string (the active mod path)


; esi = &std::string -> esi = c_str(). Inline: BFV's strings are SSO, so no import is needed.
%macro cstr_esi 0
    cmp     dword [esi+STR_CAP], 16
    jb      %%inline
    mov     esi, [esi+STR_BUF]
    jmp     %%done
%%inline:
    add     esi, STR_BUF
%%done:
%endmacro
