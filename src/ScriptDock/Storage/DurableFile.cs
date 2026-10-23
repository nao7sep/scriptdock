using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace ScriptDock.Storage;

/// <summary>Publishes a closed, fully written staging file in the destination's directory, and forces
/// the publication to stable storage before reporting success. A failure after the rename may leave
/// the new destination in place: it reports unconfirmed durability, without attempting rollback.</summary>
internal static class DurableFile
{
    private const int FullFileSync = 51; // macOS F_FULLFSYNC includes the device's write cache.
    private const uint ReplaceExisting = 0x1;
    private const uint WriteThrough = 0x8;

    internal static void Publish(string stagingPath, string destinationPath)
    {
        stagingPath = Path.GetFullPath(stagingPath);
        destinationPath = Path.GetFullPath(destinationPath);

        if (OperatingSystem.IsMacOS())
            PreserveUnixMode(stagingPath, destinationPath);
        else if (OperatingSystem.IsWindows())
            PreserveWindowsAccess(stagingPath, destinationPath);
        else
            throw new PlatformNotSupportedException("Durable publication supports macOS and Windows.");

        // Share deletion so Windows can rename the file while this handle still owns its lifetime.
        using var staging = new FileStream(stagingPath, FileMode.Open, FileAccess.ReadWrite,
            FileShare.Read | FileShare.Delete);
        staging.Flush(flushToDisk: true);

        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileEx(stagingPath, destinationPath, ReplaceExisting | WriteThrough))
                throw NativeFailure("Could not publish the durable file", destinationPath);
            return;
        }

        ForceDeviceCache(staging.SafeFileHandle, stagingPath);
        File.Move(stagingPath, destinationPath, overwrite: true);

        // File contents and the containing directory's new name entry are separate durability work.
        var parent = Path.GetDirectoryName(destinationPath)!;
        using var directory = OpenDirectory(parent, flags: 0); // macOS O_RDONLY.
        if (directory.IsInvalid)
            throw NativeFailure("Could not open the published file's directory", parent);
        if (SyncDirectory(directory) != 0)
            throw NativeFailure("Could not synchronize the published file's directory", parent);
        ForceDeviceCache(staging.SafeFileHandle, destinationPath);
    }

    [SupportedOSPlatform("macos")]
    private static void PreserveUnixMode(string stagingPath, string destinationPath)
    {
        UnixFileMode mode;
        try { mode = File.GetUnixFileMode(destinationPath); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        File.SetUnixFileMode(stagingPath, mode);
    }

    [SupportedOSPlatform("windows")]
    private static void PreserveWindowsAccess(string stagingPath, string destinationPath)
    {
        FileSecurity access;
        try { access = new FileInfo(destinationPath).GetAccessControl(AccessControlSections.Access); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        new FileInfo(stagingPath).SetAccessControl(access);
    }

    private static void ForceDeviceCache(SafeFileHandle handle, string path)
    {
        if (FileControl(handle, FullFileSync) != 0)
            throw NativeFailure("Could not force the published file to stable storage", path);
    }

    private static IOException NativeFailure(string operation, string path) =>
        new($"{operation}: {path}", new Win32Exception(Marshal.GetLastPInvokeError()));

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int FileControl(SafeFileHandle file, int command);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int SyncDirectory(SafeFileHandle directory);

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingPath, string destinationPath, uint flags);
}
