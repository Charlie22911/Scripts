using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
namespace MediaReviewer {
    public sealed class AssetSpec { public string Name,Sha256; public long Length; }
    public sealed class Preparation {
        public volatile bool Done; public volatile string Stage="Preparing private dependencies",Error;
        internal Preparation(string script,string directory,string manifest) {
            new Thread(()=>Run(script,directory,manifest)) { IsBackground=true,Name="MediaReviewer dependency preparation" }.Start();
        }
        void Run(string script,string directory,string manifest) {
            try {
                StartupDiagnostics.EnvironmentDetails(directory);
                var specs=new JavaScriptSerializer().Deserialize<AssetSpec[]>(manifest); var expected=specs.ToDictionary(s=>s.Name,StringComparer.Ordinal);
                using(var reader=new StreamReader(script,Encoding.UTF8,true,65536)) {
                    string line; int completed=0;
                    while((line=reader.ReadLine())!=null) {
                        if(!line.StartsWith("#MCR-ASSET ",StringComparison.Ordinal)) continue;
                        string name=line.Substring(11); AssetSpec spec;
                        if(!expected.TryGetValue(name,out spec) || name!=Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || spec.Length<0 || spec.Length>128L*1024*1024) throw new InvalidDataException("Invalid embedded dependency manifest entry");
                        Stage="Extracting "+name+" ("+(completed+1)+"/"+specs.Length+")";
                        StartupDiagnostics.Record(directory,Stage);
                        string compressed=Path.Combine(directory,name+".gzip"); bool ended=false; long encoded=0;
                        using(var output=File.Create(compressed)) {
                            while((line=reader.ReadLine())!=null) { if(line=="#MCR-END"){ended=true;break;} if(!line.StartsWith("#",StringComparison.Ordinal) || line.Length>32769)throw new InvalidDataException("Malformed embedded dependency payload");byte[] bytes=Convert.FromBase64String(line.Substring(1));encoded+=bytes.Length;if(encoded>128L*1024*1024)throw new InvalidDataException("Compressed dependency exceeds limit");output.Write(bytes,0,bytes.Length); }
                        }
                        if(!ended)throw new InvalidDataException("Truncated embedded dependency");
                        string destination=Path.Combine(directory,name);
                        // The bootstrap helper is already loaded, so validate it in place rather than overwriting it.
                        if(name!="MediaReviewer.Core.dll") {
                            using(var input=File.OpenRead(compressed)) using(var gzip=new GZipStream(input,CompressionMode.Decompress)) using(var output=File.Create(destination)) {
                                byte[] block=new byte[65536];long count=0;int read;
                                while((read=gzip.Read(block,0,block.Length))!=0){count+=read;if(count>spec.Length)throw new InvalidDataException("Expanded dependency exceeds declared length");output.Write(block,0,read);}
                                if(count!=spec.Length)throw new InvalidDataException("Expanded dependency is incomplete");
                            }
                        }
                        using(var input=File.OpenRead(destination)) using(var sha=SHA256.Create()) {
                            if(input.Length!=spec.Length || BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant()!=spec.Sha256)throw new InvalidDataException("Dependency integrity check failed: "+name);
                        }
                        if(name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))Native.ValidateBinary(destination);File.Delete(compressed);expected.Remove(name);completed++;
                    }
                }
                if(expected.Count!=0)throw new InvalidDataException("Embedded dependencies are missing");
                Stage="Loading verified x64 native libraries";StartupDiagnostics.Record(directory,Stage);Native.Initialize(directory);
                if(Native.SqliteVersion!="3.54.0" || Native.DecoderVersion!="8.18.7")throw new InvalidDataException("Unexpected native dependency version");
                Stage="Checking isolated media helper and offline codecs";StartupDiagnostics.Record(directory,Stage);Preview.VerifyDecoder(directory);
                Stage="Checking Windows authenticated encryption";StartupDiagnostics.Record(directory,Stage);Crypto.VerifyAvailable();
                Stage="Ready";
                StartupDiagnostics.Record(directory,"Startup dependency checks completed.");
            } catch(Exception error) { Error=StartupDiagnostics.Error(error);StartupDiagnostics.Record(directory,"Failed stage: "+Stage+Environment.NewLine+error);Stage="Startup failed"; }
            finally {Done=true;}
        }
    }
    public static class Distribution {
        public static Preparation Prepare(string script,string directory,string manifest) { return new Preparation(script,directory,manifest); }
        public static void CleanupIdle(string root,string active) {
            try {
                string basePath=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;int examined=0;
                foreach(string candidate in Directory.EnumerateDirectories(root)) {
                    if(++examined>4)break;
                    string full=Path.GetFullPath(candidate);
                    if(!full.StartsWith(basePath,StringComparison.OrdinalIgnoreCase) || String.Equals(full,Path.GetFullPath(active),StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)continue;
                    string marker=Path.Combine(full,"owner.marker");if(!File.Exists(marker) || File.ReadAllText(marker)!="MediaComplianceReviewer-owned-v1")continue;
                    try {
                        using(var lease=new FileStream(Path.Combine(full,"instance.lease"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
                            // Only flat directories produced by this loader are eligible. No recursive deletion.
                            if(Directory.EnumerateDirectories(full).Any())continue;
                            foreach(string file in Directory.EnumerateFiles(full)) {if(Path.GetFileName(file)=="instance.lease" || Path.GetFileName(file)=="owner.marker")continue;if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)continue;File.Delete(file);}
                        }
                        File.Delete(Path.Combine(full,"instance.lease"));File.Delete(marker);Directory.Delete(full,false);
                    } catch(IOException) {} catch(UnauthorizedAccessException) {}
                }
            } catch(IOException) {} catch(UnauthorizedAccessException) {}
        }
        public static void ScheduleCleanup(string root,string active) {new Thread(()=>CleanupIdle(root,active)){IsBackground=true,Name="MediaReviewer idle cleanup"}.Start();}
    }
}
