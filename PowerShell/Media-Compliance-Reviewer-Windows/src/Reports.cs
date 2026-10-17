using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
namespace MediaReviewer {
    public sealed class ReportOptions {
        public string Destination,Format="Csv";
        public bool IncludeEvidence,IncludeSavedPreviews=true,SpreadsheetSafeText=true;
        internal ReportOptions Copy(){return (ReportOptions)MemberwiseClone();}
        internal void Validate(){if(String.IsNullOrEmpty(Destination) || Destination.Length>32000 || (Format!="Csv" && Format!="HtmlDirectory"))throw new ArgumentException("Choose a new CSV file or HTML report folder.");Destination=Paths.Source(Destination);if(Path.GetFileName(Destination).IndexOf(':')>=0)throw new ArgumentException("Alternate output streams are not supported.");}
    }
    // A streamed ownership journal avoids retaining every output filename in memory.
    internal sealed class ReportOutput:IDisposable {
        readonly string destination,partial;readonly bool directory;readonly Func<string,FileStream> fileFactory;FileStream lease,journalFile;StreamWriter journal;string leaseIdentity,journalIdentity,csvIdentity;bool complete;
        internal string Partial {get{return partial;}}
        internal ReportOutput(ReportOptions options,Func<string,FileStream> fileFactory=null){
            this.fileFactory=fileFactory;
            destination=options.Destination;directory=options.Format=="HtmlDirectory";partial=destination+".partial-"+Guid.NewGuid().ToString("N");
            if(File.Exists(Paths.Io(destination)) || Directory.Exists(Paths.Io(destination)))throw new IOException("Report destination already exists. Choose a new name.");
            if(!Directory.Exists(Paths.Io(Path.GetDirectoryName(destination))))throw new DirectoryNotFoundException("The report parent folder does not exist.");
            if(directory){Directory.CreateDirectory(Paths.Io(partial));try{lease=new FileStream(Paths.Io(Path.Combine(partial,".mcr-owner")),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);leaseIdentity=FileAccessNative.Stamp(lease).Identity;journalFile=new FileStream(Paths.Io(Path.Combine(partial,".mcr-files")),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read);journalIdentity=FileAccessNative.Stamp(journalFile).Identity;journal=new StreamWriter(journalFile,new UTF8Encoding(false),4096,true);}catch{Dispose();throw;}}
        }
        internal FileStream Open(string name){
            if(name.IndexOfAny(new[]{'\\','/',':','\t','\r','\n'})>=0 || name=="." || name=="..")throw new ArgumentException("Invalid generated report filename.");
            string path=directory ? Path.Combine(partial,name) : partial;var file=fileFactory==null ? new FileStream(Paths.Io(path),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,65536,FileOptions.SequentialScan) : fileFactory(Paths.Io(path));
            string identity=null;
            try{identity=FileAccessNative.Stamp(file).Identity;if(directory){journal.WriteLine(name+"\t"+identity);journal.Flush();}else csvIdentity=identity;return file;}catch{try{file.Dispose();}catch{}try{FileAccessNative.DeleteOwnedFile(path,identity);}catch(IOException){}catch(UnauthorizedAccessException){}throw;}
        }
        void CloseOwnership(){
            Exception failure=null;var writer=journal;var file=journalFile;var owner=lease;journal=null;journalFile=null;lease=null;
            try{if(writer!=null)writer.Dispose();}catch(Exception error){failure=error;}
            try{if(file!=null)file.Dispose();}catch(Exception error){if(failure==null)failure=error;}
            try{if(owner!=null)owner.Dispose();}catch(Exception error){if(failure==null)failure=error;}
            if(failure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        internal void Publish(){
            CloseOwnership();if(directory)Directory.Move(Paths.Io(partial),Paths.Io(destination));else File.Move(Paths.Io(partial),Paths.Io(destination));complete=true;
            if(directory){try{FileAccessNative.DeleteOwnedFile(Path.Combine(destination,".mcr-files"),journalIdentity);FileAccessNative.DeleteOwnedFile(Path.Combine(destination,".mcr-owner"),leaseIdentity);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
        public void Dispose(){
            // Publication reports close failures. Cleanup must still run without replacing that error.
            try{CloseOwnership();}catch{}if(complete)return;
            if(!directory){try{FileAccessNative.DeleteOwnedFile(partial,csvIdentity);}catch(IOException){}catch(UnauthorizedAccessException){}return;}
            try{if(!Directory.Exists(Paths.Io(partial)) || (File.GetAttributes(Paths.Io(partial))&FileAttributes.ReparsePoint)!=0)return;
                string manifest=Path.Combine(partial,".mcr-files");if(File.Exists(Paths.Io(manifest)))using(var file=new FileStream(Paths.Io(manifest),FileMode.Open,FileAccess.Read,FileShare.Read)){
                    if(FileAccessNative.Stamp(file).Identity!=journalIdentity)return;using(var reader=new StreamReader(file,Encoding.UTF8,false,4096)){string line;while((line=reader.ReadLine())!=null){var fields=line.Split('\t');if(fields.Length!=2 || fields[0].Length>80 || fields[0].IndexOfAny(new[]{'\\','/',':'})>=0 || fields[0]=="." || fields[0]=="..")return;FileAccessNative.DeleteOwnedFile(Path.Combine(partial,fields[0]),fields[1]);}}
                }
                FileAccessNative.DeleteOwnedFile(manifest,journalIdentity);FileAccessNative.DeleteOwnedFile(Path.Combine(partial,".mcr-owner"),leaseIdentity);Directory.Delete(Paths.Io(partial),false);
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
    internal static class ReportText {
        sealed class JoinedReader:TextReader {
            readonly Func<TextReader>[] sources;int index;TextReader current;
            internal JoinedReader(Func<TextReader>[] sources){this.sources=sources;}
            public override int Peek(){while(index<sources.Length){if(current==null)current=sources[index]();int c=current.Peek();if(c>=0)return c;current.Dispose();current=null;index++;}return -1;}
            public override int Read(){return Peek()<0 ? -1 : current.Read();}
            protected override void Dispose(bool disposing){if(current!=null)current.Dispose();base.Dispose(disposing);}
        }
        internal static Func<TextReader> Join(params Func<TextReader>[] fields){return ()=>new JoinedReader(fields);}
        internal static Func<TextReader> Literal(object value){string text=Convert.ToString(value,CultureInfo.InvariantCulture)??"";return ()=>new StringReader(text);}
        internal static Func<TextReader> Field(SqliteConnection db,string table,long row,string column){return ()=>{
            if(db.Scalar("SELECT 1 FROM "+table+" WHERE rowid=? AND "+column+" IS NOT NULL",row)==null)return new StringReader("");
            return (TextReader)new StreamReader(ImportedFields.Open(db,table,row,column),Catalog.ReviewUtf8,false,4096);
        };}
        internal static bool Dangerous(Func<TextReader> source,CancellationToken token){using(var reader=source()){int c;while((c=reader.Read())>=0){token.ThrowIfCancellationRequested();if(c=='\t' || c=='\r' || c=='\n' || c=='=' || c=='+' || c=='-' || c=='@')return true;if(!Char.IsWhiteSpace((char)c))return false;}return false;}}
        internal static void Csv(TextWriter writer,Func<TextReader> source,bool safe,CancellationToken token){
            writer.Write('"');if(safe && Dangerous(source,token))writer.Write('\'');using(var reader=source()){var buffer=new char[4096];int n;while((n=reader.Read(buffer,0,buffer.Length))!=0){token.ThrowIfCancellationRequested();writer.Write(new string(buffer,0,n).Replace("\"","\"\""));}}writer.Write('"');
        }
        internal static void Html(TextWriter writer,Func<TextReader> source,int limit,CancellationToken token){
            using(var reader=source()){int c,count=0;while((c=reader.Read())>=0){if((count++&4095)==0)token.ThrowIfCancellationRequested();if(count>limit){writer.Write(" [continued in report.csv]");break;}switch(c){case '&':writer.Write("&amp;");break;case '<':writer.Write("&lt;");break;case '>':writer.Write("&gt;");break;case '"':writer.Write("&quot;");break;case '\0':writer.Write("&#9216;");break;default:writer.Write((char)c);if(Char.IsHighSurrogate((char)c) && Char.IsLowSurrogate((char)reader.Peek())){writer.Write((char)reader.Read());count++;}break;}}}
        }
        internal static string Small(SqliteConnection db,string table,long row,string column,int limit=1024){using(var reader=Field(db,table,row,column)()){char[] chars=new char[limit+1];int n=reader.ReadBlock(chars,0,chars.Length);if(n>limit)throw new InvalidDataException("Report metadata exceeds its supported bound: "+table+"."+column);return new string(chars,0,n);}}
    }
    internal sealed class ReportWriter:IDisposable {
        internal static readonly string[] Columns={"Status","Hash method","Hash value","Full SHA-256","Root","Relative path","Display path","Media kind","Extension","Duration seconds","Size","Last modified","Archive path","Archive member","Original connected","Reviewed UTC","Reviewer","Notes","Last scan ID"};
        const string Style="body{font:16px Segoe UI,Arial,sans-serif;max-width:1080px;margin:24px auto;padding:0 16px;color:#202b38;background:#f4f6f8}a{color:#064f91}article{background:white;border:1px solid #ccd5df;border-radius:6px;padding:16px;margin:18px 0;overflow-wrap:anywhere}img{max-width:256px;max-height:256px;object-fit:contain;float:right;margin:0 0 12px 18px}dl{display:grid;grid-template-columns:150px 1fr;gap:5px 12px}dt{font-weight:600}dd{margin:0;white-space:pre-wrap;overflow-wrap:anywhere}nav{display:flex;gap:24px;padding:16px 0}article:after{content:'';display:block;clear:both}@media(max-width:600px){img{float:none}dl{grid-template-columns:1fr}dd{margin-bottom:8px}}@media print{nav{display:none}article{break-inside:avoid}}";
        readonly ReportOutput output;readonly ReportOptions options;readonly CancellationToken token;readonly long revision;readonly string at;FileStream csvFile,htmlFile;StreamWriter csv,html;int page,rowsOnPage;internal long Rows;
        internal ReportWriter(ReportOutput output,ReportOptions options,long revision,CancellationToken token){this.output=output;this.options=options;this.revision=revision;this.token=token;at=Catalog.Now();try{csvFile=output.Open("report.csv");csv=new StreamWriter(csvFile,new UTF8Encoding(true,true),65536,true);for(int i=0;i<Columns.Length;i++){if(i>0)csv.Write(',');ReportText.Csv(csv,ReportText.Literal(Columns[i]),false,token);}csv.Write("\r\n");}catch{try{Dispose();}catch{}throw;}}
        void Header(StreamWriter writer,string title){writer.Write("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>");ReportText.Html(writer,ReportText.Literal(title),256,token);writer.Write("</title><style>"+Style+"</style></head><body><h1>");ReportText.Html(writer,ReportText.Literal(title),256,token);writer.Write("</h1><p>Database revision "+revision+". Exported "+at+". Source availability was not checked.</p><p>Report text and saved previews are unencrypted. Originals are not included. <a href=\"report.csv\">Complete CSV</a></p>");}
        string PageName(int number){return "page-"+number.ToString("D6",CultureInfo.InvariantCulture)+".html";}
        void ClosePage(bool more){if(html==null)return;html.Write("<nav><a href=\"index.html\">Summary</a>");if(page>1)html.Write("<a rel=\"prev\" href=\""+PageName(page-1)+"\">Previous page</a>");if(more)html.Write("<a rel=\"next\" href=\""+PageName(page+1)+"\">Next page</a>");html.Write("</nav></body></html>");html.Flush();htmlFile.Flush(true);html.Dispose();html=null;htmlFile.Dispose();htmlFile=null;}
        internal void Row(Func<TextReader>[] fields,byte[] preview){
            token.ThrowIfCancellationRequested();for(int i=0;i<fields.Length;i++){if(i>0)csv.Write(',');ReportText.Csv(csv,fields[i],options.SpreadsheetSafeText,token);}csv.Write("\r\n");Rows++;
            if(options.Format!="HtmlDirectory")return;
            if(html==null || rowsOnPage==128){ClosePage(true);page++;rowsOnPage=0;htmlFile=output.Open(PageName(page));html=new StreamWriter(htmlFile,new UTF8Encoding(false,true),65536,true);Header(html,"Media review report — page "+page);}
            rowsOnPage++;html.Write("<article><h2>Location "+Rows+"</h2>");string mime=PreviewMime(preview);if(options.IncludeSavedPreviews && mime!=null)html.Write("<img alt=\"Saved preview\" loading=\"lazy\" src=\"data:"+mime+";base64,"+Convert.ToBase64String(preview)+"\">");else html.Write("<p>Saved preview unavailable or excluded.</p>");html.Write("<dl>");for(int i=0;i<fields.Length;i++){html.Write("<dt>"+Columns[i]+"</dt><dd>");ReportText.Html(html,fields[i],i==17 ? 16384 : 4096,token);html.Write("</dd>");}html.Write("</dl></article>");
        }
        static string PreviewMime(byte[] bytes){if(bytes==null || bytes.Length>32768)return null;if(bytes.Length>=3 && bytes[0]==255 && bytes[1]==216 && bytes[2]==255)return "image/jpeg";if(bytes.Length>=8 && bytes.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))return "image/png";if(bytes.Length>=6 && Encoding.ASCII.GetString(bytes,0,6).StartsWith("GIF8",StringComparison.Ordinal))return "image/gif";if(bytes.Length>=12 && Encoding.ASCII.GetString(bytes,0,4)=="RIFF" && Encoding.ASCII.GetString(bytes,8,4)=="WEBP")return "image/webp";return null;}
        internal void Complete(long groups){ClosePage(false);csv.Flush();csvFile.Flush(true);csv.Dispose();csv=null;csvFile.Dispose();csvFile=null;if(options.Format=="HtmlDirectory")using(var file=output.Open("index.html"))using(var writer=new StreamWriter(file,new UTF8Encoding(false,true),4096,true)){Header(writer,"Media compliance review report");writer.Write("<p>"+groups+" groups; "+Rows+" reported locations (including rows for groups without a recorded location).</p>");if(page>0)writer.Write("<p><a href=\""+PageName(1)+"\">Open report pages</a> — "+page+" pages, up to 128 locations per page.</p>");writer.Write("<p>HTML shows bounded text; the CSV preserves complete fields. Spreadsheet-safe CSV text: "+(options.SpreadsheetSafeText ? "enabled" : "disabled")+".</p></body></html>");writer.Flush();file.Flush(true);}}
        public void Dispose(){try{if(html!=null)html.Dispose();}finally{try{if(htmlFile!=null)htmlFile.Dispose();}finally{try{if(csv!=null)csv.Dispose();}finally{if(csvFile!=null)csvFile.Dispose();}}}}
    }
    public sealed partial class ReviewerEngine {
        volatile bool reportActive;
        internal Func<string,FileStream> reportFileFactory;
        internal Action reportCompletionLog;
        public bool ReportActive {get{return reportActive;}}
        void RequireReportWritable(){if(reportActive)throw new InvalidOperationException("A report is being exported. Wait or cancel it before changing reviews.");}
        public string RequestReport(ReportOptions options){
            if(options==null)throw new ArgumentNullException("options");var copy=options.Copy();copy.Validate();long epoch=VaultGeneration;if(copy.IncludeEvidence && !VaultUnlocked)throw new InvalidOperationException("Unlock stored originals before including Evidence in a plaintext report.");
            lock(scanGate){if(reportActive || (scan!=null && !scan.Finished) || sourceConnectionBusy)throw new InvalidOperationException("Finish or cancel the scan/source task before exporting a report.");reportActive=true;try{return EnqueueOperation("Report",(db,t,p)=>ExportReport(db,copy,epoch,t,p),4096+copy.Destination.Length*2L,()=>{lock(scanGate)reportActive=false;});}catch{reportActive=false;throw;}}
        }
        string ExportReport(SqliteConnection db,ReportOptions options,long epoch,CancellationToken token,Action<long,long,string> progress){
            long revision=(long)WaitWrite(w=>(object)Catalog.Revision(w));string states=options.IncludeEvidence ? "('NON_COMPLIANT','EVIDENCE')" : "('NON_COMPLIANT')";long after=0,groups=0;
            using(var output=new ReportOutput(options,reportFileFactory)){
                WaitWrite(w=>{Catalog.Log(w,"info","Report",options.Destination,"Writing incomplete export: "+output.Partial);return null;});
                try{using(var writer=new ReportWriter(output,options,revision,token)){
                    while(true){token.ThrowIfCancellationRequested();CheckReportRevision(db,revision);var batch=db.Query("SELECT g.id,g.content_id,r.rowid,g.status,c.size FROM review_groups g JOIN roots r ON r.id=g.root_id JOIN contents c ON c.id=g.content_id WHERE g.retired=0 AND g.status IN "+states+" AND g.id>? ORDER BY g.id LIMIT 32",new object[]{after},32);if(batch.Count==0)break;
                        foreach(var group in batch){token.ThrowIfCancellationRequested();long id=(long)group[0],content=(long)group[1],rootRow=(long)group[2];string status=(string)group[3];after=id;byte[] preview=options.IncludeSavedPreviews ? db.Scalar("SELECT CASE WHEN length(bytes)<=32768 THEN bytes ELSE NULL END FROM previews WHERE content_id=?",content) as byte[] : null;
                            if(status=="EVIDENCE"){
                                using(var evidence=ReadOriginal(db,id,epoch,token,progress,null)){RequireVault(epoch,token);WriteEvidenceRows(db,writer,group,evidence,preview,token);}groups++;progress(writer.Rows,-1,"locations written");continue;
                            }
                            long cursor=0;bool any=false;
                            while(true){token.ThrowIfCancellationRequested();CheckReportRevision(db,revision);var locations=db.Query("SELECT occurrence_order,size,modified_ticks FROM group_occurrences WHERE group_id=? AND superseded=0 AND occurrence_order>? ORDER BY occurrence_order LIMIT 128",new object[]{id,cursor},128);if(locations.Count==0)break;
                                foreach(var location in locations){cursor=(long)location[0];writer.Row(OrdinaryReportFields(db,group,cursor,(long)location[1],(long)location[2],token),preview);any=true;}progress(writer.Rows,-1,"locations written");
                            }if(!any)writer.Row(OrdinaryReportFields(db,group,0,(long)group[4],0,token),preview);groups++;progress(writer.Rows,-1,"locations written");
                        }
                    }CheckReportRevision(db,revision);token.ThrowIfCancellationRequested();if(options.IncludeEvidence)RequireVault(epoch,token);writer.Complete(groups);
                }token.ThrowIfCancellationRequested();output.Publish();string result="Report exported: "+options.Destination+" ("+groups+" groups, revision "+revision+").";
                    // Publication has succeeded. A diagnostic write cannot turn this into an incomplete export.
                    try{WaitWrite(w=>{if(reportCompletionLog!=null)reportCompletionLog();Catalog.Log(w,"info","Report",options.Destination,"Completed report at revision "+revision+": "+groups+" groups.");return null;});}catch(Exception error){return result+" The completion log could not be saved: "+error.Message;}
                    return result;
                }catch(Exception error){try{WaitWrite(w=>{Catalog.Log(w,"error","Report",options.Destination,"Stopped export; incomplete output: "+output.Partial+". "+error.Message);return null;});}catch{}throw;}
            }
        }
        static void CheckReportRevision(SqliteConnection db,long expected){if(Catalog.Revision(db)!=expected)throw new InvalidOperationException("The database changed during report export. No mixed-revision report was published.");}
        static string ReportTime(long ticks){if(ticks<=0)return "";try{return new DateTime(ticks,DateTimeKind.Utc).ToString("o",CultureInfo.InvariantCulture);}catch(ArgumentOutOfRangeException){return "Invalid recorded timestamp";}}
        static string LegacyReportTime(object value){if(value==null || Convert.ToString(value,CultureInfo.InvariantCulture)=="")return "";double milliseconds;if(!Double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out milliseconds) || Double.IsNaN(milliseconds) || Double.IsInfinity(milliseconds))return "Invalid recorded timestamp";try{return new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddMilliseconds(milliseconds).ToString("o",CultureInfo.InvariantCulture);}catch(ArgumentOutOfRangeException){return "Invalid recorded timestamp";}}
        static Dictionary<string,object> ReportMap(object value){return value as Dictionary<string,object>??new Dictionary<string,object>();}
        static object ReportValue(Dictionary<string,object> value,string name){object result;return value.TryGetValue(name,out result) ? result : "";}
        static readonly HashSet<string> ReportProperties=new HashSet<string>(new[]{"duration","archivePath","archiveEntry"});
        static Dictionary<string,object> LegacyReportFields(SqliteConnection db,long row,CancellationToken token){if(row==0 || db.Scalar("SELECT 1 FROM occurrences WHERE rowid=? AND legacy_json IS NOT NULL",row)==null)return new Dictionary<string,object>();using(var stream=ImportedFields.Open(db,"occurrences",row,"legacy_json"))return JsonProjection.Read(stream,new HashSet<string>(),token,ReportProperties);}
        static Func<TextReader>[] OrdinaryReportFields(SqliteConnection db,object[] group,long occurrence,long size,long ticks,CancellationToken token){
            long id=(long)group[0],content=(long)group[1],root=(long)group[2];var legacy=LegacyReportFields(db,occurrence,token);string algorithm=ReportText.Small(db,"contents",content,"algorithm",64),extension=occurrence==0 ? "" : ReportText.Small(db,"occurrences",occurrence,"extension",64);
            var path=occurrence==0 ? ReportText.Literal("") : ReportText.Field(db,"occurrences",occurrence,"path");
            object connected=db.Scalar("SELECT c.path FROM root_source_heads h JOIN source_connections c ON c.id=h.connection_id WHERE h.root_id=(SELECT root_id FROM review_groups WHERE id=?)",id);var currentRoot=connected==null ? ReportText.Field(db,"roots",root,"path") : ReportText.Literal(connected);
            return new[]{ReportText.Literal(group[3]),ReportText.Literal(algorithm=="sha256" ? "Full SHA-256" : "Quick candidate: "+algorithm),ReportText.Field(db,"contents",content,"digest"),algorithm=="sha256" ? ReportText.Field(db,"contents",content,"digest") : ReportText.Literal(""),ReportText.Field(db,"roots",root,"label"),path,
                occurrence==0 ? ReportText.Literal("No recorded location") : ReportText.Join(currentRoot,ReportText.Literal("\\"),path),ReportText.Literal(extension.Length==0 ? "" : Formats.IsVideo(extension) ? "video" : "image"),ReportText.Literal(extension),ReportText.Literal(ReportValue(legacy,"duration")),ReportText.Literal(size),ReportText.Literal(ReportTime(ticks)),ReportText.Literal(ReportValue(legacy,"archivePath")),ReportText.Literal(ReportValue(legacy,"archiveEntry")),ReportText.Literal("Not checked"),ReportText.Field(db,"review_groups",id,"reviewed_at"),ReportText.Field(db,"review_groups",id,"reviewer"),ReportText.Field(db,"review_groups",id,"notes"),occurrence==0 ? ReportText.Literal("") : ReportText.Field(db,"occurrences",occurrence,"last_scan_id")};
        }
        void WriteEvidenceRows(SqliteConnection db,ReportWriter writer,object[] group,EvidenceInspection evidence,byte[] ordinaryPreview,CancellationToken token){
            var metadata=ReportMap(new JavaScriptSerializer {MaxJsonLength=1024*1024,RecursionLimit=32}.DeserializeObject(Catalog.ReviewUtf8.GetString(evidence.Metadata)));
            if(metadata.ContainsKey("displayNotice"))throw new InvalidDataException("Protected report metadata exceeds the bounded report limit; the encrypted original is preserved.");
            long id=(long)group[0],root=(long)group[2];var decision=ReportMap(ReportValue(metadata,"decision"));var content=ReportMap(ReportValue(metadata,"content"));var locations=ReportValue(metadata,"occurrences") as object[];
            bool native=metadata.ContainsKey("captureId");if(native)locations=new object[]{new Dictionary<string,object>{{"path",ReportValue(metadata,"path")},{"extension",Path.GetExtension(Convert.ToString(ReportValue(metadata,"name"))).TrimStart('.')},{"size",evidence.Size}}};
            if(locations==null || locations.Length==0)throw new InvalidDataException("Authenticated Evidence metadata has no readable report locations.");
            foreach(var raw in locations){token.ThrowIfCancellationRequested();var location=ReportMap(raw);var fields=OrdinaryReportFields(db,group,0,evidence.Size,0,token);string path=Convert.ToString(ReportValue(location,"path"),CultureInfo.InvariantCulture),rootPath=native ? Convert.ToString(ReportValue(metadata,"rootPath"),CultureInfo.InvariantCulture) : Convert.ToString(db.Scalar("SELECT path FROM roots WHERE rowid=?",root),CultureInfo.InvariantCulture);string extension=Convert.ToString(ReportValue(location,"extension"),CultureInfo.InvariantCulture);
                fields[1]=ReportText.Literal("Full SHA-256 (authenticated retained original)");fields[2]=fields[3]=ReportText.Literal(evidence.Hash);fields[5]=ReportText.Literal(path);fields[6]=ReportText.Literal(rootPath.TrimEnd('\\')+"\\"+path);fields[7]=ReportText.Literal(Formats.IsVideo(extension) ? "video" : "image");fields[8]=ReportText.Literal(extension);fields[9]=ReportText.Literal(ReportValue(location,"duration"));fields[10]=ReportText.Literal(ReportValue(location,"size"));fields[11]=ReportText.Literal(LegacyReportTime(ReportValue(location,"lastModified")));fields[12]=ReportText.Literal(ReportValue(location,"archivePath"));fields[13]=ReportText.Literal(ReportValue(location,"archiveEntry"));fields[16]=ReportText.Literal(ReportValue(native ? metadata : decision,"reviewer"));fields[17]=ReportText.Literal(ReportValue(native ? metadata : decision,"notes"));fields[18]=ReportText.Literal(ReportValue(location,"lastScanId"));writer.Row(fields,evidence.Preview??ordinaryPreview);
            }
        }
    }
}
