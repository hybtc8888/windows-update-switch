using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Windows Update Switch")]
[assembly: System.Reflection.AssemblyDescription("Local Windows update control and monitoring")]
[assembly: System.Reflection.AssemblyVersion("1.4.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.4.0.0")]

namespace UpdateSwitch {
    static class App {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [STAThread] static int Main(string[] args) {
            try {
                if(args.Length>0 && args[0]=="--service") { ServiceBase.Run(new GuardService()); return 0; }
                if(args.Length==2 && args[0]=="--diagnose") {
                    File.WriteAllText(args[1],new JavaScriptSerializer().Serialize(new { At=DateTime.Now.ToString("o"),State=Storage.Load(),GuardRunning=Storage.GuardRunning(),Scan=new WindowsPlatform().Read() }),Encoding.UTF8); return 0;
                }
                if((args.Length==1 || (args.Length==2 && args[1]=="--quiet")) && (args[0]=="--disable" || args[0]=="--enable" || args[0]=="--upgrade")) {
                    if(!Storage.Admin) throw new InvalidOperationException("此操作需要管理员权限，请通过工具按钮运行。");
                    if(args[0]=="--disable") Storage.Disable(); else if(args[0]=="--enable") Storage.Enable(); else Storage.Upgrade();
                    return 0;
                }
                SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if(args.Length==2 && args[0]=="--preview-en") {
                    using(var form=new MainForm(true)) { form.SetLanguage(true,false); form.Opacity=0; form.ShowInTaskbar=false; form.Show(); Application.DoEvents(); form.RefreshStatus(); using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height)); bitmap.Save(args[1]); } }
                    return 0;
                }
                if(args.Length==2 && args[0]=="--preview") {
                    using(var form=new MainForm(true)) {
                        form.Opacity=0; form.ShowInTaskbar=false; form.Show(); Application.DoEvents(); form.RefreshStatus();
                        using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height)); bitmap.Save(args[1]); }
                    } return 0;
                }
                if(args.Length>0) throw new ArgumentException("未知参数。");
                Application.Run(new MainForm(false)); return 0;
            } catch(Exception ex) {
                if(args.Contains("--quiet") || args.Contains("--service")) { try { Storage.Log("操作失败："+ex.ToString()); } catch { } }
                else MessageBox.Show(ex.Message,"Windows 更新开关 · 未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return 1;
            }
        }
    }
    public sealed class MainForm : Form {
        readonly Label title=new Label(), status=new Label(), sub=new Label();
        readonly Label intro=new Label(), note=new Label(), footer=new Label();
        readonly LinkLabel settings=new LinkLabel(), logs=new LinkLabel(), language=new LinkLabel();
        readonly TextBox details=new TextBox();
        readonly Button disable=new Button(), enable=new Button();
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
        bool working;
        bool english;
        readonly Color ink=Color.FromArgb(31,43,61), blue=Color.FromArgb(35,83,160);
        public MainForm(bool preview) {
            Text="Windows 更新开关"; Icon=SystemIcons.Shield;
            ClientSize=new Size(700,510); MinimumSize=new Size(716,549); MaximumSize=new Size(716,549);
            FormBorderStyle=FormBorderStyle.FixedSingle; MaximizeBox=false; StartPosition=FormStartPosition.CenterScreen;
            Font=new Font("Microsoft YaHei UI",10F); BackColor=Color.FromArgb(246,248,251); ForeColor=ink;
            title.Text="Windows 更新开关"; title.Font=new Font(Font.FontFamily,20,FontStyle.Bold); title.SetBounds(28,22,560,42);
            language.Text="EN"; language.AutoSize=true; language.Location=new Point(620,34); language.LinkClicked+=delegate { SetLanguage(!english); RefreshStatus(); };
            intro.AutoSize=false; intro.SetBounds(30,69,630,26);
            status.Text="正在读取状态…"; status.Font=new Font(Font.FontFamily,14,FontStyle.Bold); status.SetBounds(30,111,630,34);
            sub.Text=""; sub.SetBounds(30,147,640,44);
            disable.Text="禁用更新"; disable.SetBounds(30,197,307,52); disable.BackColor=blue; disable.ForeColor=Color.White; disable.FlatStyle=FlatStyle.Flat; disable.FlatAppearance.BorderSize=0;
            enable.Text="开启更新"; enable.SetBounds(353,197,317,52); enable.BackColor=Color.White; enable.FlatStyle=FlatStyle.Flat; enable.FlatAppearance.BorderColor=Color.FromArgb(195,205,219);
            disable.Click+=async delegate { await Change("--disable"); };
            enable.Click+=async delegate { await Change("--enable"); };
            note.Font=new Font(Font.FontFamily,9); note.AutoSize=false; note.SetBounds(30,260,640,27);
            details.Multiline=true; details.ReadOnly=true; details.ScrollBars=ScrollBars.Vertical; details.BorderStyle=BorderStyle.FixedSingle; details.BackColor=Color.White; details.Font=new Font(Font.FontFamily,9); details.SetBounds(30,293,640,143);
            settings.AutoSize=true; settings.Location=new Point(30,454); settings.LinkClicked+=delegate { Process.Start("ms-settings:windowsupdate"); };
            logs.AutoSize=true; logs.Location=new Point(265,454); logs.LinkClicked+=delegate { string f=Path.Combine(Storage.Data,"events.log"); if(File.Exists(f)) Process.Start("notepad.exe","\""+f+"\""); else MessageBox.Show(english ? "There is no activity log yet." : "尚未启用过守护，没有操作日志。",Text); };
            footer.Font=new Font(Font.FontFamily,8); footer.ForeColor=Color.DimGray; footer.AutoSize=true; footer.Location=new Point(427,456);
            Controls.AddRange(new Control[] { title,language,intro,status,sub,disable,enable,note,details,settings,logs,footer });
            try { english=string.Equals(Convert.ToString(Registry.GetValue(@"HKEY_CURRENT_USER\Software\WindowsUpdateSwitch","Language","zh-CN")),"en",StringComparison.OrdinalIgnoreCase); } catch { english=false; }
            SetLanguage(english,false);
            timer.Interval=3000; timer.Tick+=delegate { if(!working) RefreshStatus(); };
            Shown+=delegate { RefreshStatus(); if(!preview) timer.Start(); };
            FormClosed+=delegate { timer.Dispose(); };
        }
        public void SetLanguage(bool useEnglish,bool save=true) {
            english=useEnglish;
            if(save) try { Registry.SetValue(@"HKEY_CURRENT_USER\Software\WindowsUpdateSwitch","Language",english ? "en" : "zh-CN",RegistryValueKind.String); } catch { }
            Text=english ? "Windows Update Switch" : "Windows 更新开关";
            title.Text=Text; language.Text=english ? "中文" : "EN";
            intro.Text=english ? "Disable updates before training. Enable them for maintenance." : "训练前禁用更新，需要维护时再开启。";
            disable.Text=english ? "Disable updates" : "禁用更新";
            enable.Text=english ? "Enable updates" : "开启更新";
            note.Text=english ? "After protection is enabled, you can close this window. The background guard keeps running." : "启用保护后可关闭窗口，后台守护会继续运行。";
            settings.Text=english ? "Windows Update settings" : "打开 Windows 更新";
            logs.Text=english ? "View log" : "查看日志";
            footer.Text=english ? "Local tool 1.4 · Administrator permission required" : "本地工具 1.4 · 操作时需要管理员授权";
            footer.Location=new Point(english ? 350 : 427,456);
        }
        public void RefreshStatus() {
            try {
                var snapshot=StatusSnapshot.Read(); var dashboard=Dashboard.Build(snapshot);
                State s=snapshot.State; bool live=snapshot.Guard; DateTime checkedAt;
                bool fresh=DateTime.TryParse(s.CheckedUtc,null,System.Globalization.DateTimeStyles.RoundtripKind,out checkedAt) && dashboard.Fresh;
                var lines=dashboard.Rows.Where(x=>x.Kind=="service").Select(x=>x.Item.Name+(english ? ": "+ServiceStatus(x.Item) : "："+x.Status)).ToList();
                status.ForeColor=Color.FromArgb(169,98,14);
                if(s.Mode=="Protect") {
                    if(dashboard.Healthy) { status.Text=english ? "Updates disabled · Guard running" : "已禁用更新 · 守护运行中"; status.ForeColor=Color.FromArgb(23,112,76); }
                    else if(!live) status.Text=english ? "Protection incomplete · Guard not running" : "保护未完成 · 守护未运行";
                    else if(!fresh) status.Text=english ? "Checking protection or awaiting heartbeat" : "正在核验或守护无新回报";
                    else status.Text=english ? "Some items are not disabled · See details" : "部分未禁用 · 请查看详情";
                    sub.Text=english ? "Checks every 10 seconds and corrects changed settings.\r\nUpdates already installing cannot be canceled." : "每 10 秒检查一次。发现设置被改回，会尝试重新禁用。\r\n已开始安装的更新不能靠此工具撤销。";
                } else if(s.Mode=="RestoreFailed" || s.Mode=="Restoring") { status.Text=english ? (s.Mode=="Restoring" ? "Restoring updates…" : "Restore incomplete") : "恢复未完成"; sub.Text=english ? "See the details below. Click Enable updates to retry." : "请查看具体错误，可再次点击“开启更新”重试恢复。"; }
                else { status.Text=english ? (s.Backup.Count==0 ? "Update protection is off" : "Updates restored · Guard stopped") : (s.Backup.Count==0 ? "尚未启用保护" : "已恢复更新配置 · 守护已停止"); status.ForeColor=blue; sub.Text=english ? "Disable updates before training. Enable them for maintenance; Windows or Store may then update automatically." : "开启后 Windows 或 Store 可能自动更新。开始训练前，请点击“禁用更新”。"; }
                if(fresh) lines.Add(english ? "Last verified: "+checkedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "最近核验："+checkedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                if(s.Backup.Count>0) lines.Add(english ? "Saved "+s.Backup.Count+" original settings; originally disabled tasks stay disabled after restore." : "已保存 "+s.Backup.Count+" 项原始设置；恢复时保留原本禁用的任务。");
                if(s.Errors.Count>0) { lines.Add(""); lines.AddRange(s.Errors.Select(TranslateDiagnostic)); }
                if(snapshot.ReadErrors.Count>0) { lines.Add(""); lines.AddRange(snapshot.ReadErrors.Select(TranslateDiagnostic)); }
                if(s.Mode=="Protect" && !live) lines.Add(english ? "The guard is not running. Continuous protection is unavailable." : "守护不在运行；不能把当前状态视为持续保护。");
                details.Text=string.Join(Environment.NewLine,lines);
                enable.Enabled=s.Backup.Count>0;
            } catch(Exception ex) { status.Text=english ? "Unable to verify protection" : "无法核验保护状态"; status.ForeColor=Color.Firebrick; details.Text=TranslateDiagnostic(ex.Message); }
        }
        string ServiceStatus(Item item) {
            string startup=item.Value==4 ? "Disabled" : item.Value==3 ? "Manual" : item.Value==2 ? "Automatic" : "Unknown";
            return startup+(item.Running ? " · Running" : " · Stopped");
        }
        string TranslateDiagnostic(string value) {
            if(!english) return value;
            return value.Replace("权限拒绝","Access denied").Replace("读取服务","Read service").Replace("读取策略","Read policy").Replace("读取计划任务","Read scheduled tasks").Replace("任务目录","task folder").Replace("未达到禁用状态","Did not reach disabled state").Replace("未恢复原设置","Did not restore original settings").Replace("已恢复更新配置","Update settings restored").Replace("恢复未完成","Restore incomplete").Replace("可再次点击开启更新重试恢复","Click Enable updates to retry").Replace("Windows 正在安装更新，暂不强行停止。","Windows is installing updates; it was not interrupted.").Replace("系统已有待重启更新；禁用服务无法取消它，请先安排完成此次更新。","An update is pending a restart and cannot be canceled by disabling services.").Replace("无法确定是否正在安装更新：","Unable to determine whether an update is installing: ").Replace("无法读取更新安装状态：","Unable to read update installation status: ").Replace("保护存在缺口：","Protection gap: ").Replace("守护异常：","Guard error: ");
        }
        async Task Change(string command) {
            if(working) return; working=true; disable.Enabled=false; enable.Enabled=false;
            status.Text=english ? (command=="--disable" ? "Enabling protection…" : "Restoring updates…") : (command=="--disable" ? "正在启用保护…" : "正在恢复更新配置…");
            try {
                var info=new ProcessStartInfo(Application.ExecutablePath,command) { UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden };
                using(var process=Process.Start(info)) {
                    await Task.Run(()=>process.WaitForExit());
                    if(process.ExitCode!=0) sub.Text=english ? "The operation failed. See the error details below." : "操作未完成，原始错误已显示。";
                }
            } catch(Win32Exception ex) { MessageBox.Show(ex.NativeErrorCode==1223 ? (english ? "UAC approval was canceled. No changes were made." : "管理员授权已取消，没有执行本次切换。") : TranslateDiagnostic(ex.Message),Text); }
            catch(Exception ex) { MessageBox.Show(TranslateDiagnostic(ex.Message),Text); }
            finally { working=false; disable.Enabled=true; enable.Enabled=true; RefreshStatus(); }
        }
    }
}
