using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GsCyberpunkTotalProfiler;

internal static class CorrelatorEngine
{
    private sealed record GrspFrame(int Id, double StartMs, double EndMs, double StartEpoch, double EndEpoch, double Duration, int Calls, double Inclusive, double Exclusive, bool Partial);
    private sealed record GrspSpike(int FrameId, string Owner, string Target, double Exclusive, double Duration);
    private sealed class Bucket
    {
        public int Index; public double Start, End, StartEpoch, EndEpoch, Exclusive, Inclusive, MaxCall, TopMs; public int Calls, SpikeCount; public string Top = "";
    }
    private sealed record ModRow(int Rank, string Owner, double MsPerSec, double SharePct, double ActivePct, double MaxCall, double MaxFrame, int Spikes, double MaxSpike, string Workload, string Note);
    private sealed class GrspData
    {
        public Dictionary<string,string> Summary = new(StringComparer.OrdinalIgnoreCase);
        public double Start, Stop, Duration;
        public List<GrspFrame> Frames = [];
        public Dictionary<int,Bucket> Buckets = [];
        public Dictionary<int,List<GrspSpike>> SpikesByFrame = [];
        public List<ModRow> ByMod = [];
    }
    private sealed record CetEvent(double StartEpoch, double EndEpoch, double Duration, double Exclusive, string Owner, string Kind, string Target);
    private sealed class CetData
    {
        public double StartEpoch, StartCapture, EpochOffset, Duration;
        public Dictionary<int,Bucket> Buckets = [];
        public List<CetEvent> Spikes = [];
        public List<CetEvent> Scheduler = [];
        public List<Dictionary<string,object?>> ByMod = [];
        public int DroppedTimeline, DroppedSpikes, DroppedSchedulerSpikes, DroppedSchedulerBursts;
    }
    private sealed class CapXData
    {
        public List<double> FrameMs = [], Cpu = [], Gpu = [];
        public List<bool> Dropped = [];
        public double Duration;
    }
    internal sealed record Result(string Report, string Status, string AnalysisJson, string AiMarkdown, string PackageZip, string SyncQuality, double Correlation, double MedianDelta, int FrameOffset);

