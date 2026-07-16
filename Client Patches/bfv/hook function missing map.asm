bits 32
org 0xE23050

%include "bfv_common.asm"

; Hook B -- intercept "map not found" and hand off to DataField42.
;
; Patched over 0x527CE5 (6 bytes):
;     mov edx, [edi+0x1f8]
; which is where the CheckSumEvent handler lands once its per-mod-path search for the level's .rfa has
; failed against every entry of the addModPath vector. Three instructions later it would push 0xb
; (MAP_NOT_FOUND) and disconnect. edi is the connection object; the level name and hash are on it.
;
; The order below is forced, and both obvious orderings are wrong:
;   * hook here and just spawn  -> ExitProcess beats the disconnect out the door, the server keeps the
;     session, and the relaunched game is refused with "CD key in use".
;   * hook after the disconnect (0x527CF6) -> the teardown has already cleared the connection's
;     strings, [edi+0x284] reads empty, the argument list collapses and DataField42 falls back to its
;     defaults ("Map: , Mod: BF1942", null host).
; So: capture while the strings are live, run the disconnect ourselves, then spawn.
;
; Emits:  DataFieldVietnam.exe bfvmap <ipHex>:<portHex> <map> <mod>
;
; Hex rather than dotted-decimal because the address only exists as 4 raw bytes and formatting
; decimal in asm costs ~50 instructions; the client parses the hex form (see CommandLineArguments).
; The registry path is not passed either -- the client knows its own -- which saves copying and
; quoting a 57-char string containing spaces.
;
; BFV statically links the CRT, so upstream's _spawnl(_P_OVERLAY) is unavailable. ShellExecuteA plus
; ExitProcess gets the same result: DataField42 takes over, the game goes away. If the spawn fails we
; fall through to the game's own error instead of killing it silently.

hook_map:
    pushad
    cld

    mov     ebp, edi                    ; conn (edi is still live; pushad saved the original)
    sub     esp, 0x100
    mov     ebx, esp                    ; ebx = argument buffer
    mov     edi, ebx                    ; edi = write cursor

    mov     esi, s_bfvmap
    call    copy_cstr

    ; IP: 4 bytes, network order, as 8 hex chars
    mov     edx, STASH+2
    mov     ecx, 4
.ip_loop:
    mov     al, [edx]
    call    emit_hex
    inc     edx
    dec     ecx
    jnz     .ip_loop

    mov     al, ':'
    stosb

    ; port: 2 bytes, network order, as 4 hex chars
    mov     al, [STASH]
    call    emit_hex
    mov     al, [STASH+1]
    call    emit_hex

    mov     al, ' '
    stosb

    lea     esi, [ebp+CONN_LEVEL]       ; level name
    cstr_esi
    call    copy_cstr

    mov     al, ' '
    stosb

    ; The mod the SERVER asked for, taken from our own command line.
    ;
    ; Not the addModPath vector: that says what the client actually loaded, and when the requested mod
    ; is not installed the game quietly falls back to base BFVietnam. The hook then reports
    ; "Mod: BFVietnam" for a DiceCity_V server and the sync asks for a map that mod does not have.
    ; The command line still carries what was asked for -- BFV relaunches itself with
    ; "+game <mod> +joinServer ..." to join, so by the time we are here it is there either way.
    call    [GETCOMMANDLINEA]
    mov     esi, s_game
    call    find_substr
    test    esi, esi
    jnz     .have_mod

    mov     esi, [GAME_OBJ]             ; nothing on the command line: fall back to what we loaded
    mov     esi, [esi+MODVEC_OFF]
    cstr_esi
    call    copy_cstr
    jmp     .mod_done
.have_mod:
    call    copy_token
.mod_done:

    xor     al, al
    stosb                               ; terminate

    ; Everything we need is now in the buffer, so tell the server we are leaving before we go.
    ;
    ; This is the game's own disconnect, the one at 0x527CE5..0x527CF3, run early. It has to happen
    ; here and not be left to the code we return to: ExitProcess would kill the process first, the
    ; server would hold the session, and the relaunched game would be refused with "CD key in use".
    ; Running it before the capture is not an option either -- it clears the connection's strings.
    ;
    ; The argument list is on our own stack frame, so tearing the connection down cannot touch it.
    ; The call cleans up its own argument (the game does not adjust esp after it either), and ebp/ebx
    ; are callee-saved, so the connection pointer and the buffer both survive.
    mov     edx, [ebp+0x1f8]
    lea     ecx, [ebp+0x1f8]
    push    0xb                         ; MAP_NOT_FOUND
    call    dword [edx+0x28]
    mov     ebx, esp                    ; re-derive the buffer, in case the call was careless with ebx

    ; ShellExecuteA(NULL, NULL, "DataFieldVietnam.exe", buffer, NULL, SW_SHOWNORMAL)
    push    1
    push    0
    push    ebx
    push    s_exe
    push    0
    push    0
    call    [SHELLEXECUTEA]             ; stdcall: cleans its own arguments

    cmp     eax, 32                     ; ShellExecuteA: <= 32 means it failed
    jbe     .failed

    push    0
    call    [EXITPROCESS]               ; does not return

.failed:
    ; The spawn failed, so let the game carry on and show its own error -- but we have already
    ; disconnected, so skip the copy of it we would otherwise return into. Nothing between here and
    ; 0x527CF6 reads edx, so the displaced instruction does not need re-running on this path.
    add     esp, 0x100
    popad
    add     esp, 4                      ; drop our return address
    jmp     0x527CF6                    ; past the game's own disconnect


; eax = haystack, esi = needle -> esi = just past the first match, or 0 if absent.
find_substr:
    push    ebx
    push    edi
    push    edx
    mov     ebx, eax
.outer:
    cmp     byte [ebx], 0
    je      .fail
    mov     edx, ebx                    ; candidate start
    mov     edi, esi                    ; needle cursor
.inner:
    mov     cl, [edi]
    test    cl, cl
    jz      .hit                        ; needle exhausted -> matched
    mov     ch, [edx]
    cmp     cl, ch
    jne     .next
    inc     edx
    inc     edi
    jmp     .inner
.next:
    inc     ebx
    jmp     .outer
.hit:
    mov     esi, edx                    ; first byte after the needle
    jmp     .done
.fail:
    xor     esi, esi
.done:
    pop     edx
    pop     edi
    pop     ebx
    ret

; esi = source, edi = cursor. Copies one command-line token: stops at a space or the terminator.
copy_token:
    lodsb
    test    al, al
    jz      .done
    cmp     al, ' '
    je      .done
    stosb
    jmp     copy_token
.done:
    ret

; esi = source C string, edi = cursor. Copies without the terminator.
copy_cstr:
    lodsb
    test    al, al
    jz      .done
    stosb
    jmp     copy_cstr
.done:
    ret

; al = byte -> two hex chars at [edi]. Preserves ecx and edx.
emit_hex:
    push    ecx
    mov     cl, al
    shr     al, 4
    call    emit_nib
    mov     al, cl
    and     al, 0x0F
    call    emit_nib
    pop     ecx
    ret

emit_nib:
    cmp     al, 10
    jb      .digit
    add     al, 'A' - 10
    jmp     .store
.digit:
    add     al, '0'
.store:
    stosb
    ret

s_bfvmap:
    db "bfvmap ", 0
s_exe:
    db "DataFieldVietnam.exe", 0
s_game:
    db "+game ", 0
