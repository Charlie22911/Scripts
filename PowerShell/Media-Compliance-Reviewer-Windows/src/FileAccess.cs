using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
namespace MediaReviewer {
    internal sealed class SourceEntry {
        internal string Name; internal FileAttributes Attributes; internal long Size,ModifiedTicks;
        internal bool Directory { get { return (Attributes&FileAttributes.Directory)!=0; } }
        internal bool Reparse { get { return (Attributes&FileAttributes.ReparsePoint)!=0; } }
    }
    internal sealed class FileStamp {
        internal long Size,ModifiedTicks; internal string Identity;
        internal bool Same(FileStamp other) { return Size==other.Size && ModifiedTicks==other.ModifiedTicks && Identity==other.Identity; }
    }
    internal sealed class FileChangedException:IOException { internal FileChangedException(string message):base(message){} }
    internal static class FileAccessNative {
        [StructLayout(LayoutKind.Sequential)] internal struct Time { internal uint Low,High; }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct FindData {
            internal uint Attributes; internal Time Creation,Access,Write; internal uint SizeHigh,SizeLow,Reserved0,Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] internal string Name;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=14)] internal string Alternate;
        }
        [StructLayout(LayoutKind.Sequential)] struct HandleInfo {
            internal uint Attributes; internal Time Creation,Access,Write; internal uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;
        }
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr FindFirstFile(string pattern,out FindData data);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool FindNextFile(IntPtr handle,out FindData data);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool FindClose(IntPtr handle);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetFileInformationByHandle(IntPtr handle,out HandleInfo data);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name,uint access,uint sharing,IntPtr security,uint creation,uint flags,IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file,int kind,ref int value,int bytes);
        internal static bool DeleteOwnedFile(string path,string identity){
            if(String.IsNullOrEmpty(identity))return false;
            using(var handle=CreateFile(Paths.Io(path),0x80010000,0,IntPtr.Zero,3,0x80,IntPtr.Zero)){
                if(handle.IsInvalid)return false;
                using(var stream=new FileStream(handle,FileAccess.Read)){if(Stamp(stream).Identity!=identity)return false;int remove=1;return SetFileInformationByHandle(handle,4,ref remove,4);}
            }
        }
        static long Ticks(Time time) { return DateTime.FromFileTimeUtc(((long)time.High<<32)|time.Low).Ticks; }
        internal static IEnumerable<SourceEntry> Entries(string path,CancellationToken token) {
            token.ThrowIfCancellationRequested(); FindData data;
            IntPtr handle=FindFirstFile(Paths.Io(path).TrimEnd('\\')+@"\*",out data);
            if(handle==new IntPtr(-1)) {
                int code=Marshal.GetLastWin32Error(); if(code==2) yield break;
                throw new Win32Exception(code,"Cannot list "+Paths.Friendly(path));
            }
            try {
                while(true) {
                    token.ThrowIfCancellationRequested();
                    if(data.Name!="." && data.Name!="..") yield return new SourceEntry { Name=data.Name,Attributes=(FileAttributes)data.Attributes,Size=((long)data.SizeHigh<<32)|data.SizeLow,ModifiedTicks=Ticks(data.Write) };
                    if(!FindNextFile(handle,out data)) { int code=Marshal.GetLastWin32Error(); if(code!=18) throw new Win32Exception(code,"Directory listing stopped: "+Paths.Friendly(path)); break; }
                }
            } finally { FindClose(handle); }
        }
        internal static FileStamp Stamp(FileStream stream) {
            HandleInfo value;
            if(!GetFileInformationByHandle(stream.SafeFileHandle.DangerousGetHandle(),out value)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot read opened file metadata");
            return new FileStamp { Size=((long)value.SizeHigh<<32)|value.SizeLow,ModifiedTicks=Ticks(value.Write),Identity=value.Volume.ToString("x8")+":"+value.IndexHigh.ToString("x8")+value.IndexLow.ToString("x8") };
        }
        internal static bool Transient(Exception error) {
            if(error is FileChangedException) return true;
            var sqlite=error as DatabaseException; if(sqlite!=null) return sqlite.IsTransient;
            if(error is UnauthorizedAccessException || error is InvalidDataException || error is FileNotFoundException || error is DirectoryNotFoundException) return false;
            var win32=error as Win32Exception; int code=win32==null ? (error.HResult&65535) : win32.NativeErrorCode;
            return code==32 || code==33 || code==53 || code==59 || code==64 || code==67 || code==121 || code==1231 || code==1232;
        }
        internal static FileRecord Fingerprint(string path,FileRecord record,byte[] buffer,CancellationToken token,Action<int> progress,long cutoff,FingerprintPolicy policy=null) {
            policy=policy??FingerprintPolicy.Full();policy.Validate();token.ThrowIfCancellationRequested();
            using(var stream=new FileStream(Paths.Io(path),FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan)) {
                return FingerprintOpened(path,stream,record,buffer,token,progress,cutoff,policy);
            }
        }
        internal static FileRecord FingerprintScoped(string path,FileRecord record,byte[] buffer,CancellationToken token,Action<int> progress,long cutoff,FingerprintPolicy policy,string root){
            token.ThrowIfCancellationRequested();using(var stream=new FileStream(Paths.Io(path),FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan)){Paths.OpenedSourceWithinRoot(stream,root);return FingerprintOpened(path,stream,record,buffer,token,progress,cutoff,policy);}
        }
        // The caller owns the stream. Verification keeps this read-only lease across both hashes.
        internal static FileRecord FingerprintOpened(string path,FileStream stream,FileRecord record,byte[] buffer,CancellationToken token,Action<int> progress,long cutoff,FingerprintPolicy policy=null) {
                policy=policy??FingerprintPolicy.Full();policy.Validate();token.ThrowIfCancellationRequested();
                FileStamp before=Stamp(stream); if(cutoff>0 && before.ModifiedTicks<cutoff) return null;
                if(Identity.RecycleMetadata(path,stream,before.Size,buffer,token)){
                    token.ThrowIfCancellationRequested();if(!before.Same(Stamp(stream)))throw new FileChangedException("Recycle metadata changed during validation");return null;
                }
                bool sampled=policy.Mode=="Sampled" && before.Size>policy.ThresholdBytes;
                if(sampled && before.Size<policy.WindowCount*2)sampled=false;
                if(sampled && before.Size>9007199254740991L)throw new InvalidDataException("File size is outside the version 1 sampling range");
                long total=0,expected=0;
                using(var hash=SHA256.Create()) {
                    if(sampled)Identity.AddText(hash,"MCR-SAMPLED-SHA256-V1\0"+before.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)+"\0");
                    var ranges=sampled ? Identity.Ranges(before.Size,policy) : new[]{new[]{0L,before.Size}};
                    foreach(long[] range in ranges){
                        token.ThrowIfCancellationRequested();stream.Position=range[0];long remaining=range[1]-range[0];expected+=remaining;
                        if(sampled)Identity.AddText(hash,range[0].ToString(System.Globalization.CultureInfo.InvariantCulture)+":"+range[1].ToString(System.Globalization.CultureInfo.InvariantCulture)+";");
                        while(remaining>0){int count=stream.ReadAsync(buffer,0,(int)Math.Min(buffer.Length,remaining),token).GetAwaiter().GetResult();token.ThrowIfCancellationRequested();if(count==0)throw new FileChangedException("Source ended during fingerprinting");hash.TransformBlock(buffer,0,count,buffer,0);remaining-=count;total+=count;if(progress!=null)progress(count);}
                    }
                    token.ThrowIfCancellationRequested();hash.TransformFinalBlock(new byte[0],0,0);FileStamp after=Stamp(stream);
                    if(total!=expected || !before.Same(after)) throw new FileChangedException("Source changed during fingerprinting or a read was incomplete.");
                    record.Size=before.Size;record.ModifiedTicks=before.ModifiedTicks;record.FileIdentity=before.Identity;record.BytesRead=total;
                    record.Algorithm=sampled ? "sampled-sha256-v1" : "sha256";record.AlgorithmVersion=1;record.Sampling=sampled ? policy.Parameters : "";
                    record.Hash=(sampled ? "q2:" : "")+BitConverter.ToString(hash.Hash).Replace("-","").ToLowerInvariant();return record;
                }
        }
    }
}