    public static Result Run(string rawRoot, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var grspDir = Directory.EnumerateFiles(rawRoot, "GRSP_Summary.csv", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).Where(x => x is not null).Cast<string>()
            .OrderByDescending(p => CsvUtil.D(CsvUtil.First(Path.Combine(p, "GRSP_Summary.csv")), "start_unix_ms")).FirstOrDefault()
            ?? throw new FileNotFoundException("Could not find GRSP_Summary.csv in collected Raw data.");
        var cetDir = Directory.EnumerateFiles(rawRoot, "CET_Runtime_Profile_Markers.csv", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault()
            ?? throw new FileNotFoundException("Could not find CET_Runtime_Profile_Markers.csv in collected Raw data.");
        var capxPath = Directory.EnumerateFiles(rawRoot, "*.json", SearchOption.AllDirectories).FirstOrDefault(p => ProfilerServices.CapXDurationMs(p) is not null)
            ?? throw new FileNotFoundException("Could not find a CapFrameX JSON capture in collected Raw data.");

        var g = LoadGrsp(grspDir);
        var c = LoadCet(cetDir);
        var x = LoadCapX(capxPath, g.Duration);
        var align = Align(x, g);
        var startDelta = c.StartEpoch - g.Start;
        var durationDelta = c.Duration - g.Duration;
        var syncQuality = align.Quality;
        if (Math.Abs(startDelta) > 100 || Math.Abs(durationDelta) > 500) syncQuality = syncQuality == "GOOD" ? "FAIR" : syncQuality;

        var frames = BuildFrames(g, c, x, align.Offset);
        var timeline = BuildTimeline(g, c, frames);
        var hitches = frames.Where(r => D(r,"capx_frametime_ms") >= 33.3).OrderByDescending(r => D(r,"capx_frametime_ms")).ToList();

        var frameFields = new[] { "capx_frame_index","grsp_frame_id","frame_start_ms","frame_end_ms","frame_start_unix_ms","frame_start_utc","capx_frametime_ms","capx_cpu_active_ms","capx_gpu_active_ms","capx_dropped","grsp_exclusive_ms","grsp_inclusive_ms","grsp_calls","grsp_top_event_owner","grsp_top_event_target","grsp_top_event_exclusive_ms","grsp_bucket_top_owner","grsp_bucket_top_owner_ms","cet_exact_mod","cet_exact_kind","cet_exact_target","cet_exact_exclusive_ms","cet_exact_duration_ms","cet_exact_overlap_ms","cet_bucket_observed_exclusive_ms","cet_bucket_top_mod","cet_bucket_top_mod_ms","scheduler_owner","scheduler_job","scheduler_duration_ms","scheduler_overlap_ms","evidence_class" };
        var timelineFields = new[] { "bucket_index","bucket_start_ms","bucket_end_ms","bucket_start_unix_ms","capx_avg_frametime_ms","capx_max_frametime_ms","capx_frames","capx_frames_ge_33_3ms","capx_frames_ge_50ms","capx_frames_ge_100ms","grsp_observed_exclusive_ms","grsp_top_owner","grsp_top_owner_ms","cet_observed_exclusive_ms","cet_top_mod","cet_top_mod_ms" };
        var framesPath = Path.Combine(outDir, "GRSP_Combined_Frames.csv");
        var timelinePath = Path.Combine(outDir, "GRSP_Combined_Timeline.csv");
        var hitchesPath = Path.Combine(outDir, "GRSP_Combined_Hitches.csv");
        CsvUtil.Write(framesPath, frameFields, frames);
        CsvUtil.Write(timelinePath, timelineFields, timeline);
        CsvUtil.Write(hitchesPath, frameFields, hitches);

        var avgFt = frames.Count == 0 ? 0 : frames.Average(r => D(r,"capx_frametime_ms"));
        var fts = frames.Select(r => D(r,"capx_frametime_ms")).ToList();
        var perf = new Dictionary<string,object?>
        {
            ["capture_duration_s"] = g.Duration / 1000.0,
            ["aligned_capx_frames"] = frames.Count,
            ["average_fps"] = avgFt > 0 ? 1000.0 / avgFt : 0,
            ["average_frametime_ms"] = avgFt,
            ["p95_frametime_ms"] = Percentile(fts, .95),
            ["p99_frametime_ms"] = Percentile(fts, .99),
            ["maximum_frametime_ms"] = fts.Count == 0 ? 0 : fts.Max(),
            ["frames_ge_33_3_ms"] = fts.Count(v => v >= 33.3),
            ["frames_ge_50_ms"] = fts.Count(v => v >= 50),
            ["frames_ge_100_ms"] = fts.Count(v => v >= 100),
            ["hitch_evidence_classes"] = hitches.GroupBy(r => S(r,"evidence_class")).ToDictionary(k => k.Key, v => v.Count())
        };

        var health = new Dictionary<string,object?>
        {
            ["grsp_frame_quality"] = Get(g.Summary,"frame_quality"),
            ["grsp_shard_merge_ok"] = Get(g.Summary,"shard_merge_ok"),
            ["grsp_unresolved_static_calls"] = Int(Get(g.Summary,"unresolved_static_calls")),
            ["grsp_dropped_spikes"] = Int(Get(g.Summary,"dropped_spikes")),
            ["grsp_dropped_hot_paths"] = Int(Get(g.Summary,"dropped_hot_paths")),
            ["cet_dropped_timeline_rows"] = c.DroppedTimeline,
            ["cet_dropped_spikes"] = c.DroppedSpikes,
            ["cet_dropped_scheduler_spikes"] = c.DroppedSchedulerSpikes,
            ["cet_dropped_scheduler_bursts"] = c.DroppedSchedulerBursts
        };

        var sync = new Dictionary<string,object?>
        {
            ["quality"] = syncQuality,
            ["grsp_start_unix_ms"] = g.Start,
            ["cet_start_unix_ms"] = c.StartEpoch,
            ["start_delta_ms"] = startDelta,
            ["grsp_duration_ms"] = g.Duration,
            ["cet_duration_ms"] = c.Duration,
            ["duration_delta_ms"] = durationDelta,
            ["capx_to_grsp_frame_offset"] = align.Offset,
            ["frametime_correlation"] = align.Correlation,
            ["median_abs_frame_duration_delta_ms"] = align.Median,
            ["mean_abs_frame_duration_delta_ms"] = align.Mean,
            ["aligned_frame_pairs"] = align.Pairs
        };

        var topRs = g.ByMod.Take(25).Select(m => new Dictionary<string,object?>
        {
            ["rank"] = m.Rank,["owner"] = m.Owner,["exclusive_ms_per_sec"] = m.MsPerSec,["share_pct"] = m.SharePct,["active_frame_pct"] = m.ActivePct,
            ["max_call_ms"] = m.MaxCall,["max_frame_ms"] = m.MaxFrame,["spike_count"] = m.Spikes,["max_spike_ms"] = m.MaxSpike,["workload_pattern"] = m.Workload,["attribution_note"] = m.Note
        }).ToList();
        var topCet = c.ByMod.Take(25).ToList();
        var topHitches = hitches.Take(50).Select(r => new Dictionary<string,object?>(r)).ToList();

        var analysis = new Dictionary<string,object?>
        {
            ["schema"] = "grsp-correlator-analysis-v1",
            ["correlator_version"] = ProfilerServices.CorrelatorVersion,
            ["source"] = rawRoot,
            ["interpretation"] = new Dictionary<string,string>
            {
                ["capframex_role"] = "Rendered frametime evidence.",
                ["grsp_role"] = "Observed instrumented REDscript work; not guaranteed complete VM self-time.",
                ["cet_role"] = "Observed CET/Lua runtime work and exact spike spans where available.",
                ["warning"] = "GRSP and CET measurements are synchronized evidence layers and must not be blindly subtracted from CapFrameX frametime.",
                ["largely_unexplained"] = "Neither script profiler shows a large signal for the frame; this does not prove the remainder is native/engine work."
            },
            ["sync"] = sync,
            ["capture_health"] = health,
            ["performance_summary"] = perf,
            ["top_redscript_owners"] = topRs,
            ["top_cet_mods"] = topCet,
            ["top_hitches"] = topHitches
        };
        var analysisPath = Path.Combine(outDir, "GRSP_Analysis.json");
        File.WriteAllText(analysisPath, JsonSerializer.Serialize(analysis, ProfilerServices.JsonOpts) + Environment.NewLine);

        var aiPath = Path.Combine(outDir, "GRSP_AI_Analysis.md");
        File.WriteAllText(aiPath, BuildAiMarkdown(sync, health, perf, g.ByMod, c.ByMod, hitches), new UTF8Encoding(false));
        var reportPath = Path.Combine(outDir, "GRSP_Combined_Report.html");
        File.WriteAllText(reportPath, BuildHtml(sync, health, perf, g.ByMod, c.ByMod, hitches), new UTF8Encoding(false));
        var statusPath = Path.Combine(outDir, "GRSP_Correlator_Status.txt");
        File.WriteAllText(statusPath, BuildStatus(rawRoot, grspDir, cetDir, capxPath, sync, health), new UTF8Encoding(false));

        var packagePath = Path.Combine(outDir, "GRSP_Analysis_Package.zip");
        if (File.Exists(packagePath)) File.Delete(packagePath);
        using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            foreach (var p in new[] { reportPath, framesPath, timelinePath, hitchesPath, analysisPath, aiPath, statusPath })
                zip.CreateEntryFromFile(p, Path.GetFileName(p), CompressionLevel.Optimal);
        }
        return new Result(reportPath, statusPath, analysisPath, aiPath, packagePath, syncQuality, align.Correlation, align.Median, align.Offset);
    }

