using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using TimeoutException = System.TimeoutException;

namespace UpdateSwitch {
    public sealed class WindowsPlatform : IPlatform {
        public static readonly string[] Services = { "UsoSvc", "InstallService", "WaaSMedicSvc", "uhssvc", "wuauserv" };
        public static readonly string[] Folders = { @"\Microsoft\Windows\UpdateOrchestrator", @"\Microsoft\Windows\WindowsUpdate", @"\Microsoft\Windows\WaaSMedic", @"\Microsoft\Windows\InstallService" };
        public static readonly string[] Policies = {
            @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU|NoAutoUpdate",
            @"SOFTWARE\Policies\Microsoft\WindowsStore|AutoDownload"
        };
        public static int? ReadNumber(string path, string name) {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path)) {
                if (key == null || key.GetValue(name) == null) return null;
                if (key.GetValueKind(name) != RegistryValueKind.DWord) throw new InvalidOperationException("注册表值不是 DWORD：" + name);
                return Convert.ToInt32(key.GetValue(name));
            }
        }
        public static bool IsAllowed(Item item) {
            if (item.Kind == "service") return Services.Contains(item.Name) && item.Value >= 2 && item.Value <= 4;
            if (item.Kind == "lock" || item.Kind=="serviceLock") return Services.Contains(item.Name) && (item.Value==0 || item.Value==1) && !string.IsNullOrEmpty(item.Text);
            if (item.Kind == "policy") return Policies.Contains(item.Name);
            if (item.Kind == "task") return Folders.Any(f => item.Name.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase) && item.Name.Substring(f.Length + 1).IndexOf('\\') < 0) && (item.Value == 0 || item.Value == 1);
            return false;
        }
        public Scan Read() {
            var result = new Scan();
            foreach (string name in Services) {
                try {
                    int? start = ReadNumber(@"SYSTEM\CurrentControlSet\Services\" + name, "Start");
                    if (start == null) continue;
                    using (var sc = new ServiceController(name)) result.Items.Add(new Item { Kind="service", Name=name, Value=(int)sc.StartType, Running=sc.Status != ServiceControllerStatus.Stopped });
                    if(name!="WaaSMedicSvc") {
                        string security=Native.ReadSecurity(name);
                        result.Items.Add(new Item { Kind="serviceLock",Name=name,Value=Native.SecurityLocked(security)?1:0,Text=security });
                    }
                    using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+name)) {
                        var acl=key.GetAccessControl(AccessControlSections.Access);
                        result.Items.Add(new Item { Kind="lock",Name=name,Value=IsLocked(acl)?1:0,Text=acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access) });
                    }
                } catch (Exception ex) { result.Errors.Add("读取服务 " + name + "：" + ex.Message); }
            }
            foreach (string policy in Policies) {
                try { string[] pair = policy.Split('|'); result.Items.Add(new Item { Kind="policy", Name=policy, Value=ReadNumber(pair[0], pair[1]) }); }
                catch (Exception ex) { result.Errors.Add("读取策略：" + ex.Message); }
            }
            dynamic scheduler = null;
            try {
                scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)); scheduler.Connect();
                foreach (string path in Folders) {
                    dynamic folder = null, tasks = null;
                    try {
                        folder = scheduler.GetFolder(path); tasks = folder.GetTasks(1);
                        for (int i=1; i<=tasks.Count; i++) {
                            dynamic task = tasks[i];
                            try { result.Items.Add(new Item { Kind="task", Name=(string)task.Path, Value=(bool)task.Enabled ? 1 : 0 }); }
                            finally { Marshal.FinalReleaseComObject(task); }
                        }
                    } catch (Exception ex) {
                        if (ex.HResult != unchecked((int)0x80070002) && ex.HResult != unchecked((int)0x80070003)) result.Errors.Add("读取任务目录 " + path + "：" + ex.Message);
                    } finally { if (tasks != null) Marshal.FinalReleaseComObject(tasks); if (folder != null) Marshal.FinalReleaseComObject(folder); }
                }
            } catch (Exception ex) { result.Errors.Add("读取计划任务：" + ex.Message); }
            finally { if (scheduler != null) Marshal.FinalReleaseComObject(scheduler); }
            return result;
        }
        public void Write(Item item, int? value, bool restore) {
            if (!IsAllowed(item)) throw new InvalidOperationException("不在工具管理范围内");
            if (item.Kind == "policy") {
                string[] pair = item.Name.Split('|');
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(pair[0])) {
                    if (value.HasValue) key.SetValue(pair[1], value.Value, RegistryValueKind.DWord);
                    else key.DeleteValue(pair[1], false);
                }
            } else if(item.Kind=="serviceLock") {
                Native.WriteSecurity(item.Name,restore ? item.Text : Native.ChangeLock(Native.ReadSecurity(item.Name),true));
            } else if (item.Kind == "lock") {
                using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+item.Name,RegistryKeyPermissionCheck.ReadWriteSubTree,RegistryRights.ReadPermissions|RegistryRights.ChangePermissions)) {
                    RegistrySecurity acl;
                    if(restore) { acl=new RegistrySecurity(); acl.SetSecurityDescriptorSddlForm(item.Text,AccessControlSections.Access); }
                    else { acl=key.GetAccessControl(AccessControlSections.Access); if(!IsLocked(acl)) acl.AddAccessRule(LockRule()); }
                    key.SetAccessControl(acl);
                }
            } else if (item.Kind == "service") {
                SetStartup(item.Name,value.Value);
                if (!restore) using (var sc = new ServiceController(item.Name)) {
                    sc.Refresh();
                    if (sc.Status == ServiceControllerStatus.Stopped) return;
                    if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
                }
            } else {
                dynamic scheduler = null, root = null, task = null;
                try {
                    scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)); scheduler.Connect();
                    root = scheduler.GetFolder("\\"); task = root.GetTask(item.Name); task.Enabled = value == 1;
                } finally {
                    if (task != null) Marshal.FinalReleaseComObject(task);
                    if (root != null) Marshal.FinalReleaseComObject(root);
                    if (scheduler != null) Marshal.FinalReleaseComObject(scheduler);
                }
            }
        }
        static RegistryAccessRule LockRule() { return new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid,null),RegistryRights.SetValue,InheritanceFlags.None,PropagationFlags.None,AccessControlType.Deny); }
        static bool IsLocked(RegistrySecurity acl) {
            return acl.GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<RegistryAccessRule>().Any(a=>a.IdentityReference.Value=="S-1-1-0" && a.AccessControlType==AccessControlType.Deny && (a.RegistryRights & RegistryRights.SetValue)!=0 && a.InheritanceFlags==InheritanceFlags.None);
        }
        static void SetStartup(string name,int start) {
            string path=@"SYSTEM\CurrentControlSet\Services\"+name;
            using(var key=Registry.LocalMachine.OpenSubKey(path,RegistryKeyPermissionCheck.ReadWriteSubTree,RegistryRights.ReadPermissions|RegistryRights.ChangePermissions)) {
                var original=key.GetAccessControl(AccessControlSections.Access); bool locked=IsLocked(original);
                string serviceSecurity=name=="WaaSMedicSvc" ? null : Native.ReadSecurity(name);
                bool serviceLocked=serviceSecurity!=null && Native.SecurityLocked(serviceSecurity);
                if(locked) { var unlocked=key.GetAccessControl(AccessControlSections.Access); unlocked.RemoveAccessRuleSpecific(LockRule()); key.SetAccessControl(unlocked); }
                try {
                    if(serviceLocked) Native.WriteSecurity(name,Native.ChangeLock(serviceSecurity,false));
                    try { Native.Configure(name,(uint)start); }
                    catch(Win32Exception ex) {
                        if(ex.NativeErrorCode!=5 || name!="WaaSMedicSvc") throw;
                        // Windows Light protected Medic rejects ChangeServiceConfig. Its startup registry is writable.
                        using(var settings=Registry.LocalMachine.OpenSubKey(path,true)) settings.SetValue("Start",start,RegistryValueKind.DWord);
                    }
                    using(var sc=new ServiceController(name)) if((int)sc.StartType!=start) throw new InvalidOperationException("启动配置尚未生效；不能报告已禁用。");
                } finally {
                    try { if(serviceLocked) Native.WriteSecurity(name,serviceSecurity); }
                    finally { if(locked) key.SetAccessControl(original); }
                }
            }
        }
        public string BusyReason() {
            foreach (string path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending" })
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path)) if (key != null) return "系统已有待重启更新；禁用服务无法取消它，请先安排完成此次更新。";
            // Query only: no search, download, installation or reboot is requested.
            object installer = null;
            try {
                installer = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Installer", true));
                if ((bool)((dynamic)installer).IsBusy) return "Windows 正在安装更新，暂不强行停止。";
            } catch (COMException ex) {
                if (ex.ErrorCode != unchecked((int)0x80070422) && ex.ErrorCode != unchecked((int)0x8024001E)) return "无法确定是否正在安装更新：" + ex.Message;
            } catch (Exception ex) { return "无法读取更新安装状态：" + ex.Message; }
            finally { if (installer != null) Marshal.FinalReleaseComObject(installer); }
            return null;
        }
    }

    public static class Native {
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr OpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint error, string binary, string group, IntPtr tag, string dependencies, string account, string password, string display);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool CloseServiceHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool QueryServiceObjectSecurity(IntPtr service,uint info,byte[] buffer,uint length,out uint needed);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool SetServiceObjectSecurity(IntPtr service,uint info,byte[] descriptor);
        const int DenyMask=0x10012; // SERVICE_CHANGE_CONFIG | SERVICE_START | DELETE
        public static bool SecurityLocked(string sddl) {
            var sd=new RawSecurityDescriptor(sddl);
            return sd.DiscretionaryAcl.Cast<GenericAce>().OfType<CommonAce>().Any(a=>a.AceQualifier==AceQualifier.AccessDenied && a.SecurityIdentifier.Value=="S-1-1-0" && (a.AccessMask & DenyMask)==DenyMask);
        }
        public static string ChangeLock(string sddl,bool add) {
            var sd=new RawSecurityDescriptor(sddl);
            for(int i=sd.DiscretionaryAcl.Count-1;i>=0;i--) {
                var a=sd.DiscretionaryAcl[i] as CommonAce;
                if(a!=null && a.AceQualifier==AceQualifier.AccessDenied && a.SecurityIdentifier.Value=="S-1-1-0" && a.AccessMask==DenyMask && a.AceFlags==AceFlags.None) sd.DiscretionaryAcl.RemoveAce(i);
            }
            if(add) sd.DiscretionaryAcl.InsertAce(0,new CommonAce(AceFlags.None,AceQualifier.AccessDenied,DenyMask,new SecurityIdentifier(WellKnownSidType.WorldSid,null),false,null));
            return sd.GetSddlForm(AccessControlSections.Access);
        }
        public static string ReadSecurity(string name) {
            IntPtr manager=OpenSCManager(null,null,1); if(manager==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr service=IntPtr.Zero;
            try {
                service=OpenService(manager,name,0x20000); if(service==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                uint needed; QueryServiceObjectSecurity(service,4,null,0,out needed);
                if(needed==0) throw new Win32Exception(Marshal.GetLastWin32Error());
                var data=new byte[needed]; if(!QueryServiceObjectSecurity(service,4,data,needed,out needed)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return new RawSecurityDescriptor(data,0).GetSddlForm(AccessControlSections.Access);
            } finally { if(service!=IntPtr.Zero) CloseServiceHandle(service); CloseServiceHandle(manager); }
        }
        public static void WriteSecurity(string name,string sddl) {
            IntPtr manager=OpenSCManager(null,null,1); if(manager==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr service=IntPtr.Zero;
            try {
                service=OpenService(manager,name,0x40000); if(service==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var sd=new RawSecurityDescriptor(sddl); var data=new byte[sd.BinaryLength]; sd.GetBinaryForm(data,0);
                if(!SetServiceObjectSecurity(service,4,data)) throw new Win32Exception(Marshal.GetLastWin32Error());
            } finally { if(service!=IntPtr.Zero) CloseServiceHandle(service); CloseServiceHandle(manager); }
        }
        public static void Configure(string name, uint start) {
            IntPtr manager = OpenSCManager(null, null, 1);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr service = IntPtr.Zero;
            try {
                service = OpenService(manager, name, 2);
                if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!ChangeServiceConfig(service, uint.MaxValue, start, uint.MaxValue, null, null, IntPtr.Zero, null, null, null, null)) throw new Win32Exception(Marshal.GetLastWin32Error());
            } finally { if (service != IntPtr.Zero) CloseServiceHandle(service); CloseServiceHandle(manager); }
        }
    }
    public static class Storage {
        public const string GuardName = "LocalWindowsUpdateGuard";
        public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LocalWindowsUpdateGuard");
        public static readonly string Program = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LocalWindowsUpdateGuard");
        public static readonly string InstalledExe = Path.Combine(Program, "UpdateSwitch.exe");
        public static readonly string StateFile = Path.Combine(Data, "state.json");
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        public static bool Admin { get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } }
        public static void SecureDirectory(string path) {
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("目录是链接，无法安全安装：" + path);
            Directory.CreateDirectory(path);
            foreach (string child in Directory.GetFileSystemEntries(path)) if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new IOException("目录内含链接，无法安全安装：" + child);
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            acl.SetOwner(admins);
            foreach (var sid in new[] { admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(path, acl);
            foreach (string file in Directory.GetFiles(path)) {
                var fa = new FileSecurity(); fa.SetAccessRuleProtection(false, false); fa.SetOwner(admins); File.SetAccessControl(file, fa);
            }
        }
        public static State Load() {
            if (!File.Exists(StateFile)) return new State();
            State state = Json.Deserialize<State>(File.ReadAllText(StateFile, Encoding.UTF8));
            if (state == null || state.Version != 1 || state.Backup == null || state.Errors == null || !new[] { "Open", "Protect", "Restoring", "RestoreFailed" }.Contains(state.Mode) || state.Backup.Any(i => !WindowsPlatform.IsAllowed(i))) throw new InvalidDataException("备份状态文件无效；未执行任何恢复操作。");
            return state;
        }
        public static void Save(State state) {
            string temp = StateFile + ".tmp";
            if (File.Exists(temp) && (File.GetAttributes(temp) & FileAttributes.ReparsePoint) != 0) throw new IOException("状态临时文件不安全");
            File.WriteAllText(temp, Json.Serialize(state), new UTF8Encoding(false));
            if (File.Exists(StateFile)) File.Replace(temp, StateFile, null); else File.Move(temp, StateFile);
        }
        public static void Log(string text) {
            string file = Path.Combine(Data, "events.log");
            if (File.Exists(file) && new FileInfo(file).Length > 1024*1024) {
                string old=file+".previous"; if (File.Exists(old)) File.Delete(old); File.Move(file,old);
            }
            File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine, Encoding.UTF8);
        }
        public static void Exclusive(string name, Action action) {
            using (var mutex = new Mutex(false, @"Global\" + name)) {
                bool taken=false;
                try {
                    try { taken=mutex.WaitOne(TimeSpan.FromSeconds(90)); } catch (AbandonedMutexException) { taken=true; }
                    if (!taken) throw new TimeoutException("另一个操作仍在执行，请稍后重试。");
                    action();
                } finally { if (taken) mutex.ReleaseMutex(); }
            }
        }
        public static bool GuardInstalled() { using (var k=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+GuardName)) return k!=null; }
        public static bool GuardRunning() {
            try { using(var sc=new ServiceController(GuardName)) return sc.Status==ServiceControllerStatus.Running && sc.StartType==ServiceStartMode.Automatic; } catch { return false; }
        }
        public static void VerifyGuardIdentity() {
            using (var k=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+GuardName)) {
                if(k!=null && !string.Equals(Convert.ToString(k.GetValue("ImagePath")), "\""+InstalledExe+"\" --service", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("发现同名的其他服务，已停止操作。");
            }
        }
        public static void RunSc(string args) {
            var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"sc.exe"),args) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
            using(var p=Process.Start(info)) {
                string output=p.StandardOutput.ReadToEnd(); string error=p.StandardError.ReadToEnd();
                p.WaitForExit(); if(p.ExitCode!=0) throw new InvalidOperationException(output+error);
            }
        }
        public static void InstallGuard() {
            SecureDirectory(Program); SecureDirectory(Data); VerifyGuardIdentity();
            UpdateInstalledBinary();
            if (!GuardInstalled()) RunSc("create "+GuardName+" binPath= \"\\\""+InstalledExe+"\\\" --service\" start= auto DisplayName= \"Windows 更新开关守护\"");
            Native.Configure(GuardName,2);
            RunSc("failure "+GuardName+" reset= 86400 actions= restart/5000/restart/10000/restart/30000");
            RunSc("description "+GuardName+" \"本地 Windows 更新开关：禁用模式每 10 秒核对服务、任务和策略。\"");
        }
        // Upgrade only this application's worker. Update services stay disabled and
        // the original restoration snapshot is never rewritten by the upgrade.
        static void UpdateInstalledBinary() {
            string source=System.Reflection.Assembly.GetExecutingAssembly().Location;
            byte[] next=File.ReadAllBytes(source);
            byte[] previous=File.Exists(InstalledExe) ? File.ReadAllBytes(InstalledExe) : null;
            if(previous!=null && previous.SequenceEqual(next)) return;
            bool running=false;
            if(GuardInstalled()) using(var sc=new ServiceController(GuardName)) {
                running=sc.Status!=ServiceControllerStatus.Stopped;
                if(running) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(80)); }
            }
            string temporary=InstalledExe+".new";
            try {
                File.WriteAllBytes(temporary,next);
                if(previous!=null) File.Replace(temporary,InstalledExe,null); else File.Move(temporary,InstalledExe);
                if(running) using(var sc=new ServiceController(GuardName)) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(15)); }
            } catch {
                if(previous!=null) {
                    using(var sc=new ServiceController(GuardName)) {
                        if(GuardInstalled() && sc.Status!=ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(80)); }
                        File.WriteAllBytes(InstalledExe,previous);
                        if(running) sc.Start();
                    }
                }
                throw;
            } finally { if(File.Exists(temporary)) File.Delete(temporary); }
        }
        public static void Upgrade() {
            Exclusive("LocalWindowsUpdateGuardAction",delegate {
                if(!GuardInstalled()) return;
                VerifyGuardIdentity(); SecureDirectory(Program); UpdateInstalledBinary();
                Log("已更新程序，原保护模式及恢复配置保持不变。");
            });
        }
        public static void StopGuard() {
            VerifyGuardIdentity(); if(!GuardInstalled()) return;
            Native.Configure(GuardName,3);
            using(var sc=new ServiceController(GuardName)) {
                if(sc.Status==ServiceControllerStatus.Stopped) return;
                if(sc.Status!=ServiceControllerStatus.StopPending) sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(80));
            }
        }
        public static void Disable() {
            Exclusive("LocalWindowsUpdateGuardAction",delegate {
                var platform=new WindowsPlatform(); string busy=platform.BusyReason(); if(busy!=null) throw new InvalidOperationException(busy);
                SecureDirectory(Data); VerifyGuardIdentity();
                Exclusive("LocalWindowsUpdateGuardState",delegate {
                    State state=Load();
                    if(state.Mode=="RestoreFailed" || state.Mode=="Restoring") throw new InvalidOperationException("上次恢复尚未完成，请先点击开启更新重试恢复。");
                    if(state.Mode!="Protect") state=new State { Mode="Protect",StartedUtc=DateTime.UtcNow.ToString("o") };
                    // Capture originals, without modifying updates in the UI process.
                    Scan scan=platform.Read();
                    foreach(Item item in scan.Items) if(!state.Backup.Any(x=>x.Id==item.Id)) state.Backup.Add(item.Copy());
                    state.Summary="正在启动守护，请等待实际核验"; state.Errors=scan.Errors; state.CheckedUtc=""; Save(state);
                });
                InstallGuard();
                using(var sc=new ServiceController(GuardName)) {
                    if(sc.Status!=ServiceControllerStatus.Running) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(15)); }
                }
                Log("用户选择禁用更新。等待守护核验。");
            });
        }
        public static void Enable() {
            Exclusive("LocalWindowsUpdateGuardAction",delegate {
                VerifyGuardIdentity();
                if(!File.Exists(StateFile)) throw new InvalidOperationException("没有本工具的原始配置备份，未猜测或改写系统配置。");
                SecureDirectory(Data);
                Exclusive("LocalWindowsUpdateGuardState",delegate {
                    State state=Load(); state.Mode="Restoring"; state.Errors.Clear(); state.Summary="正在恢复原始设置"; Save(state);
                });
                // Restoration uses the same SYSTEM identity that disabled protected tasks.
                if(!GuardInstalled()) InstallGuard();
                using(var sc=new ServiceController(GuardName)) if(sc.Status!=ServiceControllerStatus.Running) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(15)); }
                var until=DateTime.UtcNow.AddSeconds(85);
                while(DateTime.UtcNow<until) {
                    State state=Load();
                    if(state.Mode=="Open") { StopGuard(); Log("用户选择开启更新：已恢复原始设置，守护已停止。"); return; }
                    if(state.Mode=="RestoreFailed") throw new InvalidOperationException(state.Summary+Environment.NewLine+string.Join(Environment.NewLine,state.Errors));
                    Thread.Sleep(400);
                }
                throw new TimeoutException("恢复仍未完成，请查看守护日志。");
            });
        }
    }
    public sealed class GuardService : ServiceBase {
        readonly ManualResetEvent stop=new ManualResetEvent(false);
        Thread worker;
        public GuardService() { ServiceName=Storage.GuardName; CanStop=true; CanShutdown=true; AutoLog=true; }
        protected override void OnStart(string[] args) {
            worker=new Thread(Loop) { IsBackground=true,Name="Update protection" }; worker.Start();
        }
        void Loop() {
            string last="";
            while(!stop.WaitOne(0)) {
                try {
                    Storage.Exclusive("LocalWindowsUpdateGuardState",delegate {
                        if(stop.WaitOne(0)) return;
                        State state=Storage.Load();
                        if(state.Mode=="Protect") Engine.Enforce(new WindowsPlatform(),state,Storage.Save);
                        else if(state.Mode=="Restoring") Engine.Restore(new WindowsPlatform(),state,Storage.Save);
                        else return;
                        string report=state.Summary+" "+string.Join("；",state.Errors);
                        if(report!=last) { Storage.Log(report); last=report; }
                    });
                } catch(Exception ex) { try { Storage.Log("守护异常："+ex.Message); } catch { } }
                if(stop.WaitOne(10000)) break;
            }
        }
        protected override void OnStop() { stop.Set(); RequestAdditionalTime(85000); if(worker!=null && !worker.Join(80000)) throw new TimeoutException("守护仍在执行系统操作，暂未停止。"); }
        protected override void OnShutdown() { stop.Set(); }
    }
}
