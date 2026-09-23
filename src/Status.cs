using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;

namespace UpdateSwitch {
    // Presentation reads service/policy state directly. Protected tasks and ACLs come
    // from the SYSTEM worker's last completed verification, never from guessed values.
    public sealed class StatusSnapshot {
        public State State;
        public bool Guard;
        public DateTime At = DateTime.UtcNow;
        public readonly Dictionary<string, Item> Live = new Dictionary<string, Item>();
        public readonly List<string> ReadErrors = new List<string>();
        public readonly List<string> PresentServices = new List<string>();
        public static StatusSnapshot Read() {
            var result = new StatusSnapshot { State = Storage.Load(), Guard = Storage.GuardRunning() };
            ServiceController[] services = ServiceController.GetServices();
            try {
                foreach (var sc in services.Where(s => WindowsPlatform.Services.Contains(s.ServiceName))) {
                    result.PresentServices.Add(sc.ServiceName);
                    try {
                        var item = new Item { Kind="service", Name=sc.ServiceName, Value=(int)sc.StartType, Running=sc.Status!=ServiceControllerStatus.Stopped };
                        result.Live[item.Id] = item;
                    } catch (Exception ex) { result.ReadErrors.Add(sc.ServiceName+"："+ex.Message); }
                }
            } finally { foreach (var sc in services) sc.Dispose(); }
            foreach (string policy in WindowsPlatform.Policies) {
                try {
                    string[] pair=policy.Split('|');
                    var item=new Item { Kind="policy", Name=policy, Value=WindowsPlatform.ReadNumber(pair[0],pair[1]) };
                    result.Live[item.Id]=item;
                } catch (Exception ex) { result.ReadErrors.Add(policy+"："+ex.Message); }
            }
            return result;
        }
    }
    public sealed class StatusRow {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        public string Status { get; set; }
        public string Evidence { get; set; }
        public string Color { get; set; }
        public string Background { get; set; }
        public bool Verified;
        public bool Matches;
        public Item Item;
    }
    public sealed class Dashboard {
        public const string Green="#3D8B5B", Blue="#355D8F", Amber="#BF5B38", Gray="#777064";
        public List<StatusRow> Rows = new List<StatusRow>();
        public List<StatusRow> Features = new List<StatusRow>();
        public string Heading, Description, Badge, Color, CheckText, GuardText, ErrorText;
        public bool Healthy, CanRestore, Protect, Fresh;
        public int Passed;
        public static string ServiceTitle(string name) {
            switch(name) {
                case "wuauserv": return "Windows 系统更新";
                case "UsoSvc": return "更新协调服务";
                case "WaaSMedicSvc": return "更新修复服务";
                case "InstallService": return "Microsoft Store 安装服务";
                case "uhssvc": return "Microsoft 更新健康服务";
                default: return name;
            }
        }
        static string TaskTitle(string name) {
            switch(name) {
                case "Schedule Scan": return "定时扫描更新";
                case "Schedule Scan Static Task": return "更新扫描触发器";
                case "Schedule Wake To Work": return "唤醒后执行更新";
                case "Schedule Maintenance Work": return "更新维护任务";
                case "Schedule Work": return "更新计划执行";
                case "Scheduled Start": return "定时启动更新服务";
                case "PerformRemediation": return "修复更新组件";
                case "ScanForUpdates": return "扫描商店应用更新";
                case "ScanForUpdatesAsUser": return "用户应用更新扫描";
                case "WakeUpAndContinueUpdates": return "唤醒并继续应用更新";
                case "WakeUpAndScanForUpdates": return "唤醒并扫描应用更新";
                case "SmartRetry": return "商店更新重试";
                case "RestoreDevice": return "商店安装恢复任务";
                case "Report policies": return "更新策略报告";
                case "Refresh Group Policy Cache": return "刷新更新策略缓存";
                default: return name;
            }
        }
        static StatusRow Describe(Item item) {
            var row=new StatusRow { Id=item.Id, Kind=item.Kind, Item=item, Detail=item.Name };
            if(item.Kind=="service") row.Title=ServiceTitle(item.Name);
            else if(item.Kind=="task") row.Title=TaskTitle(item.Name.Substring(item.Name.LastIndexOf('\\')+1));
            else if(item.Kind=="policy") {
                row.Title=item.Name.EndsWith("|AutoDownload") ? "商店应用自动更新策略" : "Windows 自动更新策略";
                row.Detail=item.Name.Substring(item.Name.LastIndexOf('|')+1);
            } else {
                row.Title=ServiceTitle(item.Name)+(item.Kind=="lock" ? " · 启动设置锁定" : " · 服务权限锁定");
                row.Detail=item.Name+(item.Kind=="lock" ? " / 注册表写入限制" : " / 启动与配置权限限制");
            }
            return row;
        }
        public static Dashboard Build(StatusSnapshot snapshot) {
            State s=snapshot.State;
            var d=new Dashboard { Protect=s.Mode=="Protect", CanRestore=s.Backup.Count>0 };
            DateTime checkedAt;
            bool dated=DateTime.TryParse(s.CheckedUtc,null,System.Globalization.DateTimeStyles.RoundtripKind,out checkedAt);
            TimeSpan age=dated ? snapshot.At-checkedAt.ToUniversalTime() : TimeSpan.MaxValue;
            d.Fresh=dated && age>=TimeSpan.Zero && age<TimeSpan.FromSeconds(60);
            bool cachedValid=d.Protect && d.Fresh && snapshot.Guard && s.Errors.Count==0;
            var cached=(s.Current ?? new List<Item>()).ToDictionary(x=>x.Id,x=>x);
            var all=new Dictionary<string,Item>();
            foreach(var item in s.Backup.Concat(cached.Values).Concat(snapshot.Live.Values)) all[item.Id]=item;
            foreach(var name in snapshot.PresentServices) {
                string id="service:"+name;
                if(!all.ContainsKey(id)) all[id]=new Item { Kind="service",Name=name };
            }
            foreach(var spec in all.Values.OrderBy(x=>x.Kind=="service"?0:x.Kind=="task"?1:x.Kind=="policy"?2:3).ThenBy(x=>x.Name,StringComparer.Ordinal)) {
                Item item;
                bool direct=spec.Kind=="service" || spec.Kind=="policy";
                bool found=(direct ? snapshot.Live : cached).TryGetValue(spec.Id,out item);
                var row=Describe(spec);
                row.Item=item ?? spec;
                row.Verified=found && (direct || cachedValid);
                row.Matches=found && Engine.Matches(item,Engine.Target(item),false);
                row.Evidence=direct ? (found ? "实时读取" : "读取失败") : (cachedValid ? "守护已核验" : "等待守护核验");
                if(!row.Verified) row.Status=found ? "待核验" : "无法读取";
                else if(spec.Kind=="service") row.Status=(item.Value==4 ? "已禁用" : item.Value==3 ? "手动启动" : "自动启动")+" · "+(item.Running ? "运行中" : "已停止");
                else if(spec.Kind=="task") row.Status=item.Value==0 ? "已禁用" : "已启用";
                else if(spec.Kind=="policy") row.Status=row.Matches ? "已限制" : item.Value.HasValue ? "未限制" : "未配置";
                else row.Status=item.Value==1 ? "已锁定" : "未锁定";
                row.Color=row.Verified ? (row.Matches ? Green : d.Protect ? Amber : Gray) : Amber;
                row.Background=row.Color==Green ? "#EAF5EE" : row.Color==Amber ? "#FFF4E3" : "#F0F0F3";
                d.Rows.Add(row);
            }
            d.Passed=d.Rows.Count(x=>x.Verified && x.Matches);
            d.Healthy=d.Protect && snapshot.Guard && d.Fresh && s.Errors.Count==0 && snapshot.ReadErrors.Count==0
                && s.Backup.Count>0 && d.Rows.Count>0 && d.Passed==d.Rows.Count;
            d.Color=d.Healthy ? Green : d.Protect || s.Mode=="Restoring" || s.Mode=="RestoreFailed" ? Amber : Blue;
            if(d.Healthy) { d.Heading="更新已禁用"; d.Badge="保护中"; d.Description="后台守护持续检查，关闭窗口后保护仍然有效。"; }
            else if(d.Protect) { d.Heading="保护需要关注"; d.Badge="待核验"; d.Description=!snapshot.Guard ? "后台守护未运行。点击“禁用更新”重新启动保护。" : !d.Fresh ? "正在等待新的核验结果，暂不能确认持续保护。" : "部分项目未通过核验。请在监测清单查看具体状态。"; }
            else if(s.Mode=="Restoring" || s.Mode=="RestoreFailed") { d.Heading=s.Mode=="Restoring"?"正在恢复更新配置":"更新配置恢复未完成"; d.Badge="恢复中"; d.Description="原始设置仍保留。恢复失败时可点击“开启更新”重试。"; }
            else { d.Heading=d.CanRestore ? "更新保护已关闭" : "更新由你决定"; d.Badge="未保护"; d.Description="点击“禁用更新”开启保护；需要维护时再开启更新。"; }
            d.CheckText=dated ? "最近核验  "+checkedAt.ToLocalTime().ToString("HH:mm:ss")+(d.Fresh ? "" : " · 已过期") : "尚无核验记录";
            d.GuardText=snapshot.Guard ? "守护运行中 · 随系统启动" : "守护未运行";
            d.ErrorText=string.Join(Environment.NewLine,s.Errors.Concat(snapshot.ReadErrors).Distinct());
            d.AddFeature("Windows 系统更新",false,x=>x.Kind=="service" && x.Item.Name!="InstallService" || x.Kind=="task" && !x.Item.Name.StartsWith(@"\Microsoft\Windows\InstallService\",StringComparison.OrdinalIgnoreCase) || x.Kind=="policy" && x.Item.Name.EndsWith("|NoAutoUpdate"));
            d.AddFeature("应用商店更新",false,x=>x.Kind=="service" && x.Item.Name=="InstallService" || x.Kind=="task" && x.Item.Name.StartsWith(@"\Microsoft\Windows\InstallService\",StringComparison.OrdinalIgnoreCase) || x.Kind=="policy" && x.Item.Name.EndsWith("|AutoDownload"));
            d.AddFeature("防回改保护",true,x=>x.Kind=="lock" || x.Kind=="serviceLock");
            return d;
        }
        void AddFeature(string title,bool locks,Func<StatusRow,bool> predicate) {
            var items=Rows.Where(predicate).ToList();
            bool ok=Protect && items.Count>0 && items.All(x=>x.Verified && x.Matches);
            Features.Add(new StatusRow { Title=title, Detail=string.Join(Environment.NewLine,items.Select(x=>x.Detail+"："+x.Status)), Evidence=items.Count+" 项", Status=ok ? locks ? "已锁定" : "已禁用" : Protect ? "待核验" : "未保护", Color=ok?Green:Protect?Amber:Gray });
        }
    }
}