    private static GrspData LoadGrsp(string dir)
    {
        var g = new GrspData();
        g.Summary = CsvUtil.First(Path.Combine(dir,"GRSP_Summary.csv"));
        g.Start = CsvUtil.D(g.Summary,"start_unix_ms"); g.Stop = CsvUtil.D(g.Summary,"stop_unix_ms"); g.Duration = CsvUtil.D(g.Summary,"duration_ms");
        foreach (var r in CsvUtil.Read(Path.Combine(dir,"GRSP_Frames.csv")))
            g.Frames.Add(new GrspFrame(CsvUtil.I(r,"frame_id"),CsvUtil.D(r,"frame_start_ms"),CsvUtil.D(r,"frame_end_ms"),CsvUtil.D(r,"frame_start_unix_ms"),CsvUtil.D(r,"frame_end_unix_ms"),CsvUtil.D(r,"frame_duration_ms"),CsvUtil.I(r,"total_calls"),CsvUtil.D(r,"observed_inclusive_ms"),CsvUtil.D(r,"exclusive_instrumented_ms"),CsvUtil.B(r,"partial")));
        foreach (var r in CsvUtil.Read(Path.Combine(dir,"GRSP_Timeline.csv")))
        {
            int i=CsvUtil.I(r,"bucket_index"); if(!g.Buckets.TryGetValue(i,out var b)){b=new Bucket{Index=i,Start=CsvUtil.D(r,"bucket_start_ms"),End=CsvUtil.D(r,"bucket_end_ms"),StartEpoch=CsvUtil.D(r,"bucket_start_unix_ms"),EndEpoch=CsvUtil.D(r,"bucket_end_unix_ms")};g.Buckets[i]=b;}
            var ex=CsvUtil.D(r,"exclusive_instrumented_ms"); b.Exclusive+=ex;b.Inclusive+=CsvUtil.D(r,"observed_inclusive_ms");b.Calls+=CsvUtil.I(r,"calls");b.MaxCall=Math.Max(b.MaxCall,CsvUtil.D(r,"max_call_ms"));b.SpikeCount+=CsvUtil.I(r,"spike_count");if(ex>b.TopMs){b.TopMs=ex;b.Top=CsvUtil.S(r,"owner");}
        }
        var spikes=Path.Combine(dir,"GRSP_Spikes.csv"); if(File.Exists(spikes)) foreach(var r in CsvUtil.Read(spikes)){var s=new GrspSpike(CsvUtil.I(r,"frame_id",-1),CsvUtil.S(r,"owner"),CsvUtil.S(r,"target"),CsvUtil.D(r,"exclusive_instrumented_ms"),CsvUtil.D(r,"duration_ms"));if(!g.SpikesByFrame.TryGetValue(s.FrameId,out var l)){l=[];g.SpikesByFrame[s.FrameId]=l;}l.Add(s);} foreach(var l in g.SpikesByFrame.Values) l.Sort((a,b)=>b.Exclusive.CompareTo(a.Exclusive));
        var mods=Path.Combine(dir,"GRSP_ByMod.csv"); if(File.Exists(mods)) foreach(var r in CsvUtil.Read(mods)) g.ByMod.Add(new ModRow(CsvUtil.I(r,"rank"),CsvUtil.S(r,"owner"),CsvUtil.D(r,"exclusive_ms_per_sec"),CsvUtil.D(r,"observed_exclusive_share_pct"),CsvUtil.D(r,"active_frame_pct"),CsvUtil.D(r,"max_call_ms"),CsvUtil.D(r,"max_frame_exclusive_ms"),CsvUtil.I(r,"spike_count"),CsvUtil.D(r,"max_spike_ms"),CsvUtil.S(r,"workload_pattern"),CsvUtil.S(r,"attribution_note")));
        return g;
    }

