using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;
namespace MediaReviewer {
    internal sealed class ImportCell {
        readonly SqliteConnection source;readonly string table,column;readonly long row;readonly bool text;readonly CancellationToken token;readonly Action<string> progress;Dictionary<string,object> projected;internal readonly HashSet<string> LongValues=new HashSet<string>();
        internal ImportCell(SqliteConnection source,string table,long row,string column,bool text,CancellationToken token,Action<string> progress){this.source=source;this.table=table;this.row=row;this.column=column;this.text=text;this.token=token;this.progress=progress;}
        internal Stream Open(){return new SqliteCellStream(source,table,row,column);}
        internal object BindingValue {get{if(!text)return new byte[0];using(var input=Open())using(var reader=new StreamReader(input,Encoding.UTF8,true,4096)){char[] chars=new char[4096];int n=reader.ReadBlock(chars,0,chars.Length);return new string(chars,0,n);}}}
        public override string ToString(){return Convert.ToString(BindingValue,CultureInfo.InvariantCulture);}
        internal Dictionary<string,object> Object(){if(projected==null)using(var input=Open())projected=JsonProjection.Read(input,LongValues,token);return projected;}
        internal IEnumerable<KeyValuePair<string,Dictionary<string,object>>> Members(string property){using(var input=Open())foreach(var entry in JsonProjection.Members(input,property,token))yield return entry;}
        internal void Preserve(SqliteConnection target,string targetTable,long targetRow,string targetColumn){
            using(var output=new ImportedFields.Writer(target,targetTable,targetRow,targetColumn,text ? "utf8" : "bytes",token))using(var input=Open()){
                byte[] bytes=new byte[65536];int n;var clock=Stopwatch.StartNew();long last=0;while((n=input.Read(bytes,0,bytes.Length))>0){token.ThrowIfCancellationRequested();output.Write(bytes,0,n);if(progress!=null && (clock.ElapsedMilliseconds-last>=200 || input.Position==input.Length)){last=clock.ElapsedMilliseconds;progress("Importing "+table+"."+column+": "+input.Position+" / "+input.Length+" bytes read");}}output.Complete();
            }
            foreach(string name in LongValues){using(var output=new ImportedFields.Writer(target,targetTable,targetRow,name,"utf8",token))using(var input=Open())using(var writer=new StreamWriter(output,new UTF8Encoding(false,true),4096,true)){JsonProjection.CopyString(input,name,writer,token);writer.Flush();output.Complete();}}
        }
    }
    internal static class ImportData {
        internal static IEnumerable<object[]> Rows(SqliteConnection source,string table,string columns,string tail,CancellationToken token,Action<string> progress=null,params object[] parameters){
            ImportedFields.Identifier(table);string[] names=columns.Split(',');foreach(string name in names)ImportedFields.Identifier(name);
            using(var stmt=source.Prepare("SELECT rowid FROM "+table+" "+tail)){stmt.Bind(parameters);while(stmt.Step()){
                token.ThrowIfCancellationRequested();long row=(long)stmt.Value(0);object[] values=new object[names.Length+1];
                for(int i=0;i<names.Length;i++){
                    string type=(string)source.Scalar("SELECT typeof("+names[i]+") FROM "+table+" WHERE rowid=?",row);bool large=false;
                    if(type=="text" || type=="blob")using(var cell=new SqliteCellStream(source,table,row,names[i]))large=cell.Length>1024*1024;
                    values[i]=large ? new ImportCell(source,table,row,names[i],type=="text",token,progress) : source.Scalar("SELECT "+names[i]+" FROM "+table+" WHERE rowid=?",row);
                }
                values[names.Length]=row;yield return values;
            }}
        }
        internal static void Preserve(object[] values,SqliteConnection target,string table,long row,string[] targetColumns){for(int i=0;i<targetColumns.Length;i++){var cell=values[i] as ImportCell;if(cell!=null)cell.Preserve(target,table,row,targetColumns[i]);}}
        internal static object[] Values(object[] row){return row.Take(row.Length-1).ToArray();}
        internal static Dictionary<string,object> Object(object value){var cell=value as ImportCell;return cell!=null ? cell.Object() : new JavaScriptSerializer {MaxJsonLength=2*1024*1024,RecursionLimit=100}.Deserialize<Dictionary<string,object>>((string)value);}
    }
    internal static class JsonProjection {
        static readonly HashSet<string> Known=new HashSet<string>(("schemaVersion,databaseSchemaVersion,catalogNormalized,id,createdAt,reviewer,preferences,config,hash,size,hashMethod,sampleDigest,fullSha256,firstSeen,lastSeen,key,rootId,status,notes,reviewedAt,path,name,extension,lastModified,lastScanId,supersededAt,version,mode,extensions,workerCount,excludeUserApplicationData,skipOlderYears,cutoffMs,rootKind,scanArchives,quickVideoHash,quickVideoThresholdMiB,scanMode,scanExtensions,visibleExtensions,itemsPerPage,thumbnailFit,thumbSize,evidenceDatabaseLimitGb,autoSaveMinutes").Split(','));
        static XmlDictionaryReader Reader(Stream source){var q=new XmlDictionaryReaderQuotas {MaxDepth=128,MaxStringContentLength=int.MaxValue,MaxBytesPerRead=65536,MaxNameTableCharCount=65536};return JsonReaderWriterFactory.CreateJsonReader(source,q);}
        static string Name(XmlReader r){return r.GetAttribute("item")??r.LocalName;}
        internal static Dictionary<string,object> Read(Stream source,HashSet<string> longValues,CancellationToken token,HashSet<string> properties=null){using(var reader=Reader(source)){reader.MoveToContent();var value=Value(reader,longValues,token,0,properties) as Dictionary<string,object>;if(value==null)throw new InvalidDataException("Expected a JSON object");return value;}}
        // Project one named object's members at a time; never retain the workspace's scan map.
        internal static IEnumerable<KeyValuePair<string,Dictionary<string,object>>> Members(Stream source,string property,CancellationToken token){
            using(var reader=Reader(source)){
                reader.MoveToContent();int root=reader.Depth;reader.Read();
                while(!reader.EOF){
                    token.ThrowIfCancellationRequested();
                    if(reader.NodeType!=XmlNodeType.Element || reader.Depth!=root+1){reader.Read();continue;}
                    if(Name(reader)!=property){reader.Skip();continue;}
                    if(reader.GetAttribute("type")!="object")throw new InvalidDataException("Expected an imported "+property+" object");
                    int level=reader.Depth;bool empty=reader.IsEmptyElement;reader.Read();if(empty)yield break;
                    while(!(reader.NodeType==XmlNodeType.EndElement && reader.Depth==level)){
                        token.ThrowIfCancellationRequested();if(reader.EOF)throw new InvalidDataException("Incomplete imported "+property+" object");
                        if(reader.NodeType!=XmlNodeType.Element){reader.Read();continue;}
                        string key=Name(reader);if(key.Length>32768)throw new InvalidDataException("Imported scan identifier exceeds its bound");
                        var member=Value(reader,new HashSet<string>(),token,0) as Dictionary<string,object>;
                        if(member==null)throw new InvalidDataException("Expected an imported scan object");
                        yield return new KeyValuePair<string,Dictionary<string,object>>(key,member);
                    }
                    yield break;
                }
            }
        }
        static object Value(XmlReader r,HashSet<string> longValues,CancellationToken token,int depth,HashSet<string> properties=null){
            token.ThrowIfCancellationRequested();if(depth>100)throw new InvalidDataException("Imported JSON nesting is excessive");string type=r.GetAttribute("type")??"string",name=Name(r);
            if(type=="object" || type=="array"){
                bool array=type=="array";var map=new Dictionary<string,object>();var list=new List<object>();int level=r.Depth;bool empty=r.IsEmptyElement;r.Read();
                if(!empty){while(!(r.NodeType==XmlNodeType.EndElement && r.Depth==level)){
                    token.ThrowIfCancellationRequested();if(r.EOF)throw new InvalidDataException("Incomplete imported JSON object");
                    if(r.NodeType!=XmlNodeType.Element){r.Read();continue;}string key=Name(r);
                    if(array){if(list.Count>=10000)throw new InvalidDataException("An imported settings array exceeds its supported bound");list.Add(Value(r,longValues,token,depth+1,properties));}
                    else if((properties??Known).Contains(key)){if(map.ContainsKey(key))throw new InvalidDataException("Duplicate known JSON property: "+key);map.Add(key,Value(r,longValues,token,depth+1,properties));}
                    else r.Skip();
                }r.Read();}
                return array ? (object)list.ToArray() : map;
            }
            int start=r.Depth;bool isEmpty=r.IsEmptyElement;var text=new StringBuilder();char[] chars=new char[4096];int limit=name=="notes" || name=="reviewer" ? 4096 : 32768;bool truncated=false;r.Read();
            if(!isEmpty){while(!(r.NodeType==XmlNodeType.EndElement && r.Depth==start)){
                token.ThrowIfCancellationRequested();if(r.EOF)throw new InvalidDataException("Incomplete imported JSON scalar");
                if(r.NodeType==XmlNodeType.Text){int n;while((n=r.ReadValueChunk(chars,0,chars.Length))>0){int take=Math.Min(n,limit-text.Length);if(take>0)text.Append(chars,0,take);if(take<n)truncated=true;token.ThrowIfCancellationRequested();}}
                r.Read();
            }r.Read();}
            if(truncated){if(depth!=1 || (name!="notes" && name!="reviewer"))throw new InvalidDataException("An imported identity or settings scalar exceeds the Windows field bound");longValues.Add(name);}
            string value=text.ToString();if(type=="null")return null;if(type=="boolean")return Boolean.Parse(value);if(type=="number"){long integer;if(Int64.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out integer))return integer;return Double.Parse(value,NumberStyles.Float,CultureInfo.InvariantCulture);}return value;
        }
        internal static void CopyString(Stream source,string property,TextWriter destination,CancellationToken token){
            using(var r=Reader(source)){r.MoveToContent();int root=r.Depth;r.Read();while(!r.EOF){token.ThrowIfCancellationRequested();if(r.NodeType==XmlNodeType.Element && r.Depth==root+1){if(Name(r)!=property){r.Skip();continue;}int level=r.Depth;r.Read();char[] chars=new char[4096];while(!(r.NodeType==XmlNodeType.EndElement && r.Depth==level)){if(r.EOF)throw new InvalidDataException("Incomplete imported string");if(r.NodeType==XmlNodeType.Text){int n;while((n=r.ReadValueChunk(chars,0,chars.Length))>0){token.ThrowIfCancellationRequested();destination.Write(chars,0,n);}}r.Read();}return;}r.Read();}throw new InvalidDataException("Preserved JSON string is missing");}
        }
    }
}
