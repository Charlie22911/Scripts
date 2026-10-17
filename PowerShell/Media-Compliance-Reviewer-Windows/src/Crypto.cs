using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
namespace MediaReviewer {
    internal static class Cng {
        [StructLayout(LayoutKind.Sequential)] internal struct Auth {
            internal uint Size,Version;internal IntPtr Nonce;internal int NonceLength;
            internal IntPtr Aad;internal int AadLength;internal IntPtr Tag;internal int TagLength;
            internal IntPtr Mac;internal int MacLength;internal uint AadProcessed;internal ulong DataProcessed;internal uint Flags;
        }
        [DllImport("bcrypt.dll",CharSet=CharSet.Unicode)] internal static extern int BCryptOpenAlgorithmProvider(out IntPtr handle,string algorithm,string provider,uint flags);
        [DllImport("bcrypt.dll")] internal static extern int BCryptCloseAlgorithmProvider(IntPtr handle,uint flags);
        [DllImport("bcrypt.dll",CharSet=CharSet.Unicode)] internal static extern int BCryptSetProperty(IntPtr handle,string property,byte[] value,int size,uint flags);
        [DllImport("bcrypt.dll",CharSet=CharSet.Unicode)] internal static extern int BCryptGetProperty(IntPtr handle,string property,out int value,int size,out int returned,uint flags);
        [DllImport("bcrypt.dll")] internal static extern int BCryptDeriveKeyPBKDF2(IntPtr algorithm,byte[] password,int passwordBytes,byte[] salt,int saltBytes,ulong iterations,byte[] key,int keyBytes,uint flags);
        [DllImport("bcrypt.dll")] internal static extern int BCryptGenerateSymmetricKey(IntPtr algorithm,out IntPtr key,IntPtr keyObject,int objectBytes,byte[] secret,int secretBytes,uint flags);
        [DllImport("bcrypt.dll")] internal static extern int BCryptDestroyKey(IntPtr key);
        [DllImport("bcrypt.dll",CharSet=CharSet.Unicode)] internal static extern int BCryptExportKey(IntPtr key,IntPtr exportKey,string blobType,byte[] output,int outputBytes,out int returned,uint flags);
        [DllImport("bcrypt.dll")] internal static extern int BCryptEncrypt(IntPtr key,byte[] input,int inputBytes,ref Auth info,byte[] iv,int ivBytes,byte[] output,int outputBytes,out int returned,uint flags);
        [DllImport("bcrypt.dll")] internal static extern int BCryptDecrypt(IntPtr key,byte[] input,int inputBytes,ref Auth info,byte[] iv,int ivBytes,byte[] output,int outputBytes,out int returned,uint flags);
        internal static void Check(int status){if(status!=0)throw new CryptographicException("CNG authenticated encryption failed (0x"+unchecked((uint)status).ToString("X8")+"). Check the password, authentication data and record integrity.");}
    }
    public sealed class VaultKey:IDisposable {
        IntPtr algorithm,key,keyObject;int objectLength;readonly int owner=Thread.CurrentThread.ManagedThreadId;
        internal IntPtr Handle {get{if(key==IntPtr.Zero)throw new ObjectDisposedException("Vault key");if(owner!=Thread.CurrentThread.ManagedThreadId)throw new InvalidOperationException("Vault key belongs to its operation worker");return key;}}
        internal byte[] ExportMaterial(){byte[] blob=new byte[44];try{int returned;Cng.Check(Cng.BCryptExportKey(Handle,IntPtr.Zero,"KeyDataBlob",blob,blob.Length,out returned,0));if(returned!=44 || BitConverter.ToUInt32(blob,0)!=0x4d42444b || BitConverter.ToUInt32(blob,4)!=1 || BitConverter.ToUInt32(blob,8)!=32)throw new CryptographicException("Unexpected AES-256 key data");byte[] material=new byte[32];Buffer.BlockCopy(blob,12,material,0,32);return material;}finally{Array.Clear(blob,0,blob.Length);}}
        internal VaultKey(byte[] material) {
            try {
                Cng.Check(Cng.BCryptOpenAlgorithmProvider(out algorithm,"AES",null,0));
                byte[] mode=Encoding.Unicode.GetBytes("ChainingModeGCM\0");Cng.Check(Cng.BCryptSetProperty(algorithm,"ChainingMode",mode,mode.Length,0));
                int returned;Cng.Check(Cng.BCryptGetProperty(algorithm,"ObjectLength",out objectLength,4,out returned,0));
                if(objectLength<1 || objectLength>65536)throw new CryptographicException("Unexpected CNG key-object size");
                keyObject=Marshal.AllocHGlobal(objectLength);Cng.Check(Cng.BCryptGenerateSymmetricKey(algorithm,out key,keyObject,objectLength,material,material.Length,0));
            }catch{Dispose();throw;}
        }
        ~VaultKey(){Release();}
        public void Dispose(){Release();GC.SuppressFinalize(this);}
        void Release(){if(key!=IntPtr.Zero){Cng.BCryptDestroyKey(key);key=IntPtr.Zero;}if(keyObject!=IntPtr.Zero){for(int i=0;i<objectLength;i++)Marshal.WriteByte(keyObject,i,0);Marshal.FreeHGlobal(keyObject);keyObject=IntPtr.Zero;}if(algorithm!=IntPtr.Zero){Cng.BCryptCloseAlgorithmProvider(algorithm,0);algorithm=IntPtr.Zero;}}
    }
    public static class Crypto {
        sealed class Pin:IDisposable {
            GCHandle pin;internal IntPtr Address;
            internal Pin(byte[] data){if(data.Length>0){pin=GCHandle.Alloc(data,GCHandleType.Pinned);Address=pin.AddrOfPinnedObject();}}
            public void Dispose(){if(pin.IsAllocated)pin.Free();}
        }
        sealed class Authentication:IDisposable {
            readonly byte[] mac=new byte[16];readonly Pin noncePin,aadPin,tagPin,macPin;
            internal Cng.Auth Info;internal byte[] Tag,Iv=new byte[16];readonly IntPtr aadAddress;readonly int aadLength;
            internal Authentication(byte[] nonce,byte[] aad,byte[] tag){
                if(nonce==null || nonce.Length!=12 || aad==null || aad.Length>1024*1024 || tag.Length!=16)throw new CryptographicException("Invalid bounded GCM nonce, tag or authentication metadata");
                Tag=tag;noncePin=new Pin(nonce);aadPin=new Pin(aad);tagPin=new Pin(tag);macPin=new Pin(mac);aadAddress=aadPin.Address;aadLength=aad.Length;
                Info=new Cng.Auth {Size=(uint)Marshal.SizeOf(typeof(Cng.Auth)),Version=1,Nonce=noncePin.Address,NonceLength=12,Aad=aadAddress,AadLength=aadLength,Tag=tagPin.Address,TagLength=16,Mac=macPin.Address,MacLength=16};
            }
            internal void Part(bool first,bool more){Info.Flags=(Info.Flags&~1u)|(more ? 1u : 0u);Info.Aad=first ? aadAddress : IntPtr.Zero;Info.AadLength=first ? aadLength : 0;}
            public void Dispose(){noncePin.Dispose();aadPin.Dispose();tagPin.Dispose();macPin.Dispose();Array.Clear(mac,0,mac.Length);Array.Clear(Iv,0,Iv.Length);}
        }
        public static VaultKey DeriveKey(byte[] passwordUtf8,byte[] salt,int iterations){
            IntPtr algorithm=IntPtr.Zero;byte[] material=new byte[32];
            try {
                if(passwordUtf8==null || passwordUtf8.Length>65536 || salt==null || salt.Length<8 || salt.Length>1024 || iterations<1)throw new CryptographicException("Invalid bounded password derivation parameters");
                Cng.Check(Cng.BCryptOpenAlgorithmProvider(out algorithm,"SHA256",null,8));
                Cng.Check(Cng.BCryptDeriveKeyPBKDF2(algorithm,passwordUtf8,passwordUtf8.Length,salt,salt.Length,(ulong)iterations,material,material.Length,0));return new VaultKey(material);
            }finally{Array.Clear(material,0,material.Length);if(passwordUtf8!=null)Array.Clear(passwordUtf8,0,passwordUtf8.Length);if(algorithm!=IntPtr.Zero)Cng.BCryptCloseAlgorithmProvider(algorithm,0);}
        }
        public static byte[] Encrypt(VaultKey key,byte[] nonce,byte[] aad,byte[] bytes){
            if(bytes==null || bytes.Length>8*1024*1024)throw new CryptographicException("Use streamed encryption for a large record");
            byte[] output=new byte[bytes.Length+16];
            using(var auth=new Authentication(nonce,aad,new byte[16])){
                int count;auth.Info.Mac=IntPtr.Zero;auth.Info.MacLength=0;
                Cng.Check(Cng.BCryptEncrypt(key.Handle,bytes,bytes.Length,ref auth.Info,null,0,output,bytes.Length,out count,0));
                GC.KeepAlive(key);if(count!=bytes.Length)throw new CryptographicException("Incomplete CNG encryption");Buffer.BlockCopy(auth.Tag,0,output,count,16);return output;
            }
        }
        public static byte[] Decrypt(VaultKey key,byte[] nonce,byte[] aad,byte[] bytes){
            if(bytes==null || bytes.Length<16 || bytes.Length>8*1024*1024+16)throw new CryptographicException("Use streamed authentication for a large record");
            byte[] output=new byte[bytes.Length-16],tag=new byte[16];Buffer.BlockCopy(bytes,bytes.Length-16,tag,0,16);
            try{using(var auth=new Authentication(nonce,aad,tag)){
                int count;auth.Info.Mac=IntPtr.Zero;auth.Info.MacLength=0;
                Cng.Check(Cng.BCryptDecrypt(key.Handle,bytes,bytes.Length-16,ref auth.Info,null,0,output,output.Length,out count,0));
                if(count!=output.Length)throw new CryptographicException("Incomplete CNG decryption");return output;
            }}catch{Array.Clear(output,0,output.Length);throw;}finally{GC.KeepAlive(key);}
        }
        static void ReadExactly(Stream source,byte[] buffer,int count){int at=0,n;while(at<count && (n=source.Read(buffer,at,count-at))>0)at+=n;if(at!=count)throw new EndOfStreamException("Incomplete authenticated record");}
        public static void VerifyGcmStream(VaultKey key,Stream ciphertextWithTag,byte[] nonce,byte[] aad,CancellationToken token){
            TransformStream(key,ciphertextWithTag,null,nonce,aad,false,token);
        }
        // The caller leases immutable ciphertext across both passes. No unauthenticated
        // legacy plaintext is returned by the verification pass.
        public static void DecryptVerifiedStream(VaultKey key,Func<Stream> reopenImmutableCiphertext,byte[] nonce,byte[] aad,Stream destination,CancellationToken token){
            using(var source=reopenImmutableCiphertext()){long start=source.Position;VerifyGcmStream(key,source,nonce,aad,token);source.Position=start;TransformStream(key,source,destination,nonce,aad,false,token);}
        }
        public static void VerifyAvailable(){using(var key=DeriveKey(new byte[]{77,67,82},new byte[16],1000)){byte[] value=new byte[]{1,2,3},nonce=new byte[12],aad=new byte[]{4,5};byte[] plain=Decrypt(key,nonce,aad,Encrypt(key,nonce,aad,value));if(plain.Length!=3 || plain[0]!=1 || plain[2]!=3)throw new CryptographicException("Windows authenticated-encryption self-check failed");Array.Clear(plain,0,plain.Length);}}
        public static void EncryptGcmStream(VaultKey key,Stream plaintext,byte[] nonce,byte[] aad,Stream ciphertextWithTag,CancellationToken token){
            TransformStream(key,plaintext,ciphertextWithTag,nonce,aad,true,token);
        }
        static void TransformStream(VaultKey key,Stream source,Stream destination,byte[] nonce,byte[] aad,bool encrypt,CancellationToken token){
            token.ThrowIfCancellationRequested();
            if(!source.CanSeek)throw new InvalidDataException("Authenticated streaming requires an immutable seekable source");
            long start=source.Position,remaining=source.Length-start-(encrypt ? 0 : 16);
            if(remaining<0)throw new InvalidDataException("Authenticated record has no complete tag");
            byte[] tag=new byte[16],input=new byte[65536],output=new byte[65536];
            if(!encrypt){source.Position=source.Length-16;ReadExactly(source,tag,16);source.Position=start;}
            try{using(var auth=new Authentication(nonce,aad,tag)){
                bool first=true;
                do {
                    token.ThrowIfCancellationRequested();int n=(int)Math.Min(input.Length,remaining);ReadExactly(source,input,n);remaining-=n;auth.Part(first,remaining>0);int written;
                    int rc=encrypt ? Cng.BCryptEncrypt(key.Handle,input,n,ref auth.Info,auth.Iv,auth.Iv.Length,output,output.Length,out written,0) : Cng.BCryptDecrypt(key.Handle,input,n,ref auth.Info,auth.Iv,auth.Iv.Length,output,output.Length,out written,0);
                    Cng.Check(rc);if(written!=n)throw new CryptographicException("Incomplete authenticated streaming operation");
                    if(destination!=null && written>0)destination.Write(output,0,written);Array.Clear(output,0,output.Length);first=false;
                }while(remaining>0);
                if(encrypt)destination.Write(auth.Tag,0,16);
            }}finally{GC.KeepAlive(key);Array.Clear(input,0,input.Length);Array.Clear(output,0,output.Length);Array.Clear(tag,0,tag.Length);}
        }
    }
}
