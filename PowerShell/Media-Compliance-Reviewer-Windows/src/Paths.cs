using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
namespace MediaReviewer {
    public static class Paths {
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetFinalPathNameByHandle(IntPtr handle,StringBuilder path,uint length,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern uint GetDriveType(string root);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
        public static string Io(string path) {
            if(path.StartsWith(@"\\?\",StringComparison.Ordinal)) return path;
            string full=Path.GetFullPath(path);
            return full.StartsWith(@"\\",StringComparison.Ordinal) ? @"\\?\UNC\"+full.Substring(2) : @"\\?\"+full;
        }
        public static string Friendly(string path) {
            if(path.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase)) return @"\\"+path.Substring(8);
            return path.StartsWith(@"\\?\",StringComparison.Ordinal) ? path.Substring(4) : path;
        }
        public static string Source(string path) { string full=Friendly(Path.GetFullPath(path)); string root=Path.GetPathRoot(full); return full.Length<=root.Length ? root : full.TrimEnd('\\'); }
        public static string Key(string path) { return path.Replace('/','\\').ToUpperInvariant(); }
        static string FinalPath(IntPtr handle){var buffer=new StringBuilder(32768);uint length=GetFinalPathNameByHandle(handle,buffer,(uint)buffer.Capacity,0);if(length==0 || length>=buffer.Capacity)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot resolve the opened source location");return Friendly(buffer.ToString());}
        // Owned file workers or the isolated decoder only: network resolution may block in the OS.
        internal static string OpenedSourceWithinRoot(FileStream source,string root){
            IntPtr directory=CreateFile(Io(root),0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
            if(directory==new IntPtr(-1))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot verify the recorded source root");
            try{string boundary=FinalPath(directory).TrimEnd('\\')+"\\",resolved=FinalPath(source.SafeFileHandle.DangerousGetHandle());if(!resolved.StartsWith(boundary,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The opened source resolves outside its recorded root (junction or reparse redirect). Rescan the intended location.");return resolved;}
            finally{CloseHandle(directory);}
        }
        public static string LocalDatabase(string path) {
            if(String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Select a local database path");
            string full=Friendly(Path.GetFullPath(path));
            if(full.StartsWith(@"\\",StringComparison.Ordinal)) throw new InvalidOperationException("The active WAL database must be on local storage; UNC/network destinations are not supported.");
            if(Path.GetFileName(full).IndexOf(':')>=0) throw new ArgumentException("Database alternate data streams are not supported");
            string parent=Path.GetDirectoryName(full);
            IntPtr handle=CreateFile(Io(parent),0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
            if(handle==new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot verify the database's local parent directory");
            string resolved;
            try { var buffer=new StringBuilder(32768); uint length=GetFinalPathNameByHandle(handle,buffer,(uint)buffer.Capacity,0); if(length==0 || length>=buffer.Capacity) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot resolve the database destination"); resolved=Friendly(buffer.ToString()); }
            finally { CloseHandle(handle); }
            if(resolved.StartsWith(@"\\",StringComparison.Ordinal) || GetDriveType(Path.GetPathRoot(resolved))==4) throw new InvalidOperationException("The active WAL database must be local; the selected mapped/reparse destination resolves to network storage.");
            uint type=GetDriveType(Path.GetPathRoot(resolved));
            if(type!=2 && type!=3) throw new InvalidOperationException("Database destination is not verified suitable writable local storage.");
            string database=Path.Combine(resolved,Path.GetFileName(full));
            try {
                FileAttributes attributes=File.GetAttributes(Io(database));
                if((attributes&FileAttributes.ReparsePoint)!=0) throw new InvalidOperationException("The database file cannot be a symbolic link or other final reparse-point alias. Select its real local file so SQLite and the owner lease use the same location.");
                if((attributes&FileAttributes.Directory)!=0) throw new InvalidOperationException("Select a database file, not a directory.");
            } catch(FileNotFoundException) {} catch(DirectoryNotFoundException) {}
            return database;
        }
    }
}
