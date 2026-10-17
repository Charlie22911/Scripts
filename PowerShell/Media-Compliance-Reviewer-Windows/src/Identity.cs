using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
namespace MediaReviewer {
    public sealed class FingerprintPolicy {
        public string Mode="Sampled";
        public long ThresholdBytes=1024*1024;
        public int AlgorithmVersion=1,WindowCount=10,MaxWindowBytes=1024*1024;
        public static FingerprintPolicy Full(){return new FingerprintPolicy {Mode="Full"};}
        internal FingerprintPolicy Copy(){return (FingerprintPolicy)MemberwiseClone();}
        internal void Validate(){
            if((Mode!="Full" && Mode!="Sampled") || AlgorithmVersion!=1 || ThresholdBytes<0 || WindowCount<2 || WindowCount>32 || MaxWindowBytes<1 || MaxWindowBytes>1024*1024)
                throw new ArgumentException("Invalid fingerprint sampling policy");
        }
        internal string Parameters {get{return "windows="+WindowCount.ToString(CultureInfo.InvariantCulture)+";maxWindowBytes="+MaxWindowBytes.ToString(CultureInfo.InvariantCulture);}}
    }
    public static class Identity {
        // Public synchronous entry point for command-line tools. UI callers use owned background workers.
        public static FileRecord Read(string path,FingerprintPolicy policy,CancellationToken token){
            return FileAccessNative.Fingerprint(path,new FileRecord(),new byte[1024*1024],token,null,0,policy.Copy());
        }
        internal static IEnumerable<long[]> Ranges(long size,FingerprintPolicy policy){
            long width=Math.Max(1,Math.Min(policy.MaxWindowBytes,size/(policy.WindowCount*2)));
            long last=size-width,divisor=policy.WindowCount-1;
            for(int i=0;i<policy.WindowCount;i++){
                // Avoid overflowing the multiplication for large files.
                long start=(last/divisor)*i+((last%divisor)*i)/divisor;
                yield return new[]{start,start+width};
            }
        }
        internal static void AddText(HashAlgorithm hash,string text){byte[] bytes=Encoding.UTF8.GetBytes(text);hash.TransformBlock(bytes,0,bytes.Length,bytes,0);}
        internal static string Digest(string value){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").ToLowerInvariant();}
        internal static bool RecycleMetadata(string path,FileStream stream,long size,byte[] buffer,CancellationToken token){
            if(size<28 || size>65564 || !Regex.IsMatch(Path.GetFileName(path),@"^\$I[a-z0-9]{6}(?:\..*)?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))return false;
            string[] parts=path.Replace('/','\\').Split('\\');bool recycle=false;
            for(int i=0;i<parts.Length-1;i++)if(String.Equals(parts[i],"$Recycle.Bin",StringComparison.OrdinalIgnoreCase)){recycle=true;break;}
            if(!recycle)return false;
            int length=(int)size,read=0;stream.Position=0;
            while(read<length){token.ThrowIfCancellationRequested();int n=stream.ReadAsync(buffer,read,length-read,token).GetAwaiter().GetResult();if(n==0)throw new FileChangedException("Recycle metadata source changed during reading");read+=n;}
            long version=BitConverter.ToInt64(buffer,0);int start=version==1 ? 24 : 28;
            if((version!=1 && version!=2) || (length-start)%2!=0)return false;
            if(version==2){uint count=BitConverter.ToUInt32(buffer,24);if(count==0 || count>32768 || start+count*2!=length || BitConverter.ToUInt16(buffer,length-2)!=0)return false;}
            string original;try{original=new UnicodeEncoding(false,false,true).GetString(buffer,start,length-start);}catch(DecoderFallbackException){return false;}
            int end=original.IndexOf('\0');return end>0 && Regex.IsMatch(original.Substring(0,end),@"^(?:[a-z]:\\|\\\\)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        }
    }
}
