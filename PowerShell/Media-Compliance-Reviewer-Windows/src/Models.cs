using System;
using System.Collections.Generic;
using System.Threading;
namespace MediaReviewer {
    public sealed class FileRecord {
        public string RootId,ScanId,RelativePath,Name,Extension,Hash,FileIdentity,Algorithm="sha256",Sampling="";
        public long Size,ModifiedTicks,JobId,BytesRead;
        public int UserOrdinal,AlgorithmVersion=1;
    }
    public sealed class PageRequest {
        public long Generation; public string Bucket="TO_REVIEW",Search="",Folder="",Type="",RootId="",Sort="found";
        public int Page=1,PageSize=60; public long[] ExistingIds=new long[0];
        internal PageRequest Copy() { var c=(PageRequest)MemberwiseClone(); c.ExistingIds=(long[])ExistingIds.Clone(); return c; }
    }
    public sealed class ReviewRequest {
        public Dictionary<long,long> ExpectedRevisions;
        public string OperationId,Status,Reviewer="",Notes=""; public long[] GroupIds;
        internal ReviewRequest Copy() { var c=(ReviewRequest)MemberwiseClone(); c.GroupIds=GroupIds==null ? null : (long[])GroupIds.Clone();c.ExpectedRevisions=ExpectedRevisions==null ? null : new Dictionary<long,long>(ExpectedRevisions);return c; }
    }
    public sealed class MediaRow {
        public long GroupId,ContentId,Sequence,Size,LocationCount,ModifiedTicks;
        public long ReviewRevision,PreviewRevision;
        public string PreviewRetryState;
        public string RootId,RootPath,RelativePath,Name,Extension,Hash,Status,Reviewer,Notes,PreviewState,PreviewError,FileIdentity,Algorithm;
        public byte[] PreviewBytes; public int PreviewWidth,PreviewHeight;
        public bool SourceIsHistorical,OriginalRetained;
        public string SourcePath { get { return System.IO.Path.Combine(RootPath,RelativePath); } }
    }
    public sealed class PageData {
        public List<MediaRow> Rows=new List<MediaRow>(); public long Total,Revision;
        public long[] Totals=new long[4]; public int Page,PageSize;
        public bool Refill;
    }
    public sealed class UiResult {
        public string RequestId,Kind,Error,OperationId,Value; public long Generation;
        public bool Success,Reconciled; public PageData Page; public long[] GroupIds;
        public IdentityResolutionPreview IdentityPreview;
        public InspectorData Inspector;public LocationPage Locations;
        public ReviewPreferences Preferences;
        public SourceConnection SourceConnection;public SourceRootPage SourceRoots;
        public PreviewRetryBatch PreviewRetry;public List<PreviewRetryBatch> PreviewRetries;
        public EvidenceInspection Evidence;
    }
    public sealed class StatusSnapshot {
        public string Stage="Opening database",Error="",ScanId="",User="",ResumeId="";
        public bool RunningScan,CancelRequested,CompletedWithErrors,BusyOperation;
        public long DiscoveredFiles,ProcessedFiles,FailedFiles,BytesRead,PublishedGroups,Revision,WalBytes,PendingWork;
        public int CompletedUsers,TotalUsers,QueueCount,PreparedQueueCount; public long RetainedBytes;
        public DateTime LastCommitUtc,LastProgressUtc;
        internal StatusSnapshot Copy() { return (StatusSnapshot)MemberwiseClone(); }
    }
    public sealed class ViewGeneration {
        long current;
        public long Advance() { return Interlocked.Increment(ref current); }
        public long Current { get { return Interlocked.Read(ref current); } }
        public bool Accept(long generation) { return generation==Current; }
    }
    internal sealed class BoundedQueue<T> where T:class {
        sealed class Entry { internal T Value; internal long Bytes; }
        readonly Queue<Entry> items=new Queue<Entry>(); readonly object gate=new object();
        readonly int maxItems; readonly long maxBytes; long bytes; bool closed;
        internal long PeakBytes; internal int PeakItems;
        internal BoundedQueue(int count,long retainedBytes) { maxItems=count; maxBytes=retainedBytes; }
        internal int Count { get { lock(gate) return items.Count; } }
        internal long Bytes { get { lock(gate) return bytes; } }
        internal bool TryAdd(T item,long cost) {
            if(cost<0 || cost>maxBytes) throw new InvalidOperationException("Work item exceeds the bounded queue byte limit");
            lock(gate) { if(closed) throw new OperationCanceledException("Work queue is closed"); if(items.Count>=maxItems || bytes+cost>maxBytes) return false;
                items.Enqueue(new Entry { Value=item,Bytes=cost }); bytes+=cost; PeakItems=Math.Max(PeakItems,items.Count); PeakBytes=Math.Max(PeakBytes,bytes); Monitor.PulseAll(gate); return true; }
        }
        internal void Add(T item,long cost,CancellationToken token) {
            while(true) { token.ThrowIfCancellationRequested(); if(TryAdd(item,cost)) return; lock(gate) { if(closed) throw new OperationCanceledException(); Monitor.Wait(gate,25); } }
        }
        internal T Take(CancellationToken token,int waitMs) {
            token.ThrowIfCancellationRequested(); lock(gate) { if(items.Count==0 && !closed && waitMs>0) Monitor.Wait(gate,waitMs); token.ThrowIfCancellationRequested(); if(items.Count==0) return null;
                Entry item=items.Dequeue(); bytes-=item.Bytes; Monitor.PulseAll(gate); return item.Value; }
        }
        internal void Close() { lock(gate) { closed=true; Monitor.PulseAll(gate); } }
    }
}
