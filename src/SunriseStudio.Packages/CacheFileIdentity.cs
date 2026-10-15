using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SunriseStudio.Packages;

/** Windows change time detects in-place edits even when callers restore the write timestamp. */
internal static class CacheFileIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo
    {
        public long Created, Accessed, Written, Changed;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity { public ulong Volume, Low, High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file,
        int infoClass, out BasicInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileIdentity(SafeFileHandle file,
        int infoClass, out FileIdentity info, uint size);

    /** SHA-256 over each file's path, length, times and volume identity; off Windows, its content. */
    internal static string Compute(IEnumerable<string> paths, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string input in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            byte[] path = Encoding.UTF8.GetBytes(input.ToUpperInvariant());
            hash.AppendData(BitConverter.GetBytes(path.Length)); hash.AppendData(path);
            var info = new FileInfo(input);
            hash.AppendData(BitConverter.GetBytes(info.Exists));
            if (!info.Exists) continue;
            using var stream = new FileStream(input, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            hash.AppendData(BitConverter.GetBytes(stream.Length));
            if (OperatingSystem.IsWindows())
            {
                if (!GetFileInformationByHandleEx(stream.SafeFileHandle, 0, out var basic,
                    (uint)Marshal.SizeOf<BasicInfo>()))
                    throw new IOException("Cannot read source change identity.");
                hash.AppendData(BitConverter.GetBytes(basic.Created));
                hash.AppendData(BitConverter.GetBytes(basic.Written));
                hash.AppendData(BitConverter.GetBytes(basic.Changed));
                if (!GetFileIdentity(stream.SafeFileHandle, 18, out var identity,
                    (uint)Marshal.SizeOf<FileIdentity>()))
                    throw new IOException("Cannot read source file identity.");
                hash.AppendData(BitConverter.GetBytes(identity.Volume));
                hash.AppendData(BitConverter.GetBytes(identity.Low));
                hash.AppendData(BitConverter.GetBytes(identity.High));
            }
            else
            {
                hash.AppendData(BitConverter.GetBytes(info.LastWriteTimeUtc.Ticks));
                hash.AppendData(SHA256.HashData(stream));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
