using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

// Read-only regression probe. It never calls Enforce, Restore, Write, or Save.
static class MemoryProbe {
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    static void Sample(StreamWriter csv, int iteration, string phase) {
        using (var process=Process.GetCurrentProcess()) {
            process.Refresh();
            csv.WriteLine("{0},{1},{2},{3},{4},{5},{6},{7},{8},{9}", DateTime.UtcNow.ToString("o"), iteration, phase,
                Clock.ElapsedMilliseconds, process.PrivateMemorySize64, GC.GetTotalMemory(false),
                process.WorkingSet64, process.HandleCount, process.Threads.Count, GC.CollectionCount(2));
            csv.Flush();
        }
    }
    [MTAThread]
    static int Main(string[] args) {
        if(args.Length!=3) { Console.Error.WriteLine("MemoryProbe <target.exe> <output-directory> <scan-count>"); return 2; }
        try {
            string target=Path.GetFullPath(args[0]), output=Path.GetFullPath(args[1]);
            int scans=int.Parse(args[2]); if(scans<1)throw new ArgumentException("Scan count must be positive");
            Directory.CreateDirectory(output);
            var assembly=Assembly.LoadFrom(target);
            var platformType=assembly.GetType("UpdateSwitch.WindowsPlatform",true);
            object platform=Activator.CreateInstance(platformType);
            var read=platformType.GetMethod("Read");
            var busy=platformType.GetMethod("BusyReason");
            var started=DateTime.UtcNow;
            object first=read.Invoke(platform,null);
            string firstJson=Json.Serialize(first);
            File.WriteAllText(Path.Combine(output,"scan-before.json"),firstJson,new UTF8Encoding(false));
            for(int i=0;i<20;i++) { read.Invoke(platform,null);busy.Invoke(platform,null); }
            Collect();
            using(var csv=new StreamWriter(Path.Combine(output,"memory.csv"),false,new UTF8Encoding(false))) {
                csv.WriteLine("utc,iteration,phase,elapsed_ms,private_bytes,managed_bytes,working_bytes,handles,threads,gen2_collections");
                Sample(csv,0,"baseline");
                for(int i=1;i<=scans;i++) {
                    read.Invoke(platform,null);
                    if(i%2==0)busy.Invoke(platform,null);
                    if(i%200==0 || i==scans)Sample(csv,i,"natural");
                    if(i%5000==0 || i==scans) { Collect();Sample(csv,i,"collected"); }
                }
            }
            object last=read.Invoke(platform,null);
            string lastJson=Json.Serialize(last);
            File.WriteAllText(Path.Combine(output,"scan-after.json"),lastJson,new UTF8Encoding(false));
            string summary=Json.Serialize(new { Status="completed",Target=target,Identity=WindowsIdentity.GetCurrent().Name,
                ProcessId=Process.GetCurrentProcess().Id,StartedUtc=started.ToString("o"),FinishedUtc=DateTime.UtcNow.ToString("o"),
                Scans=scans,BusyQueries=scans/2,ScanUnchanged=firstJson==lastJson,
                ReadOnly=true,ElapsedMilliseconds=Clock.ElapsedMilliseconds });
            File.WriteAllText(Path.Combine(output,"summary.json"),summary,new UTF8Encoding(false));
            Console.WriteLine(summary);
            return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex.ToString());return 1; }
    }
}
