using System.Drawing;
namespace AntigravityGeminiDns {
 internal static class DoctorPresentation {
  internal static Color LevelInk(DoctorLevel l)=>l==DoctorLevel.Ok?Theme.Green:l==DoctorLevel.Warning?Theme.Amber:l==DoctorLevel.Fail?Theme.Red:Theme.Muted;

  internal static string NameOf(string id)=>id==Doctor.IdNetwork?L.T("Сеть"):id==Doctor.IdJournal?L.T("Журнал и прерванные изменения"):id==Doctor.IdRules?L.T("Свои правила NRPT"):id==Doctor.IdConflicts?L.T("Чужие правила NRPT"):id==Doctor.IdPort53?L.T("Порт 53"):id==Doctor.IdProvider?L.T("Провайдер (DoH)"):id==Doctor.IdAnswers?L.T("Ответы для защищённых доменов"):id==Doctor.IdTls?L.T("TLS"):id==Doctor.IdVpn?L.T("VPN и TUN-адаптеры"):id==Doctor.IdBrowser?L.T("Secure DNS браузера"):id;

 }
}
