using System.Diagnostics;
using System.Text;

public class Bf1942Client(string path)
{
    /// <summary>
    /// Launches Battlefield Vietnam, optionally straight into a server.
    /// </summary>
    /// <remarks>
    /// The argument shapes are the ones BfVietnam.exe builds for itself when it relaunches to join a
    /// server, read out of the exe's own format strings: "+joinServer %s:%s +isInternet 1" for an
    /// internet server (0 is LAN), " +password ", and "+game %s +restart 1".
    ///
    /// Interface 6 is the server browser in BFV as well as BF1942 (tested). The exe also contains a
    /// hardcoded "+goToInterface 8", but that is the custom-game screen it returns to after a mod
    /// switch, not the browser.
    /// </remarks>
    public void Start(string? modId = null, string? ipPort = null, string? password = null)
    {
        if (Debugger.IsAttached)
            Environment.Exit(0);

        string arguments = " +restart 1";
        if (modId != null)
            arguments += $" +game {modId}";
        if (ipPort != null)
            arguments += $" +joinServer {ipPort} +isInternet 1";
        else
            arguments += $" +goToInterface 6";
        if (password != null)
            arguments += $" +password {password}";

        ExternalProcess.SwitchTo(path, arguments);
    }

    /// <summary>The patch the installer applies and the dashboard warns about when it is missing.</summary>
    private const string AutoDownloadPatchId = "autodownload";

    /// <summary>Every patch this build knows about, with how each one currently sits in the exe.</summary>
    public IReadOnlyList<GamePatchStatus> GetPatchStatuses()
    {
        using var gameExe = new FileStream(path, FileMode.Open, FileAccess.Read);
        return Bf1942ClientPatches.All
            .Select(patch => new GamePatchStatus(patch, patch.GetState(gameExe)))
            .ToList();
    }

    /// <summary>Writes a patch in, or brings an out-of-date one up to date.</summary>
    public void ApplyPatch(GamePatch patch) => WritePatch(patch, applied: true);

    /// <summary>Puts back the bytes a patch overwrote, turning it off again.</summary>
    public void RevertPatch(GamePatch patch) => WritePatch(patch, applied: false);

    /// <remarks>
    /// Refuses outright on an executable these offsets were not built for. Every offset is specific to
    /// BfVietnam.exe v1.21, so writing into any other build corrupts it -- and now that a user can press
    /// this from a menu rather than it only ever running from the installer, that check has to be here.
    /// </remarks>
    private void WritePatch(GamePatch patch, bool applied)
    {
        try
        {
            using var gameExe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);

            var state = patch.GetState(gameExe);
            if (state == GamePatchState.UnsupportedExecutable)
                throw new InvalidOperationException(
                    $"'{Path.GetFileName(path)}' is not the Battlefield Vietnam v1.21 executable these " +
                    "patches were built for. It has been left untouched.");

            if (state == (applied ? GamePatchState.Applied : GamePatchState.NotApplied))
                return;   // already where it needs to be

            // The cave goes in first so the hook is never left pointing at bytes that are not there
            // yet. On revert it is deliberately left behind: restoring the hook is what disables the
            // patch, and a cave nothing jumps to is harmless.
            if (applied && patch.Cave is { } cave)
            {
                PeSections.EnsureCaveSection(gameExe, cave.Name, cave.SectionVirtualAddress, cave.SectionSize);
                var caveOffset = PeSections.ToFileOffset(gameExe, cave.ContentVirtualAddress)
                    ?? throw new InvalidOperationException(
                        $"The cave at 0x{cave.ContentVirtualAddress:X8} is not mapped in this executable.");
                gameExe.Seek(caveOffset, SeekOrigin.Begin);
                gameExe.Write(cave.Contents, 0, cave.Contents.Length);
            }

            foreach (var edit in patch.Edits)
            {
                var bytes = applied ? edit.Patched : edit.Original;
                gameExe.Seek(edit.Offset, SeekOrigin.Begin);
                gameExe.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new Exception($"Failed patching {Path.GetFileName(path)}: {ex.Message}", ex);
        }
    }

    public bool IsDataField42PatchApplied()
    {
        if (Debugger.IsAttached)
            return true;

        using var gameExe = new FileStream(path, FileMode.Open, FileAccess.Read);
        return AutoDownloadPatch.GetState(gameExe) == GamePatchState.Applied;
    }

    public void ApplyDataField42Patch() => ApplyPatch(AutoDownloadPatch);

    private static GamePatch AutoDownloadPatch =>
        Bf1942ClientPatches.All.Single(patch => patch.Id == AutoDownloadPatchId);

    /// <summary>
    /// Reads the CD-key registry path the game itself uses, out of the string baked into the exe.
    /// </summary>
    /// <remarks>
    /// File offset of "SOFTWARE\Electronic Arts\EA Games\Battlefield Vietnam\ergc" in BfVietnam.exe
    /// v1.21 (.rdata; VA 0xB44E0C). Taken from the exe rather than hardcoded here so a key path that
    /// does not match the running game shows up as a mismatch instead of silently hashing nothing.
    /// Note the exe also still carries BF1942's Road to Rome key path at 0x761400 -- not this one.
    /// </remarks>
    public string GetKeyRegistryPath()
    {
        var bytes = Read(0x74400C, 100);
        int nullIndex = Array.IndexOf(bytes, (byte)0x00);
        return Encoding.UTF8.GetString(bytes[..nullIndex]);
    }

    private byte[] Read(int offset, int length)
    {
        try
        {
            var buffer = new byte[length];
            using var clientExe = new FileStream(path, FileMode.Open, FileAccess.Read);
            clientExe.Seek(offset, SeekOrigin.Begin);
            clientExe.ReadExactly(buffer, 0, length);
            return buffer;
        }
        catch (Exception ex)
        {
            throw new Exception("Failed Patching BF1942.exe: " + ex.Message, ex);
        }
    }
}
