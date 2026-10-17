using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MediaReviewer {
    public static class Native {
        static readonly List<IntPtr> libraries = new List<IntPtr>();
        static bool ready;
        public static string RuntimeDirectory { get; private set; }
        public static string SqliteVersion { get { return Utf8(Sql.sqlite3_libversion()); } }
        public static string DecoderVersion { get { return Utf8(Vips.vips_version_string()); } }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        public static void ValidateBinary(string path) {
            using(var s=File.OpenRead(path)) using(var r=new BinaryReader(s)) {
                if(s.Length<128 || r.ReadUInt16()!=0x5a4d) throw new InvalidDataException("Invalid PE binary: "+Path.GetFileName(path));
                s.Position=0x3c; long offset=r.ReadUInt32();
                if(offset<64 || offset>s.Length-26) throw new InvalidDataException("Invalid PE header");
                s.Position=offset;
                if(r.ReadUInt32()!=0x4550 || r.ReadUInt16()!=0x8664) throw new InvalidDataException("Native binary architecture must be x64");
            }
        }
        public static void Initialize(string directory) {
            if(ready) return;
            if(!Environment.Is64BitProcess) throw new PlatformNotSupportedException("Use 64-bit Windows PowerShell 5.1.");
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling",false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths",false);
            foreach(string name in new[]{"sqlite3.dll","libglib-2.0-0.dll","libgobject-2.0-0.dll","libvips-42.dll"}) {
                string path=Path.Combine(Path.GetFullPath(directory),name); ValidateBinary(path);
                IntPtr module=LoadLibraryEx(path,IntPtr.Zero,0x1100);
                if(module==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot load private dependency "+name+". A dependency or application-control policy may prevent loading.");
                libraries.Add(module);
            }
            if(Vips.vips_init(Encoding.UTF8.GetBytes("MediaComplianceReviewer\0"))!=0) throw new InvalidOperationException(Utf8(Vips.vips_error_buffer()));
            Vips.vips_cache_set_max(0); Vips.vips_cache_set_max_mem(new UIntPtr(16*1024*1024)); Vips.vips_cache_set_max_files(0); Vips.vips_concurrency_set(1);
            RuntimeDirectory=Path.GetFullPath(directory);
            ready=true;
        }
        internal static byte[] Text(string text) { return Encoding.UTF8.GetBytes(text+"\0"); }
        internal static string Utf8(IntPtr pointer) {
            if(pointer==IntPtr.Zero) return "";
            int count=0; while(Marshal.ReadByte(pointer,count)!=0) { if(++count>2*1024*1024) throw new InvalidDataException("Native text exceeds bounded limit"); }
            byte[] bytes=new byte[count]; Marshal.Copy(pointer,bytes,0,count); return Encoding.UTF8.GetString(bytes);
        }
    }
    public sealed class DatabaseException: IOException {
        public readonly int Code;
        public bool IsTransient { get { return (Code&255)==5 || (Code&255)==6; } }
        internal DatabaseException(int code,string message):base("SQLite "+code+": "+message) { Code=code; }
    }
    internal static class Sql {
        const string D="sqlite3.dll";
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_libversion();
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2(byte[] path,out IntPtr db,int flags,IntPtr vfs);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_extended_result_codes(IntPtr db,int value);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db,int ms);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_exec(IntPtr db,byte[] sql,IntPtr callback,IntPtr data,out IntPtr error);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void sqlite3_free(IntPtr ptr);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(IntPtr db,byte[] sql,int length,out IntPtr stmt,out IntPtr tail);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_bind_parameter_count(IntPtr stmt);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_bind_null(IntPtr stmt,int i);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(IntPtr stmt,int i,long value);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_bind_double(IntPtr stmt,int i,double value);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_bind_text(IntPtr stmt,int i,byte[] value,int length,IntPtr dispose);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_bind_blob(IntPtr stmt,int i,byte[] value,int length,IntPtr dispose);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_bind_zeroblob(IntPtr stmt,int i,int length);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr stmt);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_reset(IntPtr stmt);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_clear_bindings(IntPtr stmt);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_column_type(IntPtr stmt,int i);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(IntPtr stmt,int i);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern double sqlite3_column_double(IntPtr stmt,int i);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text(IntPtr stmt,int i);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_blob(IntPtr stmt,int i);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_column_bytes(IntPtr stmt,int i);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_changes(IntPtr db);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern long sqlite3_last_insert_rowid(IntPtr db);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_backup_init(IntPtr target,byte[] targetName,IntPtr source,byte[] sourceName);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_backup_step(IntPtr backup,int pages);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_backup_finish(IntPtr backup);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_backup_remaining(IntPtr backup);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_backup_pagecount(IntPtr backup);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_blob_open(IntPtr db,byte[] schema,byte[] table,byte[] column,long rowid,int flags,out IntPtr blob);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_blob_read(IntPtr blob,IntPtr buffer,int count,int offset);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_blob_close(IntPtr blob);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_blob_bytes(IntPtr blob);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ProgressCallback(IntPtr state);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void sqlite3_progress_handler(IntPtr db,int instructions,ProgressCallback callback,IntPtr state);
    }
    public sealed class SqliteConnection:IDisposable {
        internal IntPtr Handle;
        public string FilePath { get;private set; }
        readonly int owner;
        public SqliteConnection(string path,bool readOnly) {
            FilePath=Path.GetFullPath(path);
            owner=System.Threading.Thread.CurrentThread.ManagedThreadId;
            int rc=Sql.sqlite3_open_v2(Native.Text(Path.GetFullPath(path)),out Handle,readOnly ? 1 : 2|4,IntPtr.Zero);
            if(rc!=0) { string error=Handle==IntPtr.Zero ? "Cannot open database" : Native.Utf8(Sql.sqlite3_errmsg(Handle)); if(Handle!=IntPtr.Zero) Sql.sqlite3_close_v2(Handle); Handle=IntPtr.Zero; throw new DatabaseException(rc,error); }
            Sql.sqlite3_extended_result_codes(Handle,1); Sql.sqlite3_busy_timeout(Handle,100);
        }
        internal void CheckOwner() { if(Handle==IntPtr.Zero) throw new ObjectDisposedException("SQLite connection"); if(owner!=System.Threading.Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("SQLite connection belongs to another worker"); }
        internal void Check(int rc) { if(rc!=0 && rc!=100 && rc!=101) throw new DatabaseException(rc,Native.Utf8(Sql.sqlite3_errmsg(Handle))); }
        public void Script(string sql) {
            CheckOwner(); IntPtr error; int rc=Sql.sqlite3_exec(Handle,Native.Text(sql),IntPtr.Zero,IntPtr.Zero,out error);
            try { if(rc!=0) throw new DatabaseException(rc,error==IntPtr.Zero ? Native.Utf8(Sql.sqlite3_errmsg(Handle)) : Native.Utf8(error)); }
            finally { if(error!=IntPtr.Zero) Sql.sqlite3_free(error); }
        }
        public SqliteStatement Prepare(string sql) { CheckOwner(); return new SqliteStatement(this,sql); }
        public int Execute(string sql,params object[] args) { using(var stmt=Prepare(sql)) { stmt.Bind(args); while(stmt.Step()){} return Sql.sqlite3_changes(Handle); } }
        public object Scalar(string sql,params object[] args) { using(var stmt=Prepare(sql)) { stmt.Bind(args); return stmt.Step() ? stmt.Value(0) : null; } }
        public List<object[]> Query(string sql,object[] args,int maxRows) {
            if(maxRows<1 || maxRows>10000) throw new ArgumentOutOfRangeException("maxRows","A bounded row limit is required");
            var rows=new List<object[]>(); using(var stmt=Prepare(sql)) { stmt.Bind(args); while(stmt.Step()) { if(rows.Count==maxRows) throw new InvalidOperationException("Query exceeded its bounded row limit"); rows.Add(stmt.Row()); } } return rows;
        }
        public long LastId { get { CheckOwner(); return Sql.sqlite3_last_insert_rowid(Handle); } }
        public void Dispose() { if(Handle!=IntPtr.Zero) { CheckOwner(); Sql.sqlite3_close_v2(Handle); Handle=IntPtr.Zero; } }
    }
    public sealed class SqliteStatement:IDisposable {
        readonly SqliteConnection db; IntPtr stmt;
        internal SqliteStatement(SqliteConnection connection,string sql) { db=connection; IntPtr tail; byte[] bytes=Native.Text(sql); db.Check(Sql.sqlite3_prepare_v2(db.Handle,bytes,bytes.Length-1,out stmt,out tail)); if(stmt==IntPtr.Zero) throw new ArgumentException("Empty SQL statement"); }
        public void Bind(params object[] values) {
            db.CheckOwner(); db.Check(Sql.sqlite3_reset(stmt)); db.Check(Sql.sqlite3_clear_bindings(stmt));
            if(values==null) values=new object[0];
            if(values.Length!=Sql.sqlite3_bind_parameter_count(stmt)) throw new ArgumentException("SQL parameter count mismatch");
            for(int i=0;i<values.Length;i++) {
                object value=values[i];var imported=value as ImportCell;if(imported!=null)value=imported.BindingValue;int n=i+1,rc;
                if(value==null || value==DBNull.Value) rc=Sql.sqlite3_bind_null(stmt,n);
                else if(value is byte[]) { byte[] bytes=(byte[])value; rc=bytes.Length==0 ? Sql.sqlite3_bind_zeroblob(stmt,n,0) : Sql.sqlite3_bind_blob(stmt,n,bytes,bytes.Length,new IntPtr(-1)); }
                else if(value is string) { byte[] bytes=Native.Text((string)value); rc=Sql.sqlite3_bind_text(stmt,n,bytes,bytes.Length-1,new IntPtr(-1)); }
                else if(value is double || value is float || value is decimal) rc=Sql.sqlite3_bind_double(stmt,n,Convert.ToDouble(value,System.Globalization.CultureInfo.InvariantCulture));
                else if(value is bool) rc=Sql.sqlite3_bind_int64(stmt,n,(bool)value ? 1 : 0);
                else rc=Sql.sqlite3_bind_int64(stmt,n,Convert.ToInt64(value,System.Globalization.CultureInfo.InvariantCulture));
                db.Check(rc);
            }
        }
        public bool Step() { db.CheckOwner(); int rc=Sql.sqlite3_step(stmt); db.Check(rc); return rc==100; }
        internal int ColumnType(int column){db.CheckOwner();return Sql.sqlite3_column_type(stmt,column);}
        internal int ColumnBytes(int column){db.CheckOwner();return Sql.sqlite3_column_bytes(stmt,column);}
        public object Value(int i) {
            db.CheckOwner(); int type=Sql.sqlite3_column_type(stmt,i);
            if(type==5) return null;
            if(type==1) return Sql.sqlite3_column_int64(stmt,i);
            if(type==2) return Sql.sqlite3_column_double(stmt,i);
            int length=Sql.sqlite3_column_bytes(stmt,i);
            if(length>(type==4 ? 8*1024*1024 : 2*1024*1024)) throw new InvalidDataException("Column exceeds bounded read limit; use incremental reads");
            byte[] bytes=new byte[length]; if(length!=0) Marshal.Copy(type==4 ? Sql.sqlite3_column_blob(stmt,i) : Sql.sqlite3_column_text(stmt,i),bytes,0,length);
            return type==4 ? (object)bytes : Encoding.UTF8.GetString(bytes);
        }
        public object[] Row() { int count=Sql.sqlite3_column_count(stmt); object[] row=new object[count]; for(int i=0;i<count;i++) row[i]=Value(i); return row; }
        public void Dispose() { if(stmt!=IntPtr.Zero) { db.CheckOwner(); Sql.sqlite3_finalize(stmt); stmt=IntPtr.Zero; } }
    }
    internal static class Vips {
        const string D="libvips-42.dll";
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int vips_init(byte[] name);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr vips_version_string();
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void vips_cache_set_max(int max);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void vips_cache_set_max_mem(UIntPtr max);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void vips_cache_set_max_files(int max);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void vips_concurrency_set(int count);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr vips_error_buffer();
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void vips_error_clear();
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int vips_thumbnail(byte[] path,out IntPtr image,int width,IntPtr end);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int vips_thumbnail_buffer(IntPtr bytes,UIntPtr size,out IntPtr image,int width,IntPtr end);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int vips_thumbnail_image(IntPtr input,out IntPtr image,int width,IntPtr end);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int vips_autorot(IntPtr input,out IntPtr image,IntPtr end);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int vips_gifload(byte[] path,out IntPtr image,IntPtr end);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int vips_webpload(byte[] path,out IntPtr image,IntPtr end);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int vips_tiffload(byte[] path,out IntPtr image,IntPtr end);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int vips_heifload(byte[] path,out IntPtr image,IntPtr end);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int vips_svgload_buffer(IntPtr bytes,UIntPtr size,out IntPtr image,IntPtr end);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int vips_image_get_width(IntPtr image);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int vips_image_get_height(IntPtr image);
        [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int vips_image_write_to_buffer(IntPtr image,byte[] format,out IntPtr buffer,out UIntPtr length,IntPtr end);
        [DllImport("libgobject-2.0-0.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern void g_object_unref(IntPtr image);
        [DllImport("libglib-2.0-0.dll",CallingConvention=CallingConvention.Cdecl)] internal static extern void g_free(IntPtr buffer);
    }
}
