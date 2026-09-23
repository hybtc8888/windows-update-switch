using System;
using System.Collections.Generic;
using System.Linq;

namespace UpdateSwitch {
    public sealed class Item {
        public string Kind, Name;
        public int? Value;
        public string Text;
        public bool Running;
        public string Id { get { return Kind + ":" + Name; } }
        public Item Copy() { return new Item { Kind=Kind, Name=Name, Value=Value, Text=Text, Running=Running }; }
    }
    public sealed class Scan {
        public List<Item> Items = new List<Item>();
        public List<string> Errors = new List<string>();
    }
    public sealed class State {
        public int Version = 1;
        public string Mode = "Open";
        public string StartedUtc = "";
        public string CheckedUtc = "";
        public List<Item> Backup = new List<Item>();
        public List<Item> Current = new List<Item>();
        public List<string> Errors = new List<string>();
        public string Summary = "尚未启用保护";
    }
    public interface IPlatform {
        Scan Read();
        void Write(Item item, int? value, bool restore);
        string BusyReason();
    }
    public static class Engine {
        public static int Target(Item item) {
            if (item.Kind == "service") return 4;
            if (item.Kind == "task") return 0;
            if (item.Kind == "lock" || item.Kind=="serviceLock") return 1;
            return item.Name.EndsWith("|AutoDownload", StringComparison.Ordinal) ? 2 : 1;
        }
        public static bool Matches(Item item, int? value, bool restoring) {
            return item.Value == value && (restoring || item.Kind != "service" || !item.Running);
        }
        public static void Enforce(IPlatform platform, State state, Action<State> save) {
            if (state.Mode != "Protect") return;
            string busy = platform.BusyReason();
            if (busy != null) {
                state.Errors = new List<string> { busy };
                state.Summary = "保护存在缺口：" + busy;
                state.CheckedUtc = DateTime.UtcNow.ToString("o");
                save(state); return;
            }
            Scan scan = platform.Read();
            var errors = new List<string>(scan.Errors);
            var writeErrors = new Dictionary<string,string>();
            foreach (Item item in scan.Items) {
                if (!state.Backup.Any(x => x.Id == item.Id)) state.Backup.Add(item.Copy());
            }
            // Persist every original value BEFORE the first change, including newly discovered tasks.
            save(state);
            foreach (Item item in scan.Items) {
                try { if (!Matches(item, Target(item), false)) platform.Write(item, Target(item), false); }
                catch (Exception ex) { writeErrors[item.Id]=ex.Message; }
            }
            Scan after = platform.Read();
            state.Current=after.Items;
            errors.AddRange(after.Errors);
            foreach(Item old in state.Backup) if(!after.Items.Any(x=>x.Id==old.Id)) errors.Add(old.Id+" 管理对象已消失，无法确认保护");
            foreach (Item item in scan.Items) {
                Item now = after.Items.FirstOrDefault(x => x.Id == item.Id);
                if (now == null || !Matches(now, Target(item), false)) errors.Add(item.Id + " 未达到禁用状态"+(writeErrors.ContainsKey(item.Id) ? "："+writeErrors[item.Id] : ""));
            }
            state.Errors = errors.Distinct().ToList();
            state.CheckedUtc = DateTime.UtcNow.ToString("o");
            state.Summary = errors.Count == 0 ? "已禁用更新，守护中" : "部分未禁用，请查看详情";
            save(state);
        }
        public static void Restore(IPlatform platform, State state, Action<State> save) {
            // Desired mode changes first, so an old protection pass cannot re-enable blocking.
            state.Mode = "Restoring"; save(state);
            var errors = new List<string>();
            var writeErrors = new Dictionary<string,string>();
            Scan before = platform.Read();
            // Unlock all registry keys before restoring startup types, including locks added in a later version.
            var ordered = state.Backup.Where(i=>i.Kind=="serviceLock").Concat(state.Backup.Where(i=>i.Kind=="lock")).Concat(state.Backup.AsEnumerable().Reverse().Where(i=>i.Kind!="lock" && i.Kind!="serviceLock"));
            foreach (Item item in ordered) {
                try {
                    Item current=before.Items.FirstOrDefault(x=>x.Id==item.Id);
                    if(current==null || !Matches(current,item.Value,true) || ((item.Kind=="lock" || item.Kind=="serviceLock") && current.Text!=item.Text)) platform.Write(item, item.Value, true);
                }
                catch (Exception ex) { writeErrors[item.Id]=ex.Message; }
            }
            Scan after = platform.Read();
            state.Current=after.Items;
            errors.AddRange(after.Errors);
            foreach (Item item in state.Backup) {
                Item now = after.Items.FirstOrDefault(x => x.Id == item.Id);
                if (now == null || !Matches(now, item.Value, true) || ((item.Kind=="lock" || item.Kind=="serviceLock") && now.Text!=item.Text)) errors.Add(item.Id + " 未恢复原设置"+(writeErrors.ContainsKey(item.Id) ? "："+writeErrors[item.Id] : ""));
            }
            state.Errors = errors.Distinct().ToList();
            state.Mode = errors.Count == 0 ? "Open" : "RestoreFailed";
            state.CheckedUtc = DateTime.UtcNow.ToString("o");
            state.Summary = errors.Count == 0 ? "已恢复更新配置" : "恢复未完成，可再次点击开启更新";
            save(state);
        }
    }
}
