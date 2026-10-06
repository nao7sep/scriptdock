using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ScriptDock.Storage;

/// <summary>
/// On macOS <see cref="File.Replace(string, string, string?, bool)"/> installs the new file by rename,
/// so the replaced file would lose its permission mode, ACL and extended attributes (Finder tags are
/// one); Windows' native replace keeps them itself. The atomic writer copies them onto its temp file
/// before the replace: never the times or ownership, which <c>COPYFILE_STAT</c> would also copy. A
/// volume that cannot hold them answers <c>ENOTSUP</c> and is left as it is.
/// </summary>
internal static class MacFileMetadata
{
    private const uint CopyfileAcl = 1 << 0;
    private const uint CopyfileXattr = 1 << 2;
    private const int Enotsup = 45;

    internal static void Copy(string original, string temp)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        using (var from = File.OpenHandle(original))
        using (var to = File.OpenHandle(temp, FileMode.Open, FileAccess.Write))
        {
            if (fcopyfile((int)from.DangerousGetHandle(), (int)to.DangerousGetHandle(), IntPtr.Zero, CopyfileAcl | CopyfileXattr) != 0
                && Marshal.GetLastPInvokeError() is var errno && errno != Enotsup)
                throw new IOException($"fcopyfile failed with errno {errno}.");
        }

        // Last, so a read-only mode cannot stop the temp file being opened above.
        File.SetUnixFileMode(temp, File.GetUnixFileMode(original));
    }

    [DllImport("/usr/lib/libSystem.dylib", SetLastError = true)]
    private static extern int fcopyfile(int from, int to, IntPtr state, uint flags);
}
