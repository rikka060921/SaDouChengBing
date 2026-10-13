// Windows PowerShell 5.1 compatible. Extended Win32 paths avoid MAX_PATH;
// no registry changes, shell commands, recursive deletion or external packages.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ToDoRuntime
{
    public sealed class RuntimeEntry
    {
        public string FullName;
        public string Name;
        public bool IsDirectory;
        public bool IsReparsePoint;
    }

    public static class NativeFiles
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindData
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint SizeHigh, SizeLow, Reserved0, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFullPathNameW(string path, uint length, StringBuilder buffer, IntPtr filePart);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string pattern, out FindData data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(IntPtr handle, out FindData data);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FindClose(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateDirectoryW(string path, IntPtr security);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CopyFileW(string source, string destination, bool failIfExists);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileExW(string source, string destination, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        private static Exception Error(string operation, string path)
        {
            return new Win32Exception(Marshal.GetLastWin32Error(), operation + ": " + path);
        }
        public static string FullPath(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path.Substring(8);
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path.Substring(4);
            if (path.StartsWith(@"\\.\", StringComparison.Ordinal)) throw new ArgumentException("Device paths are not supported.");
            StringBuilder buffer = new StringBuilder(32768);
            uint result = GetFullPathNameW(path, (uint)buffer.Capacity, buffer, IntPtr.Zero);
            if (result == 0) throw Error("Resolve path", path);
            if (result >= buffer.Capacity) throw new PathTooLongException(path);
            return buffer.ToString();
        }
        private static string Extended(string path)
        {
            path = FullPath(path);
            return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
        }
        public static string Parent(string path)
        {
            path = FullPath(path);
            string root;
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                int serverEnd = path.IndexOf('\\', 2);
                int shareEnd = serverEnd < 0 ? -1 : path.IndexOf('\\', serverEnd + 1);
                root = shareEnd < 0 ? path.TrimEnd('\\') + "\\" : path.Substring(0, shareEnd + 1);
            }
            else root = path.Substring(0, 3);
            path = path.TrimEnd('\\', '/');
            if (path.Length < root.Length || String.Equals(path, root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
            int index = path.LastIndexOf('\\');
            return index < root.Length ? root : path.Substring(0, index);
        }
        private static uint Attributes(string path)
        {
            uint attributes = GetFileAttributesW(Extended(path));
            if (attributes != UInt32.MaxValue) return attributes;
            int error = Marshal.GetLastWin32Error();
            if (error == 2 || error == 3) return attributes;
            throw new Win32Exception(error, "Read attributes: " + path);
        }
        public static bool Exists(string path) { return Attributes(path) != UInt32.MaxValue; }
        public static bool IsDirectory(string path) { uint a = Attributes(path); return a != UInt32.MaxValue && (a & 16) != 0; }
        public static bool IsFile(string path) { uint a = Attributes(path); return a != UInt32.MaxValue && (a & 16) == 0; }
        public static void AssertNoReparse(string path)
        {
            for (string current = FullPath(path); current != null; current = Parent(current))
            {
                uint a = Attributes(current);
                if (a != UInt32.MaxValue && (a & 1024) != 0) throw new IOException("Linked runtime paths are not supported: " + current);
            }
        }
        public static RuntimeEntry[] Children(string path)
        {
            List<RuntimeEntry> entries = new List<RuntimeEntry>();
            FindData data;
            IntPtr handle = FindFirstFileW(Extended(path).TrimEnd('\\') + @"\*", out data);
            if (handle == new IntPtr(-1))
            {
                if (Marshal.GetLastWin32Error() == 2 && IsDirectory(path)) return entries.ToArray();
                throw Error("Enumerate directory", path);
            }
            try
            {
                do
                {
                    if (data.Name != "." && data.Name != "..")
                        entries.Add(new RuntimeEntry { Name = data.Name, FullName = FullPath(path).TrimEnd('\\') + "\\" + data.Name,
                            IsDirectory = (data.Attributes & 16) != 0, IsReparsePoint = (data.Attributes & 1024) != 0 });
                } while (FindNextFileW(handle, out data));
                if (Marshal.GetLastWin32Error() != 18) throw Error("Enumerate directory", path);
            }
            finally { FindClose(handle); }
            return entries.ToArray();
        }
        public static void CreateDirectory(string path)
        {
            if (IsDirectory(path)) return;
            if (Exists(path)) throw new IOException("Directory destination is a file: " + path);
            string parent = Parent(path);
            if (parent != null) CreateDirectory(parent);
            if (!CreateDirectoryW(Extended(path), IntPtr.Zero) && !IsDirectory(path)) throw Error("Create directory", path);
        }
        public static string Hash(string path)
        {
            using (SafeFileHandle handle = CreateFileW(Extended(path), 0x80000000, 7, IntPtr.Zero, 3, 0x08000000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw Error("Open file", path);
                using (FileStream stream = new FileStream(handle, FileAccess.Read))
                using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            }
        }
        public static void Copy(string source, string target)
        {
            if (!CopyFileW(Extended(source), Extended(target), true)) throw Error("Copy file", source + " -> " + target);
        }
        public static void Move(string source, string target)
        {
            Move(source, target, true);
        }
        public static void Move(string source, string target, bool replaceExisting)
        {
            if (!MoveFileExW(Extended(source), Extended(target), (replaceExisting ? 1u : 0u) | 8u)) throw Error("Publish file", target);
        }
        public static void DeleteTemporary(string path)
        {
            // Only unique managed temporary files, never arbitrary files/directories.
            string name = path.Substring(path.LastIndexOf('\\') + 1);
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^__todo-[a-f0-9]{32}\.tmp$")) throw new ArgumentException("Not a managed temporary file.");
            if (!DeleteFileW(Extended(path))) throw Error("Remove temporary file", path);
        }
    }
}
