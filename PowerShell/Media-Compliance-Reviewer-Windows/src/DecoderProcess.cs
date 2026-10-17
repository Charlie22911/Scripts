using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
namespace MediaReviewer {
    internal sealed class DecoderProtocolException:IOException {internal DecoderProtocolException(string message):base(message){}}
    internal sealed class DecoderJob:SafeHandleZeroOrMinusOneIsInvalid {
        [StructLayout(LayoutKind.Sequential)] struct Basic { public long ProcessTime,JobTime;public uint Flags;public UIntPtr Minimum,Maximum;public uint Active;public UIntPtr Affinity;public uint Priority,Scheduling; }
        [StructLayout(LayoutKind.Sequential)] struct Io {public ulong Read,Write,Other,ReadBytes,WriteBytes,OtherBytes;}
        [StructLayout(LayoutKind.Sequential)] struct Limits {public Basic Basic;public Io Io;public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob;}
        [StructLayout(LayoutKind.Sequential)] struct Accounting {public long User,Kernel,PeriodUser,PeriodKernel;public uint Faults,Total,Active,Terminated;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr security,string name);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job,int info,ref Limits limits,uint size);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateJobObject(IntPtr job,uint code);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr job,int kind,out Accounting value,uint length,IntPtr returned);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        internal DecoderJob():base(true) {
            SetHandle(CreateJobObject(IntPtr.Zero,null));if(IsInvalid)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot create the private decoder job");
            var limits=new Limits { Basic=new Basic {Flags=0x2000|0x200|8,Active=2},JobMemory=new UIntPtr(384*1024*1024) };
            if(!SetInformationJobObject(handle,9,ref limits,(uint)Marshal.SizeOf(limits))){int code=Marshal.GetLastWin32Error();Dispose();throw new System.ComponentModel.Win32Exception(code,"Cannot apply decoder memory/process limits");}
        }
        internal void Assign(Process process){if(!AssignProcessToJobObject(handle,process.Handle))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Application control or process restrictions prevented private decoder isolation");}
        internal void StopAndWait(){
            if(!TerminateJobObject(handle,0xc000013a))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot stop the owned decoder job; ownership is retained");
            while(true){Accounting value;if(!QueryInformationJobObject(handle,1,out value,(uint)Marshal.SizeOf(typeof(Accounting)),IntPtr.Zero))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot confirm decoder tree exit; ownership is retained");if(value.Active==0)return;Thread.Sleep(20);}
        }
        protected override bool ReleaseHandle(){return CloseHandle(handle);}
    }
    internal sealed class DecoderHost:IDisposable {
        const int Magic=0x4d435244;readonly string directory;readonly int budget;readonly object gate=new object();
        Process process;DecoderJob job;BinaryReader input;BinaryWriter output;Task<string> stderr;bool greeting,started,disposed,retiring;int requests;
        internal DecoderHost(string runtimeDirectory,int budgetMilliseconds=30000){directory=runtimeDirectory;budget=budgetMilliseconds;}
        void Start(){
            if(retiring)Retire();
            if(process!=null && !process.HasExited && requests<100)return;Retire();
            string executable=Path.Combine(directory,"MediaReviewer.Decoder.exe");Native.ValidateBinary(executable);
            job=new DecoderJob();process=new Process {StartInfo=new ProcessStartInfo(executable) {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=directory,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true}};
            try{process.Start();started=true;job.Assign(process);input=new BinaryReader(process.StandardOutput.BaseStream,Encoding.UTF8);output=new BinaryWriter(process.StandardInput.BaseStream,Encoding.UTF8);var errors=process.StandardError;stderr=Task.Run(()=>{var message=new StringBuilder();char[] block=new char[1024];int count;while((count=errors.Read(block,0,block.Length))>0){if(message.Length<8192)message.Append(block,0,Math.Min(count,8192-message.Length));}return message.ToString();});greeting=false;requests=0;output.Write(0x4d435253);output.Flush();}
            catch{Retire();throw;}
        }
        internal PreviewResult Create(string path,long size,long ticks,string identity,int side,int byteLimit,CancellationToken token){
            return CreateScoped(path,size,ticks,identity,side,byteLimit,token,null);
        }
        internal PreviewResult CreateScoped(string path,long size,long ticks,string identity,int side,int byteLimit,CancellationToken token,string root){
            while(!Monitor.TryEnter(gate,50))token.ThrowIfCancellationRequested();
            try{
                token.ThrowIfCancellationRequested();if(disposed)throw new ObjectDisposedException("Decoder host");Start();
                Task<PreviewResult> read=Task.Run(()=>{
                    if(!greeting){if(input.ReadInt32()!=Magic || input.ReadInt32()!=4)throw new DecoderProtocolException("Private decoder protocol/version mismatch");greeting=true;}
                    byte[] name=Encoding.UTF8.GetBytes(path),stamp=Encoding.UTF8.GetBytes(identity??""),scope=Encoding.UTF8.GetBytes(root??"");if(name.Length>131072 || stamp.Length>256 || scope.Length>131072)throw new InvalidDataException("Decoder request exceeds its path/metadata bounds");
                    output.Write(name.Length);output.Write(name);output.Write(size);output.Write(ticks);output.Write(stamp.Length);output.Write(stamp);output.Write(side);output.Write(byteLimit);output.Write(scope.Length);output.Write(scope);output.Flush();
                    int state=input.ReadInt32();if(state!=0){if(state!=1)throw new DecoderProtocolException("Invalid decoder response status");int code=input.ReadInt32(),n=input.ReadInt32();if(code<0 || code>65535 || n<0 || n>16384)throw new DecoderProtocolException("Invalid decoder error frame");byte[] message=input.ReadBytes(n);if(message.Length!=n)throw new EndOfStreamException();string text=Encoding.UTF8.GetString(message);var nativeError=new System.ComponentModel.Win32Exception(code,text);if(FileAccessNative.Transient(nativeError))throw nativeError;throw new InvalidDataException(text);}
                    int width=input.ReadInt32(),height=input.ReadInt32(),sourceWidth=input.ReadInt32(),sourceHeight=input.ReadInt32(),length=input.ReadInt32();if(width<1 || height<1 || width>side || height>side || (long)width*height>InspectionPlan.MaxPixels || sourceWidth<1 || sourceHeight<1 || (long)sourceWidth*sourceHeight>200000000 || length<1 || length>byteLimit)throw new DecoderProtocolException("Private decoder returned an unbounded image");
                    byte[] bytes=input.ReadBytes(length);if(bytes.Length!=length)throw new EndOfStreamException();int actualWidth,actualHeight;try{Preview.Dimensions(bytes,bytes.Length,out actualWidth,out actualHeight);}catch(Exception error){throw new DecoderProtocolException("Invalid encoded preview dimensions: "+error.Message);}if(actualWidth!=width || actualHeight!=height)throw new DecoderProtocolException("Encoded preview dimensions disagree with its helper frame");int labelLength=input.ReadInt32();if(labelLength<0 || labelLength>2048)throw new DecoderProtocolException("Invalid decoder label frame");byte[] label=input.ReadBytes(labelLength);if(label.Length!=labelLength)throw new EndOfStreamException();return new PreviewResult {Bytes=bytes,Width=width,Height=height,SourceWidth=sourceWidth,SourceHeight=sourceHeight,Decoder=Encoding.UTF8.GetString(label)};
                });
                var clock=Stopwatch.StartNew();
                while(!read.IsCompleted){if(token.WaitHandle.WaitOne(25) || clock.ElapsedMilliseconds>=budget){Retire();try{read.GetAwaiter().GetResult();}catch{};token.ThrowIfCancellationRequested();throw new InvalidDataException("Preview exceeded the decoder time budget; its private process tree was stopped. The fingerprint is retained.");}}
                requests++;try{return read.GetAwaiter().GetResult();}catch(DecoderProtocolException){Retire();throw;}catch(EndOfStreamException){string detail=Retire(true);throw new IOException("The private decoder ended before completing its response. "+detail);}catch(IOException){Retire();throw;}
            }finally{Monitor.Exit(gate);}
        }
        string Retire(bool includeExit=false){
            string detail="";retiring=true;int? exit=null;
            // EOF can precede Windows publishing the final process status by a few ticks.
            // This short reap wait collects that status, not a retry of the failed work.
            if(includeExit && process!=null && started && process.WaitForExit(100))exit=process.ExitCode;
            if(job!=null){job.StopAndWait();job.Dispose();job=null;}
            // Confirm exit before replacement: blocked OS I/O must never create abandoned helpers.
            if(process!=null){if(started){if(!process.HasExited){try{process.Kill();}catch(InvalidOperationException){}}process.WaitForExit();}if(input!=null)input.Dispose();if(output!=null)output.Dispose();if(stderr!=null){try{detail=stderr.GetAwaiter().GetResult();}catch{}};process.Dispose();process=null;input=null;output=null;stderr=null;started=false;}retiring=false;return includeExit ? exit.HasValue ? StartupDiagnostics.ProcessExit("MediaReviewer.Decoder.exe",exit.Value,detail) : "MediaReviewer.Decoder.exe closed its response stream and was stopped by its owner before a natural exit status was available. "+detail : detail;
        }
        public void Dispose(){lock(gate){disposed=true;Retire();}}
    }
}
