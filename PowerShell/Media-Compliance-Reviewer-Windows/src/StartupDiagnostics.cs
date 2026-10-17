using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
namespace MediaReviewer {
    public static class StartupDiagnostics {
        static readonly object logGate=new object();
        public static string ProcessExit(string tool,int exitCode,string stderr){
            uint code=unchecked((uint)exitCode);string reason="";
            switch(code){
                case 0xc0000135:reason="Windows could not find a required DLL.";break;
                case 0xc0000139:reason="A required DLL entry point is unavailable on this Windows installation.";break;
                case 0xc000007b:reason="Windows rejected an executable or DLL image format.";break;
                case 0xc0000142:reason="A DLL failed to initialize.";break;
                case 0xc000001d:reason="Windows reported an illegal instruction; the exact CPU/build compatibility needs checking.";break;
                case 0xc0000022:reason="Windows reported access denied.";break;
                case 0xc0000428:reason="Windows could not verify an image signature or hash.";break;
                case 0xc0000017:case 0xc000009a:case 0xc000012d:reason="Windows reported insufficient memory or system resources.";break;
                case 0xc0000005:reason="Windows reported an access violation.";break;
            }
            string detail=String.IsNullOrWhiteSpace(stderr) ? "No error text was returned." : stderr.Trim();if(detail.Length>8192)detail=detail.Substring(0,8192);
            return Path.GetFileName(tool)+" exited with code 0x"+code.ToString("X8",CultureInfo.InvariantCulture)+" ("+exitCode.ToString(CultureInfo.InvariantCulture)+"). "+reason+(reason.Length==0 ? "" : " ")+detail;
        }
        public static string Error(Exception error){
            var native=error as Win32Exception;return error.GetType().Name+": "+error.Message+" [HRESULT 0x"+unchecked((uint)error.HResult).ToString("X8",CultureInfo.InvariantCulture)+(native==null ? "" : "; Win32 "+native.NativeErrorCode.ToString(CultureInfo.InvariantCulture))+"]";
        }
        public static void Record(string directory,string text){
            // Best-effort diagnostics must never conceal the original startup failure.
            try{lock(logGate){string path=Path.Combine(directory,"startup.log");if(text.Length>8192)text=text.Substring(0,8192);byte[] bytes=new UTF8Encoding(false).GetBytes(DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)+" "+text+Environment.NewLine);using(var file=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read)){long remaining=65536-file.Length;if(remaining>0)file.Write(bytes,0,(int)Math.Min(bytes.Length,remaining));}}}
            catch(IOException){}catch(UnauthorizedAccessException){}catch(System.Security.SecurityException){}
        }
        static string RegistryText(string key,string name){try{return Convert.ToString(Registry.GetValue(key,name,"unavailable"),CultureInfo.InvariantCulture);}catch(Exception){return "unavailable";}}
        [StructLayout(LayoutKind.Sequential)] sealed class MemoryStatus {public uint Length=(uint)Marshal.SizeOf(typeof(MemoryStatus)),Load;public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;}
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool GlobalMemoryStatusEx([In,Out]MemoryStatus value);
        public static void EnvironmentDetails(string directory){
            Record(directory,"Media Compliance Reviewer "+Product.Version+"; CLR "+Environment.Version+"; 64-bit process="+Environment.Is64BitProcess+"; logical CPUs="+Environment.ProcessorCount);
            Record(directory,"Windows="+RegistryText(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion","ProductName")+"; build="+RegistryText(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion","CurrentBuildNumber")+"."+RegistryText(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion","UBR")+"; .NET Framework release="+RegistryText(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full","Release"));
            Record(directory,"CPU="+RegistryText(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0","ProcessorNameString")+"; manufacturer="+RegistryText(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS","SystemManufacturer"));
            try{var memory=new MemoryStatus();if(GlobalMemoryStatusEx(memory))Record(directory,"Physical total/available="+memory.TotalPhysical+"/"+memory.AvailablePhysical+" bytes; commit limit/available="+memory.TotalPageFile+"/"+memory.AvailablePageFile+" bytes");}catch(Exception){}
            Record(directory,"Decoder isolation: job memory limit=384 MiB; active process limit=2 (helper plus one media tool). No machine policy changes.");
        }
    }
}
