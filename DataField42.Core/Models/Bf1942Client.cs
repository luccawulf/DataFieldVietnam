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

    public bool IsDataField42PatchApplied()
    {
        if (Debugger.IsAttached)
            return true;

        try
        {
            using var clientExe = new FileStream(path, FileMode.Open, FileAccess.Read);

            foreach (var (offset, bytes) in Bf1942ClientPatches.Patches)
            {
                var buffer = new byte[bytes.Length];

                clientExe.Seek(offset, SeekOrigin.Begin);

                if (clientExe.Read(buffer, 0, buffer.Length) != buffer.Length)
                    throw new Exception($"Failed to read client exe.");

                if (!buffer.SequenceEqual(bytes))
                    return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            throw new Exception("Failed checking BF1942.exe patch status: " + ex.Message, ex);
        }
    }

    public void ApplyDataField42Patch()
    {
        try
        {
            using var clientExe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
            foreach (var (offset, bytes) in Bf1942ClientPatches.Patches)
            {
                clientExe.Seek(offset, SeekOrigin.Begin);
                clientExe.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex)
        {
            throw new Exception("Failed Patching BF1942.exe: " + ex.Message, ex);
        }
    }

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