    private static CetData LoadCet(string dir)
    {
        var c=new CetData(); var markers=CsvUtil.Read(Path.Combine(dir,"CET_Runtime_Profile_Markers.csv")); if(markers.Count==0) throw new InvalidDataException("CET marker file is empty."); var start=markers.FirstOrDefault(r=>CsvUtil.S(r,"Label")=="CAPTURE_START")??markers[0];c.StartCapture=CsvUtil.D(start,"CaptureMs");c.StartEpoch=CsvUtil.D(start,"UnixEpochMs");c.EpochOffset=c.StartEpoch-c.StartCapture;c.Duration=markers.Max(r=>CsvUtil.D(r,"CaptureMs"))-c.StartCapture;
        var tp=Path.Combine(dir,"CET_Runtime_Profile_Timeline.csv"); if(File.Exists(tp)) foreach(var r in CsvUtil.Read(tp)){int i=CsvUtil.I(r,"BucketIndex");if(!c.Buckets.TryGetValue(i,out var b)){b=new Bucket{Index=i,Start=CsvUtil.D(r,"BucketStartMs"),End=CsvUtil.D(r,"BucketEndMs")};c.Buckets[i]=b;}var ex=CsvUtil.D(r,"ExclusiveMs");b.Exclusive+=ex;b.Calls+=CsvUtil.I(r,"Calls");b.MaxCall=Math.Max(b.MaxCall,CsvUtil.D(r,"MaxExclusiveMs"));if(ex>b.TopMs){b.TopMs=ex;b.Top=CsvUtil.S(r,"Mod");}c.DroppedTimeline=Math.Max(c.DroppedTimeline,CsvUtil.I(r,"DroppedTimelineRowsAtDump"));}
        LoadCetEvents(Path.Combine(dir,"CET_Runtime_Profile_Spikes.csv"),false,c,c.Spikes,ref c.DroppedSpikes);LoadCetEvents(Path.Combine(dir,"CET_Runtime_Profile_Scheduler_Spikes.csv"),true,c,c.Scheduler,ref c.DroppedSchedulerSpikes);
        var bursts=Path.Combine(dir,"CET_Runtime_Profile_Scheduler_FrameBursts.csv"); if(File.Exists(bursts)) foreach(var r in CsvUtil.Read(bursts)) c.DroppedSchedulerBursts=Math.Max(c.DroppedSchedulerBursts,CsvUtil.I(r,"DroppedBurstsAtDump"));
        var mp=Path.Combine(dir,"CET_Runtime_Profile_ByMod.csv");if(File.Exists(mp)) foreach(var r in CsvUtil.Read(mp)) c.ByMod.Add(new Dictionary<string,object?>{{"mod",CsvUtil.S(r,"Mod")},{"calls_per_sec",CsvUtil.D(r,"CallsPerSecond")},{"exclusive_ms",CsvUtil.D(r,"ExclusiveTotalMs")},{"exclusive_ms_per_sec",CsvUtil.D(r,"ExclusiveMsPerSecond")},{"one_core_pct",CsvUtil.D(r,"MeasuredOneCorePct")},{"max_exclusive_ms",CsvUtil.D(r,"MaxExclusiveMs")},{"share_pct",CsvUtil.D(r,"MeasuredExclusiveSharePct")},{"coverage",CsvUtil.S(r,"Coverage")}});c.ByMod=c.ByMod.OrderByDescending(r=>Convert.ToDouble(r["exclusive_ms_per_sec"],CultureInfo.InvariantCulture)).ToList();
        return c;
    }

    private static void LoadCetEvents(string path,bool scheduler,CetData c,List<CetEvent> dest,ref int dropped)
    {
        if(!File.Exists(path))return;foreach(var r in CsvUtil.Read(path)){double cs=CsvUtil.D(r,"CaptureStartMs"),ce=CsvUtil.D(r,"CaptureEndMs");if(scheduler){dest.Add(new CetEvent(c.EpochOffset+cs,c.EpochOffset+ce,CsvUtil.D(r,"DurationMs"),CsvUtil.D(r,"DurationMs"),CsvUtil.S(r,"Owner"),CsvUtil.S(r,"JobType"),CsvUtil.S(r,"Job")));}else{dest.Add(new CetEvent(c.EpochOffset+cs,c.EpochOffset+ce,CsvUtil.D(r,"InclusiveMs"),CsvUtil.D(r,"ExclusiveMs"),CsvUtil.S(r,"Mod"),CsvUtil.S(r,"Kind"),CsvUtil.S(r,"Target")));}dropped=Math.Max(dropped,CsvUtil.I(r,"DroppedEventsAtDump"));}dest.Sort((a,b)=>a.StartEpoch.CompareTo(b.StartEpoch));
    }

