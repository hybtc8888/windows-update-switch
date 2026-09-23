using System;
using System.IO;
using System.Linq;

namespace UpdateSwitch {
    static class StatusTests {
        static StatusSnapshot Sample() {
            var snapshot=new StatusSnapshot { State=new State { Mode="Protect",CheckedUtc=DateTime.UtcNow.ToString("o") },Guard=true };
            var items=new[] {
                new Item { Kind="service",Name="wuauserv",Value=4 },
                new Item { Kind="task",Name=@"\Microsoft\Windows\WindowsUpdate\Scheduled Start",Value=0 },
                new Item { Kind="policy",Name=WindowsPlatform.Policies[0],Value=1 },
                new Item { Kind="lock",Name="wuauserv",Value=1 }
            };
            snapshot.State.Current=items.Select(x=>x.Copy()).ToList();
            snapshot.State.Backup=items.Select(x=>x.Copy()).ToList();
            foreach(var item in items.Where(x=>x.Kind=="service" || x.Kind=="policy")) snapshot.Live[item.Id]=item;
            snapshot.At=DateTime.UtcNow;
            return snapshot;
        }
        static void Check(bool condition,string name) { if(!condition) throw new Exception(name); Console.WriteLine("PASS "+name); }
        static int Main(string[] args) {
            try {
                string path=Path.Combine(args[0],"engine.txt"); Tests.Run(path); Console.Write(File.ReadAllText(path));
                var s=Sample(); var d=Dashboard.Build(s);
                Check(d.Healthy && d.Passed==4,"新鲜核验且所有项目达标才显示保护中");
                Check(d.Features.Count==3 && d.Features.Sum(x=>int.Parse(x.Evidence.Split(' ')[0]))==d.Rows.Count,"合并为三组且每个受管对象只统计一次");
                s.Guard=false; d=Dashboard.Build(s);
                Check(!d.Healthy && !d.Rows.Single(x=>x.Kind=="task").Verified,"守护停止后不把缓存任务标为已核验");
                s=Sample(); s.At=s.At.AddSeconds(61); d=Dashboard.Build(s);
                Check(!d.Healthy && d.Rows.Single(x=>x.Kind=="task").Status=="待核验","过期快照明确显示待核验");
                Check(d.Features[0].Status=="待核验","任一成员未核验时整组不能显示已禁用");
                s=Sample(); s.At=s.At.AddMinutes(-1);
                Check(!Dashboard.Build(s).Healthy,"未来时间戳不能通过新鲜度判断");
                s=Sample(); s.Live["service:wuauserv"].Running=true; d=Dashboard.Build(s);
                Check(!d.Healthy && d.Rows.Single(x=>x.Kind=="service").Status.Contains("运行中"),"实时服务状态优先于缓存");
                s=Sample(); s.Live.Remove("service:wuauserv"); d=Dashboard.Build(s);
                Check(!d.Healthy && d.Rows.Single(x=>x.Kind=="service").Status=="无法读取","原受管服务消失时保留异常项");
                s=Sample(); s.State.Current.RemoveAll(x=>x.Kind=="task"); d=Dashboard.Build(s);
                Check(!d.Healthy && d.Rows.Count==4,"消失的计划任务不能静默退出清单");
                s=Sample(); s.State.Errors.Add("存在待重启更新"); d=Dashboard.Build(s);
                Check(!d.Healthy && d.ErrorText.Contains("待重启") && !d.Rows.Single(x=>x.Kind=="task").Verified,"安装或待重启错误使缓存核验失效");
                s=Sample(); s.State.Mode="Open"; d=Dashboard.Build(s);
                Check(!d.Healthy && d.Features.All(x=>x.Status=="未保护"),"恢复模式不把仍禁用的原始配置误报为保护中");
                s=Sample(); s.ReadErrors.Add("读取失败");
                Check(!Dashboard.Build(s).Healthy,"实时读取失败不显示全部通过");
                Console.WriteLine("23 checks passed; no system configuration was changed."); return 0;
            } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
