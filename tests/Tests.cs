using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UpdateSwitch {
    static class Tests {
        sealed class Fake : IPlatform {
            public List<Item> Items=new List<Item>();
            public string FailId, Busy;
            public int Writes;
            public Action BeforeWrite;
            public Scan Read() { return new Scan { Items=Items.Select(x=>x.Copy()).ToList() }; }
            public string BusyReason() { return Busy; }
            public void Write(Item item,int? value,bool restore) {
                if(BeforeWrite!=null) BeforeWrite();
                if(item.Id==FailId) throw new UnauthorizedAccessException("模拟系统拒绝写入");
                Item current=Items.First(x=>x.Id==item.Id); current.Value=value;
                if(item.Kind=="service" && !restore) current.Running=false; Writes++;
            }
        }
        static void Check(bool ok,string name,List<string> passes) { if(!ok) throw new Exception(name); passes.Add("PASS "+name); }
        public static void Run(string output) {
            var passes=new List<string>();
            var f=new Fake();
            f.Items.Add(new Item { Kind="service",Name="wuauserv",Value=3,Running=true });
            f.Items.Add(new Item { Kind="task",Name=@"\Microsoft\Windows\InstallService\WakeUpAndScanForUpdates",Value=0 });
            f.Items.Add(new Item { Kind="policy",Name=WindowsPlatform.Policies[0],Value=1 });
            f.Items.Add(new Item { Kind="policy",Name=WindowsPlatform.Policies[1],Value=null });
            State s=new State { Mode="Protect" }; bool saved=false;
            Action<State> save=delegate(State a){ saved=a.Backup.Count==f.Items.Count; };
            f.BeforeWrite=delegate { if(!saved) throw new Exception("原始值未持久化便写入"); };
            Engine.Enforce(f,s,save);
            Check(s.Errors.Count==0 && f.Items[0].Value==4 && !f.Items[0].Running,"原始值先保存，禁用后核验停止状态",passes);
            int writes=f.Writes; Engine.Enforce(f,s,save);
            Check(f.Writes==writes,"状态正确时不重复改写系统",passes);
            f.Items[0].Value=3; f.Items[0].Running=true; Engine.Enforce(f,s,save);
            Check(f.Items[0].Value==4 && !f.Items[0].Running && s.Backup[0].Value==3,"被重新启用后拦回，保留原始启动类型",passes);
            f.Items.Add(new Item { Kind="service",Name="UsoSvc",Value=2,Running=true }); saved=false;
            Engine.Enforce(f,s,save);
            Check(s.Backup.Last().Value==2 && f.Items.Last().Value==4,"新出现的管理对象先备份再禁用",passes);
            f.Items[0].Value=3; f.FailId=f.Items[0].Id; Engine.Enforce(f,s,save);
            Check(s.Errors.Count>0 && s.Summary.Contains("部分"),"权限拒绝不会假报保护成功",passes);
            f.FailId=null; f.Busy="正在安装"; writes=f.Writes; Engine.Enforce(f,s,save);
            Check(f.Writes==writes && s.Errors.Count>0,"安装中不强停并明确报告保护缺口",passes);
            f.Busy=null; Engine.Enforce(f,s,save);
            f.FailId=f.Items[0].Id; Engine.Restore(f,s,save);
            Check(s.Mode=="RestoreFailed" && s.Backup.Count==5,"恢复失败保留备份以便重试",passes);
            f.FailId=null; Engine.Restore(f,s,save);
            Check(s.Mode=="Open" && f.Items[0].Value==3 && f.Items.Last().Value==2,"重试恢复原服务启动类型",passes);
            Check(f.Items[1].Value==0 && f.Items[2].Value==1 && f.Items[3].Value==null,"保留原禁用任务和原策略，移除工具新增值",passes);
            writes=f.Writes; Engine.Enforce(f,s,save);
            Check(f.Writes==writes,"开启更新后守护不再重新阻断",passes);
            Check(!WindowsPlatform.IsAllowed(new Item { Kind="service",Name="WinDefend",Value=2 }) && !WindowsPlatform.IsAllowed(new Item { Kind="task",Name=@"\Unrelated\Example",Value=1 }),"拒绝管理无关服务和任务",passes);
            File.WriteAllText(output,string.Join(Environment.NewLine,passes)+Environment.NewLine,Encoding.UTF8);
        }
    }
}
