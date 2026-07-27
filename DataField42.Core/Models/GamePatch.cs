/// <summary>
/// One byte-level edit to the game executable, carrying both what should be there before and after.
/// </summary>
/// <remarks>
/// Keeping the original bytes is what makes the rest of this safe. Every offset here is hardcoded to
/// BfVietnam.exe v1.21, so if the bytes at an offset match neither form, the file is a different build
/// and must not be written to. The old check only looked for our own bytes, which could not tell "not
/// patched yet" apart from "not the executable these offsets were built for".
/// </remarks>
public record PatchEdit(int Offset, byte[] Original, byte[] Patched)
{
    /// <summary>
    /// True when this edit writes into free space rather than over real code.
    /// </summary>
    /// <remarks>
    /// The hook bodies go in the zeroed tail of the .tls section; the call sites overwrite real
    /// instructions. That difference is what lets us tell an out-of-date patch from a foreign
    /// executable: unrecognised bytes in free space can only be an older build of our own hook,
    /// because nothing else writes there, while unrecognised bytes over an instruction mean this is
    /// not the exe we know.
    /// </remarks>
    public bool IsFreeSpace => Original.All(b => b == 0);
}

/// <summary>A patch paired with how it currently sits in the game executable.</summary>
public record GamePatchStatus(GamePatch Patch, GamePatchState State);

/// <summary>
/// A block of code a patch redirects into, and the section that has to exist to hold it.
/// </summary>
/// <remarks>
/// Some patches cannot be expressed as byte edits: they point an instruction at new code, and that code
/// needs somewhere to live. The executable has no room (.text's largest padding run is 117 bytes, and
/// the .tls tail is nearly full), so the section is appended.
///
/// <para><see cref="Contents"/> is written at <see cref="ContentVirtualAddress"/>, not at the section
/// base. That matters: the author's own .ctfsnd section already holds other caves, and blanket-writing
/// from the base would erase them and leave their hooks pointing at zeros.</para>
///
/// <para>Nothing here needs undoing on revert. Restoring the hook instruction disables the patch
/// completely, and a cave nothing jumps to is just dead bytes — so revert never shrinks the file or
/// rewrites a section table.</para>
/// </remarks>
public record CaveSection(
    string Name,
    uint SectionVirtualAddress,
    uint SectionSize,
    uint ContentVirtualAddress,
    byte[] Contents);

/// <summary>How a patch currently sits in the game executable.</summary>
public enum GamePatchState
{
    /// <summary>Every edit matches its original: not installed, and safe to install.</summary>
    NotApplied,

    /// <summary>Every edit matches its patched form: installed and current.</summary>
    Applied,

    /// <summary>
    /// Installed, but an older build of it -- the call sites are ours while the hook body differs.
    /// Re-applying brings it up to date.
    /// </summary>
    Outdated,

    /// <summary>
    /// Some edits applied and some not, with nothing to suggest an older build. A write was probably
    /// interrupted, so this needs repairing rather than toggling.
    /// </summary>
    Partial,

    /// <summary>
    /// An edit that overwrites real code matches neither form, so this is not the executable these
    /// offsets were built for. Never write in this state.
    /// </summary>
    UnsupportedExecutable,
}

/// <summary>
/// A named, independently selectable modification to the game executable.
/// </summary>
/// <remarks>
/// A patch is all-or-nothing internally. The auto-download feature, for example, is five separate byte
/// edits -- two call sites, two hook bodies, and the flag that makes the section holding them
/// executable -- and applying any subset would crash the game: a hook body without the executable flag
/// runs into non-executable memory, and a call site without its body jumps into zeros. The user chooses
/// between patches, never between the edits inside one.
/// </remarks>
public record GamePatch(string Id, string Name, string Description, PatchEdit[] Edits, CaveSection? Cave = null)
{
    /// <summary>Reads the executable and reports how this patch currently sits in it.</summary>
    public GamePatchState GetState(Stream gameExe)
    {
        var allPatched = true;
        var allOriginal = true;
        var executableIsKnown = true;
        var freeSpaceDiffers = false;

        foreach (var edit in Edits)
        {
            var buffer = new byte[edit.Patched.Length];
            gameExe.Seek(edit.Offset, SeekOrigin.Begin);
            if (gameExe.Read(buffer, 0, buffer.Length) != buffer.Length)
                return GamePatchState.UnsupportedExecutable;   // too short to be the exe we know

            var isPatched = buffer.SequenceEqual(edit.Patched);
            var isOriginal = buffer.SequenceEqual(edit.Original);

            if (!isPatched) allPatched = false;
            if (!isOriginal) allOriginal = false;

            if (!isPatched && !isOriginal)
            {
                if (edit.IsFreeSpace)
                    freeSpaceDiffers = true;      // an older build of our own hook
                else
                    executableIsKnown = false;    // an instruction we do not recognise
            }
        }

        if (!executableIsKnown) return GamePatchState.UnsupportedExecutable;
        if (allPatched) return GamePatchState.Applied;
        if (allOriginal) return GamePatchState.NotApplied;
        if (freeSpaceDiffers) return GamePatchState.Outdated;
        return GamePatchState.Partial;
    }
}
