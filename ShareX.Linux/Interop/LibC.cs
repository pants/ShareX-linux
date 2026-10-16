using System.Runtime.InteropServices;

namespace ShareX.Linux.Interop;

/// <summary>The handful of libc entry points ShareX needs that .NET does not surface.</summary>
internal static partial class LibC
{
    private const int F_GETFD = 1;
    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    public static partial int Duplicate(int fd);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Fcntl(int fd, int cmd, int arg);

    [LibraryImport("libc", EntryPoint = "chmod", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int Chmod(string path, uint mode);

    /// <summary>
    /// Clears FD_CLOEXEC so a descriptor survives exec and can be handed to a child process.
    /// .NET marks every descriptor it opens close-on-exec, which would hide it from ffmpeg.
    /// </summary>
    public static bool MakeInheritable(int fd)
    {
        int flags = Fcntl(fd, F_GETFD, 0);
        if (flags < 0) return false;

        return Fcntl(fd, F_SETFD, flags & ~FD_CLOEXEC) >= 0;
    }
}