    private static CapXData LoadCapX(string path,double target)
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(path));var runs=doc.RootElement.GetProperty("Runs");JsonElement best=default;double bestScore=double.MaxValue;bool found=false;foreach(var run in runs.EnumerateArray()){if(!run.TryGetProperty("CaptureData",out var cd)||!cd.TryGetProperty("TimeInSeconds",out var ts)||!cd.TryGetProperty("MsBetweenPresents",out var fm)||ts.GetArrayLength()==0||fm.GetArrayLength()==0)continue;var dur=ts[ts.GetArrayLength()-1].GetDouble()*1000;var score=Math.Abs(dur-target);if(score<bestScore){bestScore=score;best=cd.Clone();found=true;}}if(!found)throw new InvalidDataException("CapFrameX JSON has no usable CaptureData run.");var x=new CapXData();foreach(var v in best.GetProperty("MsBetweenPresents").EnumerateArray())x.FrameMs.Add(Num(v));if(best.TryGetProperty("CpuActive",out var cpu))foreach(var v in cpu.EnumerateArray())x.Cpu.Add(Num(v));if(best.TryGetProperty("GpuActive",out var gpu))foreach(var v in gpu.EnumerateArray())x.Gpu.Add(Num(v));if(best.TryGetProperty("Dropped",out var dr))foreach(var v in dr.EnumerateArray())x.Dropped.Add(v.ValueKind==JsonValueKind.True||(v.ValueKind==JsonValueKind.Number&&v.GetInt32()!=0));while(x.Cpu.Count<x.FrameMs.Count)x.Cpu.Add(0);while(x.Gpu.Count<x.FrameMs.Count)x.Gpu.Add(0);while(x.Dropped.Count<x.FrameMs.Count)x.Dropped.Add(false);x.Duration=best.GetProperty("TimeInSeconds")[best.GetProperty("TimeInSeconds").GetArrayLength()-1].GetDouble()*1000;return x;
    }
    private static double Num(JsonElement e)=>e.ValueKind==JsonValueKind.Number?e.GetDouble():double.TryParse(e.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var d)?d:0;

    private sealed record AlignResult(int Offset,double Correlation,double Median,double Mean,int Pairs,string Quality);
    private static AlignResult Align(CapXData x,GrspData g)
    {
        (double score,double corr,int off,double med,double mean,int pairs)? best=null;for(int off=-50;off<=50;off++){var xs=new List<double>();var ys=new List<double>();var ds=new List<double>();for(int i=0;i<x.FrameMs.Count;i++){int j=i+off;if(j<0||j>=g.Frames.Count)continue;var f=g.Frames[j];if(f.Partial)continue;xs.Add(x.FrameMs[i]);ys.Add(f.Duration);ds.Add(Math.Abs(x.FrameMs[i]-f.Duration));}if(xs.Count<100)continue;var corr=Pearson(xs,ys);var med=Median(ds);var mean=ds.Average();var score=corr-Math.Min(med,10)*.001;if(best is null||score>best.Value.score)best=(score,corr,off,med,mean,xs.Count);}if(best is null)throw new InvalidDataException("Could not align CapFrameX frames to GRSP frames.");var b=best.Value;var q=b.corr>=.90&&b.med<=2?"GOOD":b.corr>=.70?"FAIR":"POOR";return new AlignResult(b.off,b.corr,b.med,b.mean,b.pairs,q);
    }

    private static List<Dictionary<string,object?>> BuildFrames(GrspData g,CetData c,CapXData x,int off)
    {
        var rows=new List<Dictionary<string,object?>>();double cetShift=c.StartEpoch-g.Start;for(int i=0;i<x.FrameMs.Count;i++){int j=i+off;if(j<0||j>=g.Frames.Count)continue;var f=g.Frames[j];var (ce,cov)=TopEvent(c.Spikes,f.StartEpoch,f.EndEpoch);var (se,sov)=TopEvent(c.Scheduler,f.StartEpoch,f.EndEpoch);g.SpikesByFrame.TryGetValue(f.Id,out var rsList);var rs=rsList?.FirstOrDefault();var mid=(f.StartMs+f.EndMs)/2;g.Buckets.TryGetValue(BucketIndex(mid),out var gb);c.Buckets.TryGetValue(BucketIndex(mid-cetShift),out var cb);var cetEx=ce?.Exclusive??0;rows.Add(new Dictionary<string,object?>{{"capx_frame_index",i},{"grsp_frame_id",f.Id},{"frame_start_ms",f.StartMs},{"frame_end_ms",f.EndMs},{"frame_start_unix_ms",f.StartEpoch},{"frame_start_utc",Epoch(f.StartEpoch)},{"capx_frametime_ms",x.FrameMs[i]},{"capx_cpu_active_ms",x.Cpu[i]},{"capx_gpu_active_ms",x.Gpu[i]},{"capx_dropped",x.Dropped[i]},{"grsp_exclusive_ms",f.Exclusive},{"grsp_inclusive_ms",f.Inclusive},{"grsp_calls",f.Calls},{"grsp_top_event_owner",rs?.Owner??""},{"grsp_top_event_target",rs?.Target??""},{"grsp_top_event_exclusive_ms",rs?.Exclusive??0},{"grsp_bucket_top_owner",gb?.Top??""},{"grsp_bucket_top_owner_ms",gb?.TopMs??0},{"cet_exact_mod",ce?.Owner??""},{"cet_exact_kind",ce?.Kind??""},{"cet_exact_target",ce?.Target??""},{"cet_exact_exclusive_ms",cetEx},{"cet_exact_duration_ms",ce?.Duration??0},{"cet_exact_overlap_ms",cov},{"cet_bucket_observed_exclusive_ms",cb?.Exclusive??0},{"cet_bucket_top_mod",cb?.Top??""},{"cet_bucket_top_mod_ms",cb?.TopMs??0},{"scheduler_owner",se?.Owner??""},{"scheduler_job",se?.Target??""},{"scheduler_duration_ms",se?.Duration??0},{"scheduler_overlap_ms",sov},{"evidence_class",Classify(x.FrameMs[i],f.Exclusive,cetEx)}});}return rows;
    }
    private static List<Dictionary<string,object?>> BuildTimeline(GrspData g,CetData c,List<Dictionary<string,object?>> frames,double width=50)
    {
        int count=(int)Math.Ceiling(Math.Max(g.Duration,c.Duration+(c.StartEpoch-g.Start))/width);var cap=new Dictionary<int,(double sum,int n,double max,int a,int b,int d)>();foreach(var f in frames){int i=BucketIndex(D(f,"frame_start_ms"),width);var v=cap.GetValueOrDefault(i);var ft=D(f,"capx_frametime_ms");cap[i]=(v.sum+ft,v.n+1,Math.Max(v.max,ft),v.a+(ft>=33.3?1:0),v.b+(ft>=50?1:0),v.d+(ft>=100?1:0));}var remap=new Dictionary<int,(double ex,string top,double topms,double calls)>();foreach(var b in c.Buckets.Values){double ss=(c.EpochOffset+b.Start)-g.Start,ee=(c.EpochOffset+b.End)-g.Start,sw=Math.Max(.001,ee-ss);int first=BucketIndex(Math.Max(0,ss),width),last=BucketIndex(Math.Max(0,ee-1e-9),width);for(int i=first;i<=last;i++){double ov=Overlap(ss,ee,i*width,(i+1)*width);if(ov<=0)continue;double frac=ov/sw;var v=remap.GetValueOrDefault(i);double cand=b.TopMs*frac;remap[i]=(v.ex+b.Exclusive*frac,cand>v.topms?b.Top:v.top,cand>v.topms?cand:v.topms,v.calls+b.Calls*frac);}}var rows=new List<Dictionary<string,object?>>();for(int i=0;i<count;i++){g.Buckets.TryGetValue(i,out var gb);var xb=cap.GetValueOrDefault(i);var cb=remap.GetValueOrDefault(i);rows.Add(new Dictionary<string,object?>{{"bucket_index",i},{"bucket_start_ms",i*width},{"bucket_end_ms",(i+1)*width},{"bucket_start_unix_ms",g.Start+i*width},{"capx_avg_frametime_ms",xb.n>0?xb.sum/xb.n:0},{"capx_max_frametime_ms",xb.max},{"capx_frames",xb.n},{"capx_frames_ge_33_3ms",xb.a},{"capx_frames_ge_50ms",xb.b},{"capx_frames_ge_100ms",xb.d},{"grsp_observed_exclusive_ms",gb?.Exclusive??0},{"grsp_top_owner",gb?.Top??""},{"grsp_top_owner_ms",gb?.TopMs??0},{"cet_observed_exclusive_ms",cb.ex},{"cet_top_mod",cb.top??""},{"cet_top_mod_ms",cb.topms}});}return rows;
    }

    private static (CetEvent? e,double ov) TopEvent(List<CetEvent> events,double s,double e){CetEvent? best=null;double score=-1,bo=0;foreach(var x in events){if(x.EndEpoch<=s||x.StartEpoch>=e)continue;double ov=Overlap(s,e,x.StartEpoch,x.EndEpoch),sc=x.Exclusive*1000+ov;if(sc>score){score=sc;best=x;bo=ov;}}return(best,bo);}    
    private static string Classify(double frame,double rs,double cet){if(frame<=0)return"UNKNOWN";double rr=rs/frame,cr=cet/frame;bool rs20=rs>=5&&rr>=.20,ce20=cet>=5&&cr>=.20,rm=rs>=10&&rr>=.10,cm=cet>=10&&cr>=.10;if((rs20&&ce20)||(rm&&ce20)||(cm&&rs20))return"MIXED_SCRIPT_SIGNAL";if(rs>=5&&rr>=.35)return"REDSCRIPT_HEAVY";if(cet>=5&&cr>=.35)return"CET_HEAVY";if(rs>=3&&rr>=.12)return"REDSCRIPT_SIGNAL";if(cet>=3&&cr>=.12)return"CET_SIGNAL";if(frame>=33.3)return"LARGELY_UNEXPLAINED";return"NORMAL";}
    private static int BucketIndex(double ms,double w=50)=>Math.Max(0,(int)Math.Floor(ms/w));
    private static double Overlap(double a0,double a1,double b0,double b1)=>Math.Max(0,Math.Min(a1,b1)-Math.Max(a0,b0));
    private static string Epoch(double ms)=>DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(ms)).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",CultureInfo.InvariantCulture);
    private static double Pearson(List<double>x,List<double>y){int n=Math.Min(x.Count,y.Count);if(n<3)return 0;double mx=x.Take(n).Average(),my=y.Take(n).Average(),sx=0,sy=0,sp=0;for(int i=0;i<n;i++){double a=x[i]-mx,b=y[i]-my;sx+=a*a;sy+=b*b;sp+=a*b;}return sx<=0||sy<=0?0:sp/Math.Sqrt(sx*sy);}
    private static double Median(List<double>v){if(v.Count==0)return 0;var a=v.Order().ToArray();return a.Length%2==1?a[a.Length/2]:(a[a.Length/2-1]+a[a.Length/2])/2;}
    private static double Percentile(List<double>v,double p){if(v.Count==0)return 0;var a=v.Order().ToArray();if(a.Length==1)return a[0];double k=(a.Length-1)*p;int lo=(int)Math.Floor(k),hi=(int)Math.Ceiling(k);return lo==hi?a[lo]:a[lo]*(hi-k)+a[hi]*(k-lo);}
    private static double D(IDictionary<string,object?>r,string k)=>r.TryGetValue(k,out var v)&&v is not null?Convert.ToDouble(v,CultureInfo.InvariantCulture):0;
    private static string S(IDictionary<string,object?>r,string k)=>r.TryGetValue(k,out var v)?Convert.ToString(v,CultureInfo.InvariantCulture)??"":"";
    private static string Get(Dictionary<string,string>r,string k)=>r.TryGetValue(k,out var s)?s:"";
    private static int Int(string s)=>int.TryParse(s,NumberStyles.Integer,CultureInfo.InvariantCulture,out var i)?i:0;

    private static string BuildStatus(string raw,string grsp,string cet,string capx,Dictionary<string,object?>sync,Dictionary<string,object?>health)
    {
        var sb=new StringBuilder();sb.AppendLine($"G's TOTAL Profiler Native Correlator {ProfilerServices.CorrelatorVersion}").AppendLine().AppendLine("INPUT").AppendLine($"  Root: {raw}").AppendLine($"  GRSP: {grsp}").AppendLine($"  CET:  {cet}").AppendLine($"  CapX: {capx}").AppendLine().AppendLine("SYNC");foreach(var k in new[]{"quality","grsp_start_unix_ms","cet_start_unix_ms","start_delta_ms","grsp_duration_ms","cet_duration_ms","duration_delta_ms","capx_to_grsp_frame_offset","frametime_correlation","median_abs_frame_duration_delta_ms","mean_abs_frame_duration_delta_ms","aligned_frame_pairs"})sb.AppendLine($"  {k}: {sync[k]}");sb.AppendLine().AppendLine("HEALTH");foreach(var kv in health)sb.AppendLine($"  {kv.Key}: {kv.Value}");sb.AppendLine().AppendLine("INTERPRETATION").AppendLine("  Evidence classes are synchronized profiling signals, not causal proof.").AppendLine("  GRSP and CET measurements are not blindly subtracted from CapFrameX frametime.").AppendLine("  LARGELY_UNEXPLAINED means neither script profiler shows a large signal for that frame.");return sb.ToString();
    }
    private static string BuildAiMarkdown(Dictionary<string,object?>sync,Dictionary<string,object?>health,Dictionary<string,object?>perf,List<ModRow>rs,List<Dictionary<string,object?>>cet,List<Dictionary<string,object?>>h)
    {
        var sb=new StringBuilder("# G's TOTAL Profiler — Combined Analysis\n\n");sb.AppendLine("## Interpretation rules\n\n- CapFrameX is the rendered frametime layer.\n- GRSP is observed instrumented REDscript work, not guaranteed complete VM self-time.\n- CET is observed CET/Lua runtime work; exact spike intervals are preferred.\n- Do **not** calculate `frametime - GRSP - CET = native`. Measurement domains can overlap.\n- `LARGELY_UNEXPLAINED` does not prove native/engine causation.\n");sb.AppendLine("## Synchronization\n").AppendLine($"- Quality: **{sync["quality"]}**").AppendLine($"- GRSP ↔ CET start delta: **{F(sync["start_delta_ms"])} ms**").AppendLine($"- Duration delta: **{F(sync["duration_delta_ms"])} ms**").AppendLine($"- CapFrameX → GRSP frame offset: **{sync["capx_to_grsp_frame_offset"]}**").AppendLine($"- Frametime correlation: **{F(sync["frametime_correlation"],6)}**").AppendLine($"- Median frame-duration delta: **{F(sync["median_abs_frame_duration_delta_ms"],6)} ms**\n");sb.AppendLine("## Performance summary\n").AppendLine($"- Duration: **{F(perf["capture_duration_s"],3)} s**").AppendLine($"- Aligned frames: **{perf["aligned_capx_frames"]}**").AppendLine($"- Average: **{F(perf["average_fps"],2)} FPS / {F(perf["average_frametime_ms"],3)} ms**").AppendLine($"- P95 / P99: **{F(perf["p95_frametime_ms"],3)} / {F(perf["p99_frametime_ms"],3)} ms**").AppendLine($"- Maximum: **{F(perf["maximum_frametime_ms"],3)} ms**").AppendLine($"- Frames ≥33.3 / 50 / 100 ms: **{perf["frames_ge_33_3_ms"]} / {perf["frames_ge_50_ms"]} / {perf["frames_ge_100_ms"]}**\n");sb.AppendLine("## Top REDscript owners\n\n| # | Owner | ms/s | Share | Active frames | Max frame | Pattern |\n|---:|---|---:|---:|---:|---:|---|");foreach(var m in rs.Take(20))sb.AppendLine($"| {m.Rank} | {Md(m.Owner)} | {m.MsPerSec:F3} | {m.SharePct:F2}% | {m.ActivePct:F1}% | {m.MaxFrame:F3} | {Md(m.Workload)} |");sb.AppendLine("\n## Top CET mods\n\n| Mod | ms/s | Share | One core | Max exclusive |\n|---|---:|---:|---:|---:|");foreach(var m in cet.Take(20))sb.AppendLine($"| {Md(Convert.ToString(m["mod"])??"")} | {F(m["exclusive_ms_per_sec"],3)} | {F(m["share_pct"],2)}% | {F(m["one_core_pct"],2)}% | {F(m["max_exclusive_ms"],3)} |");sb.AppendLine("\n## Largest hitches\n\n| Frame | Frametime | REDscript | CET exact | Evidence | CET target |\n|---:|---:|---:|---:|---|---|");foreach(var r in h.Take(50))sb.AppendLine($"| {r["capx_frame_index"]} | {F(r["capx_frametime_ms"],3)} | {F(r["grsp_exclusive_ms"],3)} | {F(r["cet_exact_exclusive_ms"],3)} | {Md(S(r,"evidence_class"))} | {Md(S(r,"cet_exact_mod")+" "+S(r,"cet_exact_target"))} |");return sb.ToString();
    }
    private static string BuildHtml(Dictionary<string,object?>sync,Dictionary<string,object?>health,Dictionary<string,object?>perf,List<ModRow>rs,List<Dictionary<string,object?>>cet,List<Dictionary<string,object?>>h)
    {
        var sb=new StringBuilder("<!doctype html><html><head><meta charset='utf-8'><title>G's TOTAL Profiler Report</title><style>body{font-family:Segoe UI,Arial;background:#11141a;color:#e7ebf3;margin:28px}h1,h2{color:#fff}.cards{display:flex;gap:12px;flex-wrap:wrap}.card{background:#1b2029;border:1px solid #303846;border-radius:8px;padding:14px;min-width:170px}table{border-collapse:collapse;width:100%;background:#171b22;margin:12px 0 28px}th,td{border-bottom:1px solid #2d3440;padding:7px 9px;text-align:left;font-size:13px}th{background:#222833}.muted{color:#aeb7c4}.good{color:#81d39b}.warn{color:#f3c878}code{background:#202630;padding:2px 5px;border-radius:4px}</style></head><body>");sb.AppendLine("<h1>G's Cyberpunk 2077 TOTAL Profiler</h1><p class='muted'>Native .NET combined profiling report. Evidence classes are synchronized signals, not causal verdicts.</p>");sb.AppendLine($"<div class='cards'><div class='card'><b>Sync</b><br><span class='{(Convert.ToString(sync["quality"])=="GOOD"?"good":"warn")}'>{H(sync["quality"])}</span><br>{F(sync["frametime_correlation"],6)} correlation</div><div class='card'><b>Average</b><br>{F(perf["average_fps"],2)} FPS<br>{F(perf["average_frametime_ms"],3)} ms</div><div class='card'><b>P95 / P99</b><br>{F(perf["p95_frametime_ms"],3)} / {F(perf["p99_frametime_ms"],3)} ms</div><div class='card'><b>Worst frame</b><br>{F(perf["maximum_frametime_ms"],3)} ms</div><div class='card'><b>≥33.3 / 50 / 100</b><br>{perf["frames_ge_33_3_ms"]} / {perf["frames_ge_50_ms"]} / {perf["frames_ge_100_ms"]}</div></div>");sb.AppendLine("<h2>Synchronization</h2><table><tr><th>Metric</th><th>Value</th></tr>");foreach(var kv in sync)sb.AppendLine($"<tr><td>{H(kv.Key)}</td><td>{H(kv.Value)}</td></tr>");sb.AppendLine("</table><h2>Top REDscript owners</h2><table><tr><th>#</th><th>Owner</th><th>ms/s</th><th>Share</th><th>Active</th><th>Max frame</th><th>Pattern</th></tr>");foreach(var m in rs.Take(25))sb.AppendLine($"<tr><td>{m.Rank}</td><td>{H(m.Owner)}</td><td>{m.MsPerSec:F3}</td><td>{m.SharePct:F2}%</td><td>{m.ActivePct:F1}%</td><td>{m.MaxFrame:F3}</td><td>{H(m.Workload)}</td></tr>");sb.AppendLine("</table><h2>Top CET mods</h2><table><tr><th>Mod</th><th>ms/s</th><th>Share</th><th>One core</th><th>Max exclusive</th></tr>");foreach(var m in cet.Take(25))sb.AppendLine($"<tr><td>{H(m["mod"])}</td><td>{F(m["exclusive_ms_per_sec"],3)}</td><td>{F(m["share_pct"],2)}%</td><td>{F(m["one_core_pct"],2)}%</td><td>{F(m["max_exclusive_ms"],3)}</td></tr>");sb.AppendLine("</table><h2>Largest hitches</h2><table><tr><th>Frame</th><th>Frametime</th><th>REDscript</th><th>CET exact</th><th>Evidence</th><th>CET target</th></tr>");foreach(var r in h.Take(100))sb.AppendLine($"<tr><td>{r["capx_frame_index"]}</td><td>{F(r["capx_frametime_ms"],3)}</td><td>{F(r["grsp_exclusive_ms"],3)}</td><td>{F(r["cet_exact_exclusive_ms"],3)}</td><td>{H(r["evidence_class"])}</td><td>{H(S(r,"cet_exact_mod")+" / "+S(r,"cet_exact_target"))}</td></tr>");sb.AppendLine("</table><p class='muted'>Do not subtract GRSP and CET from frametime to infer a native remainder. Measurement domains can overlap.</p></body></html>");return sb.ToString();
    }
    private static string F(object?o,int n=3)=>Convert.ToDouble(o??0,CultureInfo.InvariantCulture).ToString("F"+n,CultureInfo.InvariantCulture);
    private static string H(object?o)=>WebUtility.HtmlEncode(Convert.ToString(o,CultureInfo.InvariantCulture)??"");
    private static string Md(string s)=>s.Replace("|","\\|").Replace("\r"," ").Replace("\n"," ");
}
