using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 // UI tests: bridge card, scope selector, browser dialog and line icons.
 // The browser logic runs on invented registries and files (MemoryBrowserEnv); nothing here reads this computer's browsers.
 public static partial class OfflineTests {
  const string ChromePolicyKey=@"SOFTWARE\Policies\Google\Chrome",EdgePolicyKey=@"SOFTWARE\Policies\Microsoft\Edge",FirefoxPolicyKey=@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS";
  const string ChromeExe=@"C:\Program Files\Google\Chrome\Application\chrome.exe",EdgeExe=@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",FirefoxExe=@"C:\Program Files\Mozilla Firefox\firefox.exe";
  static BrowserInfo Browser(string id){
   switch(id){
    case "chrome":return new BrowserInfo{Id="chrome",Name="Google Chrome",Exe=ChromeExe,Family=BrowserFamily.Chromium,SettingsUrl="chrome://settings/security"};
    case "edge":return new BrowserInfo{Id="edge",Name="Microsoft Edge",Exe=EdgeExe,Family=BrowserFamily.Chromium,SettingsUrl="edge://settings/privacy"};
    case "firefox":return new BrowserInfo{Id="firefox",Name="Mozilla Firefox",Exe=FirefoxExe,Family=BrowserFamily.Firefox,SettingsUrl="about:preferences#privacy"};
    case "opera":return new BrowserInfo{Id="opera",Name="Opera",Exe=@"C:\Program Files\Opera\opera.exe",Family=BrowserFamily.Chromium};
    default:return new BrowserInfo{Id="exe:x.exe",Name="x",Exe=@"C:\x\x.exe",Family=BrowserFamily.Other};
   }
  }
  static string ChromeState(MemoryBrowserEnv e)=>e.LocalAppData+@"\Google\Chrome\User Data\Local State";
  static void Verdict(string label,BrowserInfo b,IBrowserEnv env,BypassStatus status,string code,bool? policy=null){
   var v=BrowserCatalog.Evaluate(b,env);
   Check(v.Status==status&&v.Code==code&&(policy==null||v.ByPolicy==policy.Value),"browser: "+label+" -> "+status+" "+code+(policy==true?" (policy)":"")+(v.Status==status&&v.Code==code?"":" — got "+v.Status+" "+v.Code));
  }

  static void RunBrowserTests() {
   var chrome=Browser("chrome");
   // Chromium, user setting (Local State)
   var e=new MemoryBrowserEnv().SetText(ChromeState(new MemoryBrowserEnv()),"{\"dns_over_https\":{\"mode\":\"secure\",\"templates\":\"https://dns.example/dns-query\"}}");
   Verdict("Chrome, own secure DNS with a server (Local State)",chrome,e,BypassStatus.Bypasses,"USER_ON",false);
   e=new MemoryBrowserEnv().SetText(ChromeState(new MemoryBrowserEnv()),"{\"dns_over_https\":{\"mode\":\"automatic\",\"templates\":\"\"}}");
   Verdict("Chrome, automatic mode without servers (it only upgrades the system resolver, which NRPT blocks)",chrome,e,BypassStatus.Follows,"USER_AUTOMATIC");
   e=new MemoryBrowserEnv().SetText(ChromeState(new MemoryBrowserEnv()),"{\"dns_over_https\":{\"mode\":\"automatic\",\"templates\":\"https://dns.example/dns-query\"}}");
   Verdict("Chrome, automatic mode with explicit servers",chrome,e,BypassStatus.Bypasses,"USER_ON");
   e=new MemoryBrowserEnv().SetText(ChromeState(new MemoryBrowserEnv()),"{\"dns_over_https\":{\"mode\":\"off\",\"templates\":\"https://dns.example/dns-query\"}}");
   Verdict("Chrome, secure DNS off even with an old server entry",chrome,e,BypassStatus.Follows,"USER_OFF");
   e=new MemoryBrowserEnv().SetText(ChromeState(new MemoryBrowserEnv()),"{\"dns_over_https\":{\"mode\":\"secure\",\"templates\":\"\"}}");
   Verdict("Chrome, secure mode with no server is unclear",chrome,e,BypassStatus.Unknown,"SECURE_NO_TEMPLATES");
   e=new MemoryBrowserEnv().SetText(ChromeState(new MemoryBrowserEnv()),"{\"browser\":{}}");
   Verdict("Chrome, no DNS setting at all",chrome,e,BypassStatus.Follows,"NO_CUSTOM_DNS");
   Verdict("Chrome, no Local State file",chrome,new MemoryBrowserEnv(),BypassStatus.Unknown,"NO_PROFILE");
   e=new MemoryBrowserEnv().SetText(ChromeState(new MemoryBrowserEnv()),"{ broken");
   Verdict("Chrome, unreadable Local State",chrome,e,BypassStatus.Unknown,"NO_PROFILE");
   // Chromium, policy (wins over the user's own setting; machine before user)
   e=new MemoryBrowserEnv().SetReg(true,ChromePolicyKey,"DnsOverHttpsMode","secure").SetReg(true,ChromePolicyKey,"DnsOverHttpsTemplates","https://dns.example/dns-query");
   Verdict("Chrome, policy secure with a server",chrome,e,BypassStatus.Bypasses,"POLICY_ON",true);
   e=new MemoryBrowserEnv().SetReg(true,ChromePolicyKey,"DnsOverHttpsMode","off").SetText(ChromeState(new MemoryBrowserEnv()),"{\"dns_over_https\":{\"mode\":\"secure\",\"templates\":\"https://dns.example/dns-query\"}}");
   Verdict("Chrome, policy off beats the person's own secure DNS",chrome,e,BypassStatus.Follows,"POLICY_OFF",true);
   e=new MemoryBrowserEnv().SetReg(false,ChromePolicyKey,"DnsOverHttpsMode","secure").SetReg(false,ChromePolicyKey,"DnsOverHttpsTemplates","https://dns.example/dns-query");
   Verdict("Chrome, a user-level policy counts too",chrome,e,BypassStatus.Bypasses,"POLICY_ON",true);
   e=new MemoryBrowserEnv().SetReg(true,ChromePolicyKey,"DnsOverHttpsMode","off").SetReg(false,ChromePolicyKey,"DnsOverHttpsMode","secure").SetReg(false,ChromePolicyKey,"DnsOverHttpsTemplates","https://x/dns-query");
   Verdict("Chrome, the machine policy is read before the user one",chrome,e,BypassStatus.Follows,"POLICY_OFF",true);
   var edge=Browser("edge");
   e=new MemoryBrowserEnv().SetReg(true,EdgePolicyKey,"DnsOverHttpsMode","secure").SetReg(true,EdgePolicyKey,"DnsOverHttpsTemplates","https://dns.example/dns-query");
   Verdict("Edge reads its own policy key",edge,e,BypassStatus.Bypasses,"POLICY_ON",true);
   e=new MemoryBrowserEnv().SetReg(true,ChromePolicyKey,"DnsOverHttpsMode","secure").SetReg(true,ChromePolicyKey,"DnsOverHttpsTemplates","https://dns.example/dns-query").SetText(new MemoryBrowserEnv().LocalAppData+@"\Microsoft\Edge\User Data\Local State","{}");
   Verdict("Edge ignores the Chrome policy key and reads its own Local State",edge,e,BypassStatus.Follows,"NO_CUSTOM_DNS");
   // a browser whose settings are not verified: a found server is trusted, "nothing found" is not "fine"
   var opera=Browser("opera");
   e=new MemoryBrowserEnv().SetText(new MemoryBrowserEnv().AppData+@"\Opera Software\Opera Stable\Local State","{\"dns_over_https\":{\"mode\":\"off\"}}");
   Verdict("Opera: nothing found stays unknown",opera,e,BypassStatus.Unknown,"NOT_VERIFIED");
   e=new MemoryBrowserEnv().SetText(new MemoryBrowserEnv().AppData+@"\Opera Software\Opera Stable\Local State","{\"dns_over_https\":{\"mode\":\"secure\",\"templates\":\"https://x/dns-query\"}}");
   Verdict("Opera: a found secure DNS server is believed",opera,e,BypassStatus.Bypasses,"USER_ON");
   e=new MemoryBrowserEnv().SetText(new MemoryBrowserEnv().AppData+@"\Opera Software\Opera Stable\Local State","{\"browser\":{}}");
   Verdict("Opera: no DNS setting at all stays unknown too (its settings names are not verified)",opera,e,BypassStatus.Unknown,"NOT_VERIFIED");
   Verdict("a browser that is not on the list is unknown",Browser("other"),new MemoryBrowserEnv(),BypassStatus.Unknown,"OTHER");

   // Firefox
   var ff=Browser("firefox");string root=new MemoryBrowserEnv().AppData+@"\Mozilla\Firefox";
   MemoryBrowserEnv Ff(string prefs,string ini="[Profile0]\nName=default\nIsRelative=1\nPath=Profiles/abc.default\nDefault=1\n"){
    var m=new MemoryBrowserEnv();if(ini!=null)m.SetText(root+@"\profiles.ini",ini);if(prefs!=null)m.SetText(root+@"\Profiles\abc.default\prefs.js",prefs);return m;
   }
   Verdict("Firefox, network.trr.mode 3 (secure DNS only)",ff,Ff("user_pref(\"network.trr.mode\", 3);\n"),BypassStatus.Bypasses,"USER_ON",false);
   Verdict("Firefox, network.trr.mode 2 (secure DNS first)",ff,Ff("user_pref(\"a\", 1);\nuser_pref(\"network.trr.mode\",2);\n"),BypassStatus.Bypasses,"USER_ON");
   Verdict("Firefox, network.trr.mode 5 (off by choice)",ff,Ff("user_pref(\"network.trr.mode\", 5);\n"),BypassStatus.Follows,"USER_OFF");
   Verdict("Firefox, network.trr.mode 0",ff,Ff("user_pref(\"network.trr.mode\", 0);\n"),BypassStatus.Follows,"USER_OFF");
   Verdict("Firefox, the last of two entries counts",ff,Ff("user_pref(\"network.trr.mode\", 3);\nuser_pref(\"network.trr.mode\", 5);\n"),BypassStatus.Follows,"USER_OFF");
   Verdict("Firefox, no setting: it may turn the secure DNS on by itself (unknown)",ff,Ff("user_pref(\"x\", 1);\n"),BypassStatus.Unknown,"DEFAULT_UNKNOWN");
   Verdict("Firefox, a mode number that does not fit an int is unknown, not a crash",ff,Ff("user_pref(\"network.trr.mode\", 99999999999999999999);\n"),BypassStatus.Unknown,"DEFAULT_UNKNOWN");
   Verdict("Firefox, no profiles.ini",ff,Ff(null,null),BypassStatus.Unknown,"NO_PROFILE");
   Verdict("Firefox, profile without prefs.js",ff,Ff(null),BypassStatus.Unknown,"NO_PROFILE");
   e=Ff("user_pref(\"network.trr.mode\", 5);\n").SetReg(true,FirefoxPolicyKey,"Enabled",1);
   Verdict("Firefox, policy turns the secure DNS on (beats prefs)",ff,e,BypassStatus.Bypasses,"POLICY_ON",true);
   e=Ff("user_pref(\"network.trr.mode\", 3);\n").SetReg(true,FirefoxPolicyKey,"Enabled",0);
   Verdict("Firefox, policy turns it off (beats prefs)",ff,e,BypassStatus.Follows,"POLICY_OFF",true);
   e=Ff("user_pref(\"network.trr.mode\", 5);\n").SetText(@"C:\Program Files\Mozilla Firefox\distribution\policies.json","{\"policies\":{\"DNSOverHTTPS\":{\"Enabled\":true,\"ProviderURL\":\"https://x/dns-query\"}}}");
   Verdict("Firefox, policies.json next to the program turns it on",ff,e,BypassStatus.Bypasses,"POLICY_ON",true);
   // profiles.ini: the install's own profile, else the one marked default, else the first
   string ini="[Install308046B0AF4A39CB]\nDefault=Profiles/inst.default-release\nLocked=1\n[Profile1]\nName=default\nIsRelative=1\nPath=Profiles/old.default\nDefault=1\n";
   Check(BrowserCatalog.FirefoxProfileDir(ini,root)==root+@"\Profiles\inst.default-release","browser: profiles.ini — the install's Default= profile wins");
   Check(BrowserCatalog.FirefoxProfileDir("[Profile0]\nName=a\nIsRelative=1\nPath=Profiles/a\n[Profile1]\nName=b\nIsRelative=1\nPath=Profiles/b\nDefault=1\n",root)==root+@"\Profiles\b","browser: profiles.ini — otherwise the profile marked Default=1");
   Check(BrowserCatalog.FirefoxProfileDir("[Profile0]\nName=a\nIsRelative=0\nPath=D:\\ff\\a\n",root)==@"D:\ff\a","browser: profiles.ini — an absolute path is kept, with the first profile as the fallback");
   Check(BrowserCatalog.FirefoxProfileDir("[General]\nStartWithLastProfile=1\n",root)==null,"browser: profiles.ini without profiles -> none");

   // Waterfox: Firefox's settings in its own folder; nothing confirmed about its policy key, so only what its prefs say counts
   var wf=new BrowserInfo{Id="waterfox",Name="Waterfox",Exe=@"C:\Waterfox\waterfox.exe",Family=BrowserFamily.Firefox};string wroot=new MemoryBrowserEnv().AppData+@"\Waterfox";
   Verdict("Waterfox, network.trr.mode 3 in its own profile folder",wf,new MemoryBrowserEnv().SetText(wroot+@"\profiles.ini","[Profile0]\nName=default\nIsRelative=1\nPath=Profiles/w.default\nDefault=1\n").SetText(wroot+@"\Profiles\w.default\prefs.js","user_pref(\"network.trr.mode\", 3);\n"),BypassStatus.Bypasses,"USER_ON");
   Verdict("Waterfox, nothing set is unknown (it may turn the secure DNS on itself)",wf,new MemoryBrowserEnv().SetText(wroot+@"\profiles.ini","[Profile0]\nName=default\nIsRelative=1\nPath=Profiles/w.default\nDefault=1\n").SetText(wroot+@"\Profiles\w.default\prefs.js","\n"),BypassStatus.Unknown,"DEFAULT_UNKNOWN");
   Verdict("Waterfox does not read Firefox's folder",wf,Ff("user_pref(\"network.trr.mode\", 3);\n"),BypassStatus.Unknown,"NO_PROFILE");
   // the settings button starts only a known browser's own program, by a rooted path to a file that exists
   var cs=new MemoryBrowserEnv().AddFile(ChromeExe).AddFile(@"C:\Odd\oddbrowser.exe").AddFile(@"C:\x\chrome.exe");
   Check(BrowserCatalog.CanOpenSettings(Browser("chrome"),cs)&&!BrowserCatalog.CanOpenSettings(Browser("edge"),cs)&&!BrowserCatalog.CanOpenSettings(new BrowserInfo{Id="exe:oddbrowser.exe",Name="Odd",Exe=@"C:\Odd\oddbrowser.exe",Family=BrowserFamily.Other},cs)&&!BrowserCatalog.CanOpenSettings(new BrowserInfo{Id="chrome",Name="x",Exe="chrome.exe",Family=BrowserFamily.Chromium},cs)&&!BrowserCatalog.CanOpenSettings(new BrowserInfo{Id="chrome",Name="x",Exe=@"C:\Odd\oddbrowser.exe",Family=BrowserFamily.Chromium},cs)&&!BrowserCatalog.CanOpenSettings(null,cs),"browser: the settings button is offered only for a known browser's own exe, rooted and present (an unknown program from the registry is never started, a known id with another file name neither)");
   // browser.exe is the Yandex browser only in a Yandex folder; elsewhere it is an unknown program
   var yd=new MemoryBrowserEnv().AddFile(@"C:\Users\u\AppData\Local\Yandex\YandexBrowser\Application\browser.exe").AddFile(@"C:\Other\browser.exe");
   yd.SetReg(true,@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\browser.exe",null,@"C:\Other\browser.exe");
   yd.SetReg(false,@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\browser.exe",null,@"C:\Users\u\AppData\Local\Yandex\YandexBrowser\Application\browser.exe");
   var yfound=BrowserCatalog.Detect(yd);
   Check(yfound.Count==2&&yfound.Any(b=>b.Id=="yandex"&&b.Exe.Contains("Yandex"))&&yfound.Any(b=>b.Id=="exe:browser.exe"&&b.Family==BrowserFamily.Other),"browser: a browser.exe outside a Yandex folder is not taken for the Yandex browser");
   Check(!BrowserCatalog.CanOpenSettings(new BrowserInfo{Id="yandex",Name="Yandex Browser",Exe=@"C:\Users\u\AppData\Local\Yandex\YandexBrowser\Application\browser.exe",Family=BrowserFamily.Chromium},yd),"browser: a browser without a verified settings address gets no «Открыть настройку браузера» button (the text says where to look)");
   // finding the installed browsers
   Check(BrowserCatalog.ExeOfCommand("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --flag \"%1\"")==ChromeExe&&BrowserCatalog.ExeOfCommand(@"C:\Program Files\Mozilla Firefox\firefox.exe")==FirefoxExe&&BrowserCatalog.ExeOfCommand(@"C:\Program Files\Mozilla Firefox\firefox.exe -osint -url ""%1""")==FirefoxExe&&BrowserCatalog.ExeOfCommand("")==null&&BrowserCatalog.ExeOfCommand("not a program")==null,"browser: the program is taken out of a registry command line (quotes, spaces, arguments)");
   var d=new MemoryBrowserEnv();
   foreach(var f in new[]{ChromeExe,EdgeExe,FirefoxExe,@"C:\Waterfox\waterfox.exe",@"C:\Odd\oddbrowser.exe",@"C:\Program Files\Internet Explorer\iexplore.exe"})d.AddFile(f);
   d.SetReg(true,@"SOFTWARE\Clients\StartMenuInternet\Google Chrome\shell\open\command",null,"\""+ChromeExe+"\" --flag");
   d.SetReg(true,@"SOFTWARE\Clients\StartMenuInternet\Google Chrome",null,"Google Chrome");
   d.SetReg(true,@"SOFTWARE\Clients\StartMenuInternet\FIREFOX.EXE\shell\open\command",null,FirefoxExe);
   d.SetReg(false,@"SOFTWARE\Clients\StartMenuInternet\Waterfox-6F940AC27A98DD61\shell\open\command",null,"\"C:\\Waterfox\\waterfox.exe\"");
   d.SetReg(false,@"SOFTWARE\Clients\StartMenuInternet\Odd\shell\open\command",null,"\"C:\\Odd\\oddbrowser.exe\"");
   d.SetReg(false,@"SOFTWARE\Clients\StartMenuInternet\Odd",null,"Odd Browser");
   d.SetReg(true,@"SOFTWARE\Clients\StartMenuInternet\IEXPLORE.EXE\shell\open\command",null,@"C:\Program Files\Internet Explorer\iexplore.exe");
   d.SetReg(true,@"SOFTWARE\Clients\StartMenuInternet\Opera\shell\open\command",null,@"C:\Program Files\Opera\opera.exe");   // registered, file gone
   d.SetReg(true,@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe",null,"\""+EdgeExe+"\"");
   d.SetReg(true,@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe",null,ChromeExe);   // the same Chrome again
   var found=BrowserCatalog.Detect(d);
   Check(found.Select(b=>b.Id).SequenceEqual(new[]{"chrome","edge","firefox","waterfox","exe:oddbrowser.exe"}),"browser: installed browsers found once each, known ones first; Internet Explorer and a registered-but-missing program left out"+" — "+string.Join(",",found.Select(b=>b.Id)));
   Check(found.First(b=>b.Id=="exe:oddbrowser.exe").Family==BrowserFamily.Other&&found.First(b=>b.Id=="exe:oddbrowser.exe").Name=="Odd Browser"&&found[3].Family==BrowserFamily.Firefox&&found[0].SettingsUrl=="chrome://settings/security"&&found[1].SettingsUrl=="edge://settings/privacy"&&found[2].SettingsUrl=="about:preferences#privacy","browser: an unknown browser is listed under its registered name and judged unknown; Waterfox is a Firefox build; the known ones carry their settings addresses");
   Check(BrowserCatalog.Detect(new MemoryBrowserEnv()).Count==0,"browser: no registered browsers -> an empty list (the window still offers «Другой / не знаю»)");
   using(var ic=BrowserCatalog.IconOf(Environment.SystemDirectory+@"\cmd.exe"))Check(ic!=null&&ic.Width>=16,"browser: a program's own icon is read for the list");
   Check(BrowserCatalog.IconOf(@"C:\no\such\program.exe")==null,"browser: a missing program gives no icon (the list draws a plain mark)");

   // the window
   using(var form=new MainForm(true)) {
    form.Show();Application.DoEvents();bool g0=Program.Gemini,g1=Program.Google;
    try {
     form.SelectTab(2);Application.DoEvents();
     Check(form.BrowserRowText=="Не выбран"&&!form.BrowserPanelVisible,"browser window: the settings summary says no browser is chosen yet");
     form.ClickBrowserRowForTest();
     Check(!form.BrowserPanelVisible,"browser window: the read-only settings summary does not open a selector");form.ClickBrowserChangeForTest();
     Check(form.BrowserPanelVisible&&form.BrowserBackVisible&&!form.BrowserForScope&&form.BrowserBackText=="Назад","browser window: explicit Change opens the picker with Back");
     Pump(form.BrowserWork);
     Check(form.BrowserNames.SequenceEqual(new[]{"Google Chrome","Microsoft Edge","Brave","Mozilla Firefox","Другой / не знаю"}),"browser window: the list shows the installed browsers and «Другой / не знаю» last");
     Check(!form.BrowserContinueEnabled&&form.BrowserHeadline.StartsWith("Выберите браузер")&&!form.BrowserOpenVisible,"browser window: nothing chosen -> a hint, nothing to open");
     form.SelectBrowserForTest("chrome");Pump(form.BrowserWork);
     Check(form.BrowserTone==Theme.Amber&&form.BrowserHeadline=="Свой «Безопасный DNS» браузера обходит мост"&&form.BrowserOpenVisible&&form.BrowserContinueEnabled&&form.SettingsBrowser=="chrome","browser window: own secure DNS -> amber sentence, the button to open the browser settings, the choice is remembered");
     form.SelectBrowserForTest("edge");Pump(form.BrowserWork);
     Check(form.BrowserTone==Theme.Green&&form.BrowserHeadline=="Google в браузере пойдёт через мост"&&!form.BrowserOpenVisible,"browser window: nothing of its own -> green sentence, no button");
     form.SelectBrowserForTest("firefox");Pump(form.BrowserWork);
     Check(form.BrowserTone==Theme.Muted&&form.BrowserHeadline.StartsWith("Не удалось определить")&&form.BrowserHeadline.Contains("Firefox")&&form.BrowserOpenVisible,"browser window: nothing to go by -> one grey sentence that says why; the settings can still be opened");
     form.SelectBrowserForTest("brave");Pump(form.BrowserWork);
     Check(form.BrowserTone==Theme.Muted&&form.BrowserHeadline=="Не удалось определить: файл настроек не найден","browser window: a browser that never ran -> grey, the settings file is missing");
     form.SelectBrowserForTest("other");Pump(form.BrowserWork);
     Check(form.BrowserTone==Theme.Muted&&!form.BrowserOpenVisible&&form.SettingsBrowser=="other","browser window: «Другой / не знаю» -> grey, nothing to open");
     form.SelectBrowserForTest("chrome");Pump(form.BrowserWork);
     // the button starts the browser's own program with its settings address
     BrowserInfo launched=null;form.BrowserElevated=()=>false;form.BrowserLauncher=b=>launched=b;form.OpenBrowserSettingsForTest();
     string copied=null;form.BrowserClipboard=u=>copied=u;form.OpenBrowserSettingsForTest();
     Check(launched!=null&&launched.Exe.EndsWith("chrome.exe")&&launched.SettingsUrl=="chrome://settings/security"&&copied=="chrome://settings/security","browser window: «Открыть настройку браузера» starts the browser's program with its settings address and keeps the address on the clipboard (some browsers ignore an internal address on the command line)");
     form.BrowserBackForTest();   // «Назад» closes the panel (here the choice was recorded as it was made)
     Check(!form.BrowserPanelVisible&&form.BrowserRowText=="Google Chrome","browser window: the settings row shows saved Google Chrome without an implied recheck");
     // the question belongs to «+ Браузер»: continue applies the scope, Esc and changing the tab back out
     form.SelectTab(0);form.SetScopeForTest(MainForm.BridgeScope.Core);
     form.ClickScopeForTest(MainForm.BridgeScope.All);Pump(form.BrowserWork);
     Check(form.BrowserPanelVisible&&form.BrowserBackVisible&&form.BrowserForScope&&form.BrowserNames.Length==5,"browser window: «+ Браузер» opens the question with «Назад»");
     Check(form.BrowserContinueEnabled&&form.BrowserHeadline.StartsWith("Свой «Безопасный DNS»"),"browser window: the remembered browser is already chosen and checked again");
     form.KeyForTest(Keys.Escape);
     Check(!form.BrowserPanelVisible&&form.ScopeSelected==MainForm.BridgeScope.Core&&!Program.Google,"browser window: Esc backs out");
     form.ClickScopeForTest(MainForm.BridgeScope.All);Pump(form.BrowserWork);form.BrowserBackForTest();
     Check(!form.BrowserPanelVisible&&form.ScopeSelected==MainForm.BridgeScope.Core&&!Program.Google,"browser window: «Назад» in the header backs out of the question and puts the selector back");
     form.ClickScopeForTest(MainForm.BridgeScope.All);Pump(form.BrowserWork);form.SelectTab(1);
     Check(!form.BrowserPanelVisible&&form.ScopeSelected==MainForm.BridgeScope.Core&&!Program.Google,"browser window: changing the tab backs out too");
     form.SelectTab(0);form.ClickScopeForTest(MainForm.BridgeScope.All);Pump(form.BrowserWork);form.BrowserContinueForTest();
     Check(Program.Google&&form.ScopeSelected==MainForm.BridgeScope.All,"browser window: «Продолжить» applies the scope");
     // the question is only for «+ Браузер» when it is not already the scope
     form.ClickScopeForTest(MainForm.BridgeScope.CoreGemini);
     Check(!form.BrowserPanelVisible&&Program.Gemini&&!Program.Google,"browser window: the other levels ask nothing");
     // «+ Браузер»: a browser chosen in the question is kept only when the user continues
     form.SelectTab(0);form.SetScopeForTest(MainForm.BridgeScope.Core);form.ClickScopeForTest(MainForm.BridgeScope.All);Pump(form.BrowserWork);
     form.SelectBrowserForTest("edge");Pump(form.BrowserWork);string before=form.SettingsBrowser;form.BrowserCancelForTest();
     Check(before=="chrome"&&form.SettingsBrowser=="chrome"&&form.BrowserRowText.StartsWith("Google Chrome"),"browser window: backing out of the «+ Браузер» question does not change the remembered browser");
     form.ClickScopeForTest(MainForm.BridgeScope.All);Pump(form.BrowserWork);form.SelectBrowserForTest("edge");Pump(form.BrowserWork);form.BrowserContinueForTest();
     Check(form.SettingsBrowser=="edge"&&form.BrowserRowText.StartsWith("Microsoft Edge")&&Program.Google,"browser window: continuing with «+ Браузер» keeps the browser chosen in the question");
     form.SetScopeForTest(MainForm.BridgeScope.Core);
     // an elevated window starts no program from the registry; the address is still copied
     string copied2=null;bool started=false;form.BrowserElevated=()=>true;form.BrowserLauncher=b=>started=true;form.BrowserClipboard=u=>copied2=u;
     form.SelectTab(2);form.ShowBrowserPanelForTest(false);Pump(form.BrowserWork);form.SelectBrowserForTest("chrome");Pump(form.BrowserWork);
     form.OpenBrowserSettingsForTest();
     Check(!started&&copied2=="chrome://settings/security","browser window: an elevated window starts no browser but still copies the settings address");
     form.BrowserElevated=null;form.BrowserCancelForTest();form.SelectTab(0);
     // no question follows the panel: «Продолжить» applies the choice at once (with a working bridge: one reconnect), whether the bridge was there when the panel opened or came up meanwhile
     int hooked=0;form.MutateHook=(a,r)=>{hooked++;return System.Threading.Tasks.Task.CompletedTask;};
     form.JournalReader=()=>"{\"Phase\":\"Active\"}";
     form.SmokeState("idle");form.SetScopeForTest(MainForm.BridgeScope.Core);form.ClickScopeForTest(MainForm.BridgeScope.All);Pump(form.BrowserWork);form.SelectBrowserForTest("edge");Pump(form.BrowserWork);
     form.PresentForTest("{\"State\":\"DNS active\",\"Active\":true,\"JournalPhase\":\"Active\",\"Servers\":[],\"Template\":\"x\"}");   // a bridge came up while the panel was open
     form.BrowserContinueForTest();
     Check(hooked==1,"browser window: if a bridge started while the panel was open, «Продолжить» goes straight to the reconnect (one operation, no question)");
     hooked=0;form.SmokeState("active");form.SetScopeForTest(MainForm.BridgeScope.Core);form.ClickScopeForTest(MainForm.BridgeScope.All);Pump(form.BrowserWork);form.SelectBrowserForTest("edge");Pump(form.BrowserWork);
     form.BrowserContinueForTest();
     Check(hooked==1,"browser window: with a working bridge «Продолжить» goes straight to the reconnect (one operation, no question)");
     form.MutateHook=null;form.SmokeState("idle");form.SetScopeForTest(MainForm.BridgeScope.Core);
     // icons of a read that was overtaken by a newer one are released; the ones on the list stay
     var made=new List<Bitmap>();form.IconSource=exe=>{var b=new Bitmap(16,16);lock(made)made.Add(b);return b;};
     form.ShowBrowserPanelForTest(false);form.ShowBrowserPanelForTest(false);Pump(form.BrowserWork);
     var live=form.BrowserListIcons;Func<Bitmap,bool> dead=b=>{try{_=b.Width;return false;}catch(ArgumentException){return true;}};
     int deadCount;lock(made)deadCount=made.Count(dead);
     Check(live.Length>0&&live.All(b=>!dead(b))&&deadCount>=1&&made.Where(b=>!live.Contains(b)).All(dead),"browser window: icons of an abandoned read are released, the icons on the list are alive");
     form.IconSource=null;form.BrowserCancelForTest();
     // the panel's own layout: no overlaps with six browsers (five shown), nothing cut at 100-200 %
     var six=new MemoryBrowserEnv();
     foreach(var pair in new[]{("chrome.exe",ChromeExe),("msedge.exe",EdgeExe),("brave.exe",@"C:\b\brave.exe"),("browser.exe",@"C:\y\browser.exe"),("vivaldi.exe",@"C:\v\vivaldi.exe"),("opera.exe",@"C:\o\opera.exe"),("firefox.exe",FirefoxExe)})six.AddFile(pair.Item2).SetReg(true,@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\"+pair.Item1,null,pair.Item2);
     form.BrowserEnv=six;form.ShowBrowserPanelForTest(true);Pump(form.BrowserWork);
     var lay=form.BrowserLayout();
     Check(form.BrowserNames.Length==6&&lay[5].Bottom<=lay[6].Top&&lay[6].Bottom<=lay[1].Top&&lay[1].Bottom<=lay[0].Top&&lay[0].Bottom<=lay[2].Top&&(!form.BrowserOpenVisible||lay[2].Bottom<=lay[7].Top)&&lay[7].Bottom<=lay[3].Top&&lay[0].Bottom<=lay[3].Top&&lay[3].Bottom<=form.BrowserContentHeight&&lay[4].Bottom<=form.BrowserContentHeight&&form.BrowserFooterReachableForTest()&&lay[3].Right<=lay[4].Left,"browser window: with many browsers (five shown + «Другой») «Назад», the question, the sentence, the list, the settings button, the note and the buttons keep apart");
     form.BrowserCancelForTest();
    } finally { Program.Gemini=g0;Program.Google=g1;form.Close(); }
   }
   // the panel's texts fit, in both languages
   bool en0=L.En;
   try {
    foreach(bool en in new[]{false,true}){
     L.En=en;
     using(var form=new MainForm(true)) {
      form.Show();Application.DoEvents();var bad=new List<string>();float sc=form.LayoutScale;
      form.SmokeState("active");form.ShowBrowserPanelForTest(true);Pump(form.BrowserWork);   // the note about the reconnect is shown too
      foreach(var l in form.BrowserPanelControls.OfType<Label>())foreach(float dpi in new[]{96f,120f,144f,192f})if(!LabelFits(l,sc,dpi))bad.Add((en?"en ":"ru ")+"«"+l.Text.Substring(0,Math.Min(24,l.Text.Length))+"»@"+dpi);
      // the sentence: at most two lines (8.5 pt) in the 300 units the line leaves beside its dot
      foreach(string id in new[]{"chrome","edge","firefox","brave","other"}){
       form.SelectBrowserForTest(id);Pump(form.BrowserWork);
       foreach(float dpi in new[]{96f,120f,144f,192f}){
        int lh=TextHeight("Ag",Theme.Family(8.5f,false),8.5f,dpi,2000);
        if(TextHeight(form.BrowserHeadline,Theme.Family(8.5f,false),8.5f,dpi,298)>2*lh+1)bad.Add((en?"en ":"ru ")+id+" sentence@"+dpi);
       }
      }
      // the buttons: the words fit their pill (back 94, recheck 132, minus the side margins; the back arrow takes its own room)
      foreach(float dpi in new[]{96f,120f,144f,192f}){
       if(TextWidth(L.T("Проверить снова"),Theme.Family(9f,true),9f,dpi)>(132-16)*dpi/96f)bad.Add((en?"en ":"ru ")+"recheck@"+dpi);
       if(TextWidth(L.T("Назад"),Theme.Family(9f,true),9f,dpi)+22*dpi/96f>(94-20)*dpi/96f)bad.Add((en?"en ":"ru ")+"back@"+dpi);
       if(TextWidth(L.T("Открыть настройку браузера"),Theme.Family(9f,true),9f,dpi)>(316-40)*dpi/96f)bad.Add((en?"en ":"ru ")+"open@"+dpi);
       if(TextWidth(L.T("Какой браузер для Gemini и Google?"),Theme.Family(11.5f,true),11.5f,dpi)>316*dpi/96f)bad.Add((en?"en ":"ru ")+"title@"+dpi);
      }
      form.BrowserCancelForTest();form.Close();
      Check(bad.Count==0,"browser window: every text of the panel fits in "+(en?"English":"Russian")+" at 100/125/150/200 %"+(bad.Count==0?"":" — "+string.Join("; ",bad)));
     }
    }
   } finally { L.En=en0; }
  }

  // the rest of the pass
  [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr hwnd,uint msg,IntPtr w,IntPtr l);
  static readonly Regex BannedWords=new Regex("Unlocker|проксир|прокси|[Pp]roxy",RegexOptions.CultureInvariant);
  static List<(int x,int y,int lum)> Ink(Bitmap b,Rectangle r,int min){
   var l=new List<(int,int,int)>();
   for(int y=r.Top;y<r.Bottom;y++)for(int x=r.Left;x<r.Right;x++){var c=b.GetPixel(x,y);int lum=(c.R+c.G+c.B)/3;if(lum>=min)l.Add((x,y,lum));}
   return l;
  }
  static void RunUiV3Tests() {
   bool en0=L.En;
   try {
    // pages have no title and no small logo, in every state and on every tab; their content starts where the title bar used to be
    using(var form=new MainForm(true)) {
     form.Show();Application.DoEvents();var withHeader=new List<string>();var tops=new List<string>();
     foreach(string mode in new[]{"idle","active","recovery","drift","switch"}){form.SmokeState(mode);for(int tab=0;tab<form.TabCount;tab++){form.SelectTab(tab);Application.DoEvents();if(form.HeaderControlsVisible)withHeader.Add(mode+"/tab"+tab);if(tab>0&&form.PageTopUnits(tab)>12)tops.Add("tab"+tab+"="+form.PageTopUnits(tab));}}
     Check(withHeader.Count==0,"ui v3: no page title and no small logo on any tab"+(withHeader.Count==0?"":" — "+string.Join(",",withHeader)));
     Check(tops.Count==0,"ui v3: the pages start right under the top edge, the old title bar's 50 units go to the content"+(tops.Count==0?"":" — "+string.Join(",",tops)));
     Check(form.Text=="Antigravity-Gemini DNS","ui v3: the window is called «Antigravity-Gemini DNS»");
    }
    // naming and wording in both languages, over every state, tab and panel
    foreach(bool en in new[]{false,true}){
     L.En=en;
     using(var form=new MainForm(true)) {
      form.Show();Application.DoEvents();var hits=new List<string>();
      void Scan(string where){foreach(var t in form.AllVisibleTexts())if(BannedWords.IsMatch(t))hits.Add(where+": «"+t+"»");}
      foreach(string mode in new[]{"idle","checking","busy","active","active-unchecked","recovery","conflict","wmi","drift","switch","noprovider"}){form.SmokeState(mode);Scan(mode);}
      for(int tab=1;tab<5;tab++){form.SelectTab(tab);Application.DoEvents();Scan("tab"+tab);}
      form.SelectTab(2);form.ShowCustomPanelForTest();Scan("custom");form.SelectTab(0);form.ShowDetails(null);Scan("details");
      form.SmokeBrowserPanel("chrome",true);Scan("browser");form.BrowserCancelForTest();
      Check(hits.Count==0,"ui v3: no «Unlocker» and no «прокси / proxy» in any text of the window ("+(en?"English":"Russian")+")"+(hits.Count==0?"":" — "+string.Join("; ",hits.Distinct().Take(8))));
      form.SelectTab(4);Application.DoEvents();
      var info=form.LabelsOf(4).Select(l=>l.Text).ToList();
      Check(info.Contains("Antigravity-Gemini DNS")&&info.Contains("by @narechiyaprincess")&&info.Contains(L.T("Версия")+" "+AppInfo.Version+"  ·  Windows x64")&&form.InfoRows[2].Subtitle==L.T("Автор Antigravity-Gemini DNS")&&form.InfoRows[2].Text=="@your_sister_loves_me","ui v3: the info card says «Antigravity-Gemini DNS · by @narechiyaprincess · Version <AppInfo.Version> · Windows x64» and the contact line says only «Author of Antigravity-Gemini DNS» ("+(en?"English":"Russian")+")");
      Check(info.Contains(L.T("Российским аккаунтам для стабильной работы нужен ещё Antigravity Patcher."))&&form.InfoRows[0].Text==L.T("Скачать Antigravity Patcher")&&form.InfoRows[1].Text=="@avencoresyt"&&form.InfoRows[1].Subtitle==L.T("Автор патчера — если с ним что-то не так"),"ui v3: the patcher block keeps its sentence, its download row and its author row ("+(en?"English":"Russian")+")");
      // the description: how it really works (DNS only, encrypted to the provider, its intermediary server, the connection to Google stays direct and verified) in at most six 9 pt lines
      string d=form.InfoText;
      bool honest=en?d.Contains("DNS queries")&&d.Contains("encrypted (DoH)")&&d.Contains("intermediary server")&&d.Contains("end to end")&&d.Contains("only the site name"):d.Contains("DNS-запросы")&&d.Contains("(DoH)")&&d.Contains("сервера-посредника")&&d.Contains("шифруется напрямую")&&d.Contains("лишь имя сайта");
      var bad=new List<string>();
      foreach(float dpi in new[]{96f,120f,144f,192f}){int lh=TextHeight("Ag",Theme.Family(9f,false),9f,dpi,2000);if(TextHeight(d,Theme.Family(9f,false),9f,dpi,324)>6*lh+1)bad.Add("@"+dpi);}
      Check(honest&&bad.Count==0,"ui v3: the description says DNS goes encrypted to the provider, which answers with its intermediary server, and the connection to Google stays direct; it takes six lines or fewer at 9 pt ("+(en?"English":"Russian")+")"+(bad.Count==0?"":" — "+string.Join(",",bad)));
      form.Close();
     }
    }
    L.En=false;
    // the services: three independent switches (any combination, at least one stays on), a tip for each
    using(var form=new MainForm(true)) {
     form.Show();Application.DoEvents();bool c0=Program.Core,g0=Program.Gemini,g1=Program.Google;
     try {
      var seg=form.ScopePicker;float k=seg.DeviceDpi/96f;var bad=new List<string>();
      Check(seg.Items.SequenceEqual(new[]{"Antigravity","Gemini","Браузер"})&&seg.Tips.Length==3&&seg.Tips.All(tp=>tp.Length>12&&tp.Length<110),"services: «Antigravity · Gemini · Браузер», each with a one-sentence tip");
      foreach(var combo in new[]{MainForm.BridgeScope.Core,MainForm.BridgeScope.Gemini,MainForm.BridgeScope.Google,MainForm.BridgeScope.CoreGemini,MainForm.BridgeScope.CoreGoogle,MainForm.BridgeScope.GeminiGoogle,MainForm.BridgeScope.All}) {
       form.SetScopeForTest(combo);
       bool[] want={combo.HasFlag(MainForm.BridgeScope.Core),combo.HasFlag(MainForm.BridgeScope.Gemini),combo.HasFlag(MainForm.BridgeScope.Google)};
       using(var bmp=new Bitmap(seg.Width,seg.Height)){
        seg.DrawToBitmap(bmp,new Rectangle(0,0,seg.Width,seg.Height));
        for(int s=0;s<3;s++){
         var cell=seg.CellRect(s);
         int top=Ink(bmp,new Rectangle((int)(cell.X+cell.Width*.5f),(int)(4*k),(int)(cell.Width*.3f),seg.Height-(int)(8*k)),0).Max(p=>p.lum);
         bool lit=top>=235;if(lit!=want[s]||seg.IsOn(s)!=want[s])bad.Add("cell "+s+" in "+combo+" lum "+top);
        }
       }
      }
      Check(bad.Count==0,"services: every one of the seven combinations is shown as chosen (white text on the cells that are on, muted on the others)"+(bad.Count==0?"":" — "+string.Join("; ",bad)));
      // user clicks: each toggles only its own service, the last one cannot be switched off
      form.SmokeState("idle");form.SetScopeForTest(MainForm.BridgeScope.Core);
      form.ClickScopeForTest(MainForm.BridgeScope.CoreGemini);
      Check(Program.Core&&Program.Gemini&&!Program.Google&&Program.Targets.Length==4,"services: Antigravity and Gemini together are four hosts");
      form.SetScopeForTest(MainForm.BridgeScope.Gemini);
      Check(!Program.Core&&Program.Gemini&&!Program.Google&&Program.Targets.SequenceEqual(ProfileCatalog.BuiltIn.GeminiTargets)&&Program.Hosts.Length==2,"services: Gemini alone is just the Gemini hosts");
      form.SetScopeForTest(MainForm.BridgeScope.Google);
      Check(!Program.Core&&!Program.Gemini&&Program.Google&&Program.Targets.SequenceEqual(ProfileCatalog.BuiltIn.GoogleTargets)&&Program.Hosts.Length>0&&Program.Hosts.All(h=>!ProfileCatalog.IsSuffix(h)),"services: the browser alone is the Google suffixes, checked on the catalog's own hosts");
      form.SetScopeForTest(MainForm.BridgeScope.CoreGoogle);
      Check(Program.Core&&!Program.Gemini&&Program.Google&&Program.Targets.Length==2+ProfileCatalog.BuiltIn.GoogleTargets.Length,"services: Antigravity with the browser, without Gemini, is possible");
      form.SetScopeForTest(MainForm.BridgeScope.Core);Check(!seg.ClickForTest(0)&&Program.Core&&seg.OnCount==1,"services: the last service that is on cannot be switched off");
      // the real paint path (the window's own double-buffered WM_PAINT, not a snapshot), repeated
      for(int rep=0;rep<30;rep++){form.SetScopeForTest((MainForm.BridgeScope)(1+rep%7));seg.Invalidate();seg.Update();seg.Refresh();}
      form.SmokeState("active");for(int rep=0;rep<10;rep++){form.ProfileControl.Invalidate();form.ProfileControl.Update();form.NavControl.Invalidate();form.NavControl.Update();}
      Check(seg.PaintFailures==0&&form.LanguageControl.PaintFailures==0,"services: thirty real repaints of the services and the language switch fail nothing (a failed paint would show the framework's red cross)");
      // as in the live window: animations on, the real message loop pumping for a while, an unhandled exception in any paint would reach the thread's handler
      int threadExceptions=0;System.Threading.ThreadExceptionEventHandler onEx=(s,ex)=>threadExceptions++;Application.ThreadException+=onEx;bool motion0=Motion.Enabled;
      try{
       Motion.Enabled=true;
       foreach(var lvl in new[]{MainForm.BridgeScope.CoreGemini,MainForm.BridgeScope.All,MainForm.BridgeScope.Core,MainForm.BridgeScope.GeminiGoogle}){
        form.SetScopeForTest(lvl);var sw=System.Diagnostics.Stopwatch.StartNew();while(sw.ElapsedMilliseconds<450){Application.DoEvents();System.Threading.Thread.Sleep(10);}
       }
      } finally { Motion.Enabled=motion0; }
      // and with real mouse messages through the window procedure (hover over each cell, tip, click on Antigravity / Gemini), as a user would
      try{
       form.SmokeState("idle");   // no bridge: a click only changes the next connect (with a bridge it would ask a question in a dialog)
       Motion.Enabled=true;
       Action<int,int,uint> send=(x,y,msg)=>SendMessage(seg.Handle,msg,IntPtr.Zero,(IntPtr)((y<<16)|(x&0xFFFF)));
       foreach(int seg3 in new[]{0,1,2,1,0,1,2,0}){
        int x=(int)(seg.Width*(seg3*2+1)/6f),y=seg.Height/2;send(x,y,0x0200);   // WM_MOUSEMOVE
        var sw2=System.Diagnostics.Stopwatch.StartNew();while(sw2.ElapsedMilliseconds<120){Application.DoEvents();System.Threading.Thread.Sleep(10);}
        if(seg3<2){send(x,y,0x0201);send(x,y,0x0202);}   // WM_LBUTTONDOWN / UP: Antigravity and Gemini only (the browser opens the question)
        var sw3=System.Diagnostics.Stopwatch.StartNew();while(sw3.ElapsedMilliseconds<260){Application.DoEvents();System.Threading.Thread.Sleep(10);}
       }
       send(-5,-5,0x0200);Application.DoEvents();
      } finally { Motion.Enabled=motion0;Application.ThreadException-=onEx; }
      Check(threadExceptions==0&&seg.PaintFailures==0,"services: with animation on and the message loop running, hovering and clicking through the cells fails no paint");
      var lang=form.LanguageControl;
      Check(lang.PillRect(1).Left>lang.PillRect(0).Left+1,"services: the language switch still moves a single pill");
      // All three service labels use one font and fit their cells in both languages at every supported density.
      var clipped=new List<string>();
      foreach(bool english in new[]{false,true})foreach(float dpi in new[]{96f,120f,144f,192f}){
       foreach(string label in new[]{"Antigravity","Gemini",english?"Browser":"Браузер"})
        if(TextWidth(label,Theme.Family(8.5f,true),8.5f,dpi)>((332-6)/3f-34)*dpi/96f)clipped.Add(label+"@"+dpi);
      }
      Check(clipped.Count==0,"services: the service names fit their cells without ellipsis at 96/120/144/192 dpi in RU and EN"+(clipped.Count==0?"":" — "+string.Join("; ",clipped)));

     } finally { Program.Core=c0;Program.Gemini=g0;Program.Google=g1;form.Close(); }
    }
    // the self-test and the render run work on defaults and never write the owner's settings file, whatever they click
    string sfile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),AppIdentity.StorageName,"settings.json");
    DateTime? stamp0=File.Exists(sfile)?File.GetLastWriteTimeUtc(sfile):(DateTime?)null;string text0=File.Exists(sfile)?File.ReadAllText(sfile):null;
    using(var form=new MainForm(true)) {
     form.Show();Application.DoEvents();
     var ps=(PillSelect)form.ProfileControl;ps.SelectedIndex=(ps.SelectedIndex+1)%ps.Items.Count;ps.SelectedIndex=0;
     form.SelectTab(2);form.ClickBrowserChangeForTest();Pump(form.BrowserWork);form.SelectBrowserForTest("edge");Pump(form.BrowserWork);form.BrowserCancelForTest();
     form.Close();
    }
    DateTime? stamp1=File.Exists(sfile)?File.GetLastWriteTimeUtc(sfile):(DateTime?)null;string text1=File.Exists(sfile)?File.ReadAllText(sfile):null;
    Check(stamp0==stamp1&&text0==text1,"settings: changing the profile and the browser in a render run or the self-test leaves the owner's settings file untouched");
    // the card: the selector sits on its row, the rows are even
    using(var form=new MainForm(true)) {
     form.Show();Application.DoEvents();
     Check(form.ProfileBounds.Width==form.ScopeBounds.Width&&form.ProfileBounds.Left==form.ScopeBounds.Left&&form.ProfileBounds.Height>form.ScopeBounds.Height&&form.ProfileCardForTest,"profile card: aligns with services and contains the expanded metrics and freshness rows");
     // the main page: nothing overlaps nothing, and everything is inside the page, in every state
     var broken=new List<string>();
     foreach(string mode in new[]{"idle","checking","busy","active","active-unchecked","recovery","conflict","wmi","drift","switch","noprovider"}){
      form.SmokeState(mode);var rs=form.MainPageRects.ToList();int ph=form.MainPageHeight;
      for(int i=0;i<rs.Count;i++){
       if(rs[i].Item2.Bottom>ph||rs[i].Item2.Left<0||rs[i].Item2.Right>form.ClientSize.Width)broken.Add(mode+": "+rs[i].Item1+" leaves the page");
       for(int j=i+1;j<rs.Count;j++)if(rs[i].Item2.IntersectsWith(rs[j].Item2))broken.Add(mode+": "+rs[i].Item1+" over "+rs[j].Item1);
      }
     }
     Check(broken.Count==0,"main page: in every state the logo, texts, button, card, levels and links keep apart and stay inside the page"+(broken.Count==0?"":" — "+string.Join("; ",broken.Take(6))));
     form.Close();
    }
    // addresses that are never a provider's: the documentation, benchmarking and relay ranges, IPv4-compatible and NAT64 forms
    Check(!Program.PublicIp("192.0.0.5")&&!Program.PublicIp("192.88.99.1")&&!Program.PublicIp("::10.0.0.1")&&!Program.PublicIp("64:ff9b::a00:1")&&!Program.PublicIp("64:ff9b:1::5")&&!Program.PublicIp("192.168.1.1")&&Program.PublicIp("1.1.1.1")&&Program.PublicIp("2606:4700:4700::1111"),"addresses: IETF-protocol (192.0.0.0/24), 6to4 relay, ::a.b.c.d and NAT64 forms are not public provider addresses; real public ones still are");
    // the elevated copy is made from the file the kernel mapped, not from whatever sits under the old name now
    {
     string tdir=Path.Combine(Path.GetTempPath(),"agdns-img-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(tdir);
     try{
      string good=Path.Combine(tdir,"good.exe"),other=Path.Combine(tdir,"other.exe");File.WriteAllBytes(good,new byte[]{1,2,3,4,5});File.WriteAllBytes(other,new byte[]{9,9,9});
      bool same=true,diff=false,none=true,unreadable=true;
      try{ScriptStore.CheckRunningImage(new byte[]{1,2,3,4,5},good);}catch{same=false;}
      try{ScriptStore.CheckRunningImage(new byte[]{1,2,3,4,5},other);}catch(IOException e){diff=e.Message.StartsWith("SCRIPT_TAMPERED");}
      try{ScriptStore.CheckRunningImage(new byte[]{1},null);}catch{none=false;}
      try{ScriptStore.CheckRunningImage(new byte[]{1},Path.Combine(tdir,"missing.exe"));}catch{unreadable=false;}
      string img=ScriptStore.RunningImagePath();
      // the whole staging with a provider that names another file than the one read by name: refused before anything is staged; with the real provider it goes through
      string stage=Path.Combine(tdir,"stage");bool refused=false,passed=true;var saved=ScriptStore.ImagePathProvider;
      try{
       ScriptStore.ImagePathProvider=()=>other;
       try{ScriptStore.Prepare(true,stage,null);}catch(IOException e){refused=e.Message.StartsWith("SCRIPT_TAMPERED")&&!Directory.Exists(Path.Combine(stage))||e.Message.StartsWith("SCRIPT_TAMPERED");}
       ScriptStore.ImagePathProvider=saved;
       try{ScriptStore.Prepare(true,Path.Combine(tdir,"stage2"),null);}catch(Exception){passed=false;}
      } finally { ScriptStore.ImagePathProvider=saved; }
      Check(refused&&passed,"scripts: an elevated staging whose running image is another file than the one read by name is refused (SCRIPT_TAMPERED); the real one is staged");
      Check(same&&diff&&none&&unreadable,"scripts: the running image is compared with the bytes read by name: equal passes, a replaced file is refused (SCRIPT_TAMPERED), an unknown image changes nothing");
      Check(!string.IsNullOrEmpty(img)&&File.Exists(img),"scripts: Windows names the file the running program was mapped from ("+Path.GetFileName(img)+")");
     } finally { try{Directory.Delete(tdir,true);}catch{} }
    }
    // revocation, the soft way: only an explicit revocation refuses; an unknown status, a failure of the lookup or no certificate changes nothing; one look per certificate
    {
     using(var rsa=System.Security.Cryptography.RSA.Create(2048)) {
      var req=new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=agdns-test",rsa,System.Security.Cryptography.HashAlgorithmName.SHA256,System.Security.Cryptography.RSASignaturePadding.Pkcs1);
      using(var c1=req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(30)))
      using(var c2=req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(30))) {
       var saved=CertRevocation.Checker;var savedNow=CertRevocation.Now;var now=new DateTime(2026,10,14,12,0,0,DateTimeKind.Utc);int calls=0;RevocationVerdict answer=RevocationVerdict.Good;
       try{
        CertRevocation.Forget();CertRevocation.Now=()=>now;CertRevocation.Checker=c=>{calls++;return answer;};
        bool good=!CertRevocation.IsRevoked(c1);
        answer=RevocationVerdict.Revoked;bool cached=!CertRevocation.IsRevoked(c1)&&calls==1;                  // remembered: no second look, still "good"
        bool other=CertRevocation.IsRevoked(c2)&&calls==2;                                                       // another certificate: looked at, revoked
        now=now.AddHours(7);bool expired=CertRevocation.IsRevoked(c1)&&calls==3;                                 // after six hours: looked at again
        answer=RevocationVerdict.Unknown;CertRevocation.Forget();bool unknownOk=!CertRevocation.IsRevoked(c1);
        bool none=!CertRevocation.IsRevoked(null);
        Check(good&&cached&&other&&expired&&unknownOk&&none,"revocation: only an explicit revocation refuses; the answer is remembered six hours per certificate; unknown status and no certificate change nothing");
        answer=RevocationVerdict.Revoked;CertRevocation.Forget();
        bool cbRevoked=!CertRevocation.Callback(null,c1,null,System.Net.Security.SslPolicyErrors.None);
        answer=RevocationVerdict.Good;CertRevocation.Forget();
        bool cbGood=CertRevocation.Callback(null,c1,null,System.Net.Security.SslPolicyErrors.None);
        bool cbErrors=!CertRevocation.Callback(null,c1,null,System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch)&&!CertRevocation.Callback(null,c1,null,System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors);
        Check(cbRevoked&&cbGood&&cbErrors,"revocation: the validation callback keeps every ordinary policy error as a refusal and adds the refusal of a revoked certificate");
       } finally { CertRevocation.Checker=saved;CertRevocation.Now=savedNow;CertRevocation.Forget(); }
       var F=System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.Revoked;
       var U=System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.RevocationStatusUnknown;
       var O=System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.OfflineRevocation;
       var P=System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.PartialChain;
       Check(CertRevocation.Decide(new[]{F|O})==RevocationVerdict.Revoked&&CertRevocation.Decide(new[]{U|P})==RevocationVerdict.Unknown&&CertRevocation.Decide(new[]{O|P})==RevocationVerdict.Unknown&&CertRevocation.Decide(new[]{U|O|F|P})==RevocationVerdict.Revoked,"revocation: combined chain status flags retain revoked precedence and unknown/offline evidence");
       Check(CertRevocation.Decide(new[]{F})==RevocationVerdict.Revoked&&CertRevocation.Decide(new[]{System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.RevocationStatusUnknown})==RevocationVerdict.Unknown&&CertRevocation.Decide(new[]{System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.OfflineRevocation})==RevocationVerdict.Unknown&&CertRevocation.Decide(new[]{System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.RevocationStatusUnknown,F})==RevocationVerdict.Revoked&&CertRevocation.Decide(new[]{System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.PartialChain})==RevocationVerdict.Good&&CertRevocation.Decide(new System.Security.Cryptography.X509Certificates.X509ChainStatusFlags[0])==RevocationVerdict.Good,"revocation: the chain statuses are read as: revoked refuses; unknown or offline does not decide; anything else is not this check's business");
       // the real lookup on a certificate with no revocation address: quick, and never "revoked"
       var sw2=System.Diagnostics.Stopwatch.StartNew();var live=CertRevocation.Live(c1);sw2.Stop();
       Check(live!=RevocationVerdict.Revoked&&sw2.ElapsedMilliseconds<3000,"revocation: the real lookup on a certificate without a revocation address is quick and is not a refusal ("+live+", "+sw2.ElapsedMilliseconds+" ms)");
      }
     }
    }
    // a protected directory is created with its rules in one call: right after creation it has only SYSTEM, Administrators and Users (read), nothing inherited from the parent
    string pd=Path.Combine(Path.GetTempPath(),"agdns-prot-"+Guid.NewGuid().ToString("N"));
    try{
     ScriptStore.CreateProtected(pd);
     var sec=new DirectoryInfo(pd).GetAccessControl();
     var all=sec.GetAccessRules(true,true,typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
     Check(sec.AreAccessRulesProtected&&all.Count==3&&all.All(r=>!r.IsInherited)&&all.Select(r=>r.IdentityReference.Value).OrderBy(x=>x).SequenceEqual(new[]{"S-1-5-18","S-1-5-32-544","S-1-5-32-545"}),"scripts: a protected directory is born with exactly its own rules and none inherited (no window with the parent's user-writable rights)");
    } finally {
     try{var di=new DirectoryInfo(pd);var s2=di.GetAccessControl();s2.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User,System.Security.AccessControl.FileSystemRights.FullControl,System.Security.AccessControl.InheritanceFlags.ContainerInherit|System.Security.AccessControl.InheritanceFlags.ObjectInherit,System.Security.AccessControl.PropagationFlags.None,System.Security.AccessControl.AccessControlType.Allow));di.SetAccessControl(s2);Directory.Delete(pd,true);}catch{}
    }
    // the settings icons: one family, the same size, centred on the tile (centre of the ink within 1 px at 100 % and 125 %)
    var off=new List<string>();var kinds=Enum.GetValues(typeof(IconKind)).Cast<IconKind>().Where(i=>i!=IconKind.None).ToList();
    foreach(float k in new[]{1f,1.25f,1.5f})foreach(var kind in kinds){
     int s=(int)Math.Round(28*k);
     using(var bmp=new Bitmap(s,s))using(var g=Graphics.FromImage(bmp)){
      g.Clear(RowKit.TileFill);RowKit.Icon(g,kind,k,s,true);
      double sw=0,sx=0,sy=0;int minX=s,maxX=0,minY=s,maxY=0;
      for(int y=(int)(4*k);y<s-(int)(4*k);y++)for(int x=(int)(4*k);x<s-(int)(4*k);x++){
       var c=bmp.GetPixel(x,y);int dist=Math.Abs(c.R-RowKit.TileFill.R)+Math.Abs(c.G-RowKit.TileFill.G)+Math.Abs(c.B-RowKit.TileFill.B);
       if(dist>25){sw+=dist;sx+=dist*(x+.5);sy+=dist*(y+.5);minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);}
      }
      double dx=sx/sw-s/2.0,dy=sy/sw-s/2.0;double ext=Math.Max(maxX-minX+1,maxY-minY+1)/k;
      if(Math.Abs(dx)>1||Math.Abs(dy)>1)off.Add(kind+"@"+k*96+" off "+dx.ToString("0.00")+","+dy.ToString("0.00"));
      if(ext<11.5||ext>15.5)off.Add(kind+"@"+k*96+" size "+ext.ToString("0.0"));
     }
    }
    Check(off.Count==0,"icons: every settings icon is centred on its tile (centre of ink within 1 px) and 11.5–15.5 units big at 100/125/150 %"+(off.Count==0?"":" — "+string.Join("; ",off)));
    using(var form=new MainForm(true)) {
     form.Show();Application.DoEvents();
     Check(form.Toggles.Select(t=>t.Icon).Concat(new[]{form.LanguageIconForTest,form.CustomRow.Icon,form.BrowserRowIconForTest,form.InfoRows[0].Icon}).Distinct().Count()==9,"icons: every row of the settings and the patcher row has its own icon (nine different ones)");
     form.Close();
    }
   } finally { L.En=en0; }
  }
 }
}
