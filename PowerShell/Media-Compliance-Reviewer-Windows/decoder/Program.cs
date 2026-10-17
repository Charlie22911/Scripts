using System;
using System.IO;
using System.Text;
using MediaReviewer;
[assembly:System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.6.2")]
static class DecoderProgram {
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    static string Text(BinaryReader input,int maximum){int length=input.ReadInt32();if(length<0 || length>maximum)throw new InvalidDataException("Decoder request exceeds its bounds");byte[] bytes=input.ReadBytes(length);if(bytes.Length!=length)throw new EndOfStreamException();return Encoding.UTF8.GetString(bytes);}
    [STAThread] static int Main(){
        try{
            SetErrorMode(0x8003);var input=new BinaryReader(Console.OpenStandardInput(),Encoding.UTF8);var output=new BinaryWriter(Console.OpenStandardOutput(),Encoding.UTF8);if(input.ReadInt32()!=0x4d435253)throw new InvalidDataException("Decoder parent release message is missing");Native.Initialize(AppDomain.CurrentDomain.BaseDirectory);MediaDecode.VerifyTools();output.Write(0x4d435244);output.Write(4);output.Flush();
            for(int count=0;count<100;count++){
                string path;try{path=Text(input,131072);}catch(EndOfStreamException){return 0;}
                long size=input.ReadInt64(),ticks=input.ReadInt64();string identity=Text(input,256);int side=input.ReadInt32(),limit=input.ReadInt32();string root=Text(input,131072);
                try{PreviewResult image=MediaDecode.CreateScoped(path,size,ticks,identity,side,limit,root);byte[] decoder=Encoding.UTF8.GetBytes(image.Decoder);if(decoder.Length>2048)throw new InvalidDataException("Decoder label exceeds its byte limit");output.Write(0);output.Write(image.Width);output.Write(image.Height);output.Write(image.SourceWidth);output.Write(image.SourceHeight);output.Write(image.Bytes.Length);output.Write(image.Bytes);output.Write(decoder.Length);output.Write(decoder);}
                catch(Exception error){var origin=error.GetBaseException();var native=origin as System.ComponentModel.Win32Exception;int code=native==null ? origin.HResult&65535 : native.NativeErrorCode;byte[] message=Encoding.UTF8.GetBytes("Preview "+Product.Version+" / libvips "+Native.DecoderVersion+" / "+error.GetType().Name+": "+error.Message);if(message.Length>16384)Array.Resize(ref message,16384);output.Write(1);output.Write(code);output.Write(message.Length);output.Write(message);}
                output.Flush();
            }return 0;
        }catch(Exception error){Console.Error.WriteLine(StartupDiagnostics.Error(error));return 1;}
    }
}
