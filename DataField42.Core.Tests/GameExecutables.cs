namespace DataField42.Core.Tests;

/// <summary>
/// Where to find the Battlefield Vietnam executables some tests need.
/// </summary>
/// <remarks>
/// The patch tests check real byte offsets, so they need a real v1.21 BfVietnam.exe to check them
/// against. Nobody can be expected to have one at the same path, and the game is not redistributable,
/// so the location is configurable and every test that needs one skips itself when it is absent. A
/// clone with no game installed still builds and runs the rest of the suite green.
///
///   BFV_PRISTINE_EXE  an untouched retail BfVietnam.exe v1.21
///   BFV_PATCHED_EXE   one with the patch kit applied, for the tests that read the author's caves
///
/// The defaults are the maintainer's layout, so they keep working without any setup here.
/// </remarks>
public static class GameExecutables
{
    /// <summary>An untouched retail executable, or null when there is not one to test against.</summary>
    public static string? Pristine => Resolve(
        "BFV_PRISTINE_EXE",
        @"D:\Games\EA GAMES\Battlefield Vietnam Original Files\BfVietnam.exe");

    /// <summary>A patch-kit executable, or null when there is not one to test against.</summary>
    public static string? Patched => Resolve(
        "BFV_PATCHED_EXE",
        @"D:\Games\EA GAMES\Battlefield Vietnam\BfVietnam.exe");

    private static string? Resolve(string variable, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? configured : null;

        return File.Exists(fallback) ? fallback : null;
    }
}
