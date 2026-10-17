using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
namespace MediaReviewer {
    internal sealed class SqliteCellStream:Stream {
        readonly SqliteConnection db;readonly string table,column;readonly long row;long position;readonly long length;bool disposed;
        internal SqliteCellStream(SqliteConnection db,string table,long row,string column){db.CheckOwner();this.db=db;this.table=table;this.row=row;this.column=column;ImportedFields.Identifier(table);ImportedFields.Identifier(column);IntPtr blob=IntPtr.Zero;try{db.Check(Sql.sqlite3_blob_open(db.Handle,Native.Text("main"),Native.Text(table),Native.Text(column),row,0,out blob));length=Sql.sqlite3_blob_bytes(blob);}finally{if(blob!=IntPtr.Zero)Sql.sqlite3_blob_close(blob);}}
        public override bool CanRead{get{return !disposed;}}public override bool CanSeek{get{return !disposed;}}public override bool CanWrite{get{return false;}}public override long Length{get{return length;}}
        public override long Position{get{return position;}set{if(value<0 || value>length)throw new ArgumentOutOfRangeException("value");position=value;}}
        public override int Read(byte[] buffer,int offset,int count){
            if(disposed)throw new ObjectDisposedException("SQLite cell stream");if(buffer==null || offset<0 || count<0 || offset>buffer.Length-count)throw new ArgumentOutOfRangeException("buffer");db.CheckOwner();int n=(int)Math.Min(count,length-position);if(n==0)return 0;
            IntPtr blob=IntPtr.Zero;GCHandle pin=default(GCHandle);
            try{db.Check(Sql.sqlite3_blob_open(db.Handle,Native.Text("main"),Native.Text(table),Native.Text(column),row,0,out blob));pin=GCHandle.Alloc(buffer,GCHandleType.Pinned);db.Check(Sql.sqlite3_blob_read(blob,IntPtr.Add(pin.AddrOfPinnedObject(),offset),n,checked((int)position)));position+=n;return n;}
            finally{if(pin.IsAllocated)pin.Free();if(blob!=IntPtr.Zero)Sql.sqlite3_blob_close(blob);}
        }
        public override long Seek(long offset,SeekOrigin origin){Position=origin==SeekOrigin.Begin ? offset : origin==SeekOrigin.Current ? position+offset : length+offset;return position;}
        public override void Flush(){}public override void SetLength(long value){throw new NotSupportedException();}public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}protected override void Dispose(bool disposing){disposed=true;base.Dispose(disposing);}
    }
    public static class ImportedFields {
        internal const int ChunkSize=1024*1024;
        internal static void Identifier(string value){if(String.IsNullOrEmpty(value) || value.Length>64)throw new ArgumentException("Invalid SQLite identifier");foreach(char c in value)if(!(c=='_' || c>='a'&&c<='z' || c>='A'&&c<='Z' || c>='0'&&c<='9'))throw new ArgumentException("Invalid SQLite identifier");}
        public static Stream Open(SqliteConnection db,string table,long row,string column){
            Identifier(table);Identifier(column);object size=db.Scalar("SELECT length FROM imported_fields WHERE owner_table=? AND owner_rowid=? AND column_name=?",table,row,column);
            return size==null ? (Stream)new SqliteCellStream(db,table,row,column) : new Segments(db,table,row,column,(long)size);
        }
        internal static void Delete(SqliteConnection db,string table,long row,string column){db.Execute("DELETE FROM imported_fields WHERE owner_table=? AND owner_rowid=? AND column_name=?",table,row,column);}
        sealed class Segments:Stream {
            readonly SqliteConnection db;readonly string table,column;readonly long row,length;long position;byte[] cached;long cachedIndex=-1;bool disposed;
            internal Segments(SqliteConnection db,string table,long row,string column,long length){this.db=db;this.table=table;this.row=row;this.column=column;this.length=length;}
            public override bool CanRead{get{return !disposed;}}public override bool CanSeek{get{return !disposed;}}public override bool CanWrite{get{return false;}}public override long Length{get{return length;}}
            public override long Position{get{return position;}set{if(value<0 || value>length)throw new ArgumentOutOfRangeException("value");position=value;}}
            public override int Read(byte[] buffer,int offset,int count){
                if(disposed)throw new ObjectDisposedException("Imported field stream");db.CheckOwner();if(buffer==null || offset<0 || count<0 || offset>buffer.Length-count)throw new ArgumentOutOfRangeException("buffer");int read=0;
                while(count>0 && position<length){long index=position/ChunkSize;if(cachedIndex!=index){cached=(byte[])db.Scalar("SELECT bytes FROM imported_field_chunks WHERE owner_table=? AND owner_rowid=? AND column_name=? AND chunk_index=?",table,row,column,index);if(cached==null || cached.Length!=Math.Min(ChunkSize,length-index*ChunkSize))throw new InvalidDataException("Imported field chunk is missing or truncated");cachedIndex=index;}int at=(int)(position%ChunkSize),n=Math.Min(count,cached.Length-at);Buffer.BlockCopy(cached,at,buffer,offset,n);position+=n;offset+=n;count-=n;read+=n;}return read;
            }
            public override long Seek(long offset,SeekOrigin origin){Position=origin==SeekOrigin.Begin ? offset : origin==SeekOrigin.Current ? position+offset : length+offset;return position;}
            public override void Flush(){}public override void SetLength(long value){throw new NotSupportedException();}public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}protected override void Dispose(bool disposing){cached=null;disposed=true;base.Dispose(disposing);}
        }
        internal sealed class Writer:Stream {
            readonly SqliteConnection db;readonly string table,column;readonly long row;readonly CancellationToken token;readonly SHA256 hash=SHA256.Create();readonly byte[] buffer=new byte[ChunkSize];int filled;long index,total;bool complete,disposed;
            internal Writer(SqliteConnection db,string table,long row,string column,string format,CancellationToken token){this.db=db;this.table=table;this.row=row;this.column=column;this.token=token;Delete(db,table,row,column);db.Execute("INSERT INTO imported_fields VALUES(?,?,?,?,0,'')",table,row,column,format);}
            void Store(){if(filled==0)return;token.ThrowIfCancellationRequested();byte[] bytes=buffer;if(filled!=buffer.Length){bytes=new byte[filled];Buffer.BlockCopy(buffer,0,bytes,0,filled);}hash.TransformBlock(bytes,0,filled,bytes,0);db.Execute("INSERT INTO imported_field_chunks VALUES(?,?,?,?,?)",table,row,column,index++,bytes);total+=filled;filled=0;}
            public void Complete(){if(complete)return;Store();hash.TransformFinalBlock(new byte[0],0,0);db.Execute("UPDATE imported_fields SET length=?,sha256=? WHERE owner_table=? AND owner_rowid=? AND column_name=?",total,BitConverter.ToString(hash.Hash).Replace("-","").ToLowerInvariant(),table,row,column);complete=true;}
            public override void Write(byte[] bytes,int offset,int count){if(disposed || complete)throw new ObjectDisposedException("Imported field writer");while(count>0){token.ThrowIfCancellationRequested();int n=Math.Min(buffer.Length-filled,count);Buffer.BlockCopy(bytes,offset,buffer,filled,n);offset+=n;count-=n;filled+=n;if(filled==buffer.Length)Store();}}
            public override bool CanRead{get{return false;}}public override bool CanSeek{get{return false;}}public override bool CanWrite{get{return !disposed;}}public override long Length{get{return total+filled;}}public override long Position{get{return Length;}set{throw new NotSupportedException();}}public override void Flush(){}public override int Read(byte[] b,int o,int n){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}public override void SetLength(long n){throw new NotSupportedException();}protected override void Dispose(bool disposing){if(!disposed){hash.Dispose();Array.Clear(buffer,0,buffer.Length);disposed=true;}base.Dispose(disposing);}
        }
    }
}
