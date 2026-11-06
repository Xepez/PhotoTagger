using System.Runtime.InteropServices;

namespace PhotoTagger.Services;

/// <summary>
/// Same ordering Windows Explorer uses: "IMG_2.jpg" sorts before "IMG_10.jpg".
/// Registered with SQLite as the EXPLORER_ORDER collation (see MediaDatabase.Open).
/// </summary>
public static class NaturalSort
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string x, string y);

    public static int Compare(string? a, string? b) => StrCmpLogicalW(a ?? string.Empty, b ?? string.Empty);
}