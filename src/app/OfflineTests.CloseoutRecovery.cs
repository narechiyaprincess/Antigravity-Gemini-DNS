using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace AntigravityGeminiDns {
 public static partial class OfflineTests {
  sealed class CloseoutRecoveryFixture:IAssistedBackend,IAssistedRecoveryBackend {
   internal readonly AssistedFixture Inner=new(){Child=new(1460,"NO_MATCHING_OPERATION_RECEIPT",false,true)};
   internal readonly List<string> Events=new();internal bool Quiet,Authority=true;internal Action Capturing;
   public Task<bool> RecoveryQuiescent(CancellationToken token){Events.Add("Child:"+Quiet);return Task.FromResult(Quiet);}
   public bool RecoveryAuthority(AssistedSnapshot s){Events.Add("Authority:"+Authority);return Authority;}
   public Task<AssistedSnapshot> Capture(AssistedRequest r,CancellationToken token){Capturing?.Invoke();Events.Add("Capture:"+Inner.State.Phase+":"+Inner.State.Network+":"+Inner.State.Revision);return Inner.Capture(r,token);}
   public Task<AssistedProof> Direct(AssistedRequest r,AssistedSnapshot s,CancellationToken token)=>Inner.Direct(r,s,token);
   public Task<AssistedPrepared> Prepare(AssistedRequest r,AssistedSnapshot s,CancellationToken token)=>Inner.Prepare(r,s,token);
   public Task<AssistedChild> Apply(AssistedPrepared p,CancellationToken token){Events.Add("Apply");return Inner.Apply(p,token);}
   public Task<AssistedChild> Disable(AssistedRequest r,AssistedSnapshot s,CancellationToken token){Events.Add("Restore:"+s.Revision);return Inner.Disable(r,s,token);}
   public Task<AssistedProof> Verify(AssistedRequest r,AssistedSnapshot s,CancellationToken token){Events.Add("Verify:"+s.Phase);return Inner.Verify(r,s,token);}
   public Task<AssistedProof> Recover(AssistedPrepared p,CancellationToken token)=>Inner.Recover(p,token);
  }
  static void RunCloseoutRecoveryContracts() {
   foreach(string scenario in new[]{"live","ended-missing-receipt","ambiguous-journal","network-changed","revision-changed","already-restored"}) {
    var f=new CloseoutRecoveryFixture();var c=new AssistedCoordinator(f,()=>f.Inner.Now);var request=AssistedRequestFixture();
    var first=c.Run(request,AcceptAssisted).GetAwaiter().GetResult();
    Check(first.Outcome==AssistedOutcome.RecoveryRequired&&c.RecoveryPending,"closeout uncertain apply latches "+scenario);
    f.Quiet=scenario!="live";f.Authority=scenario!="ambiguous-journal";
    if(scenario=="network-changed")f.Inner.State=f.Inner.State with{Network="network-b"};
    if(scenario=="revision-changed"){int captures=0;f.Capturing=()=>{if(++captures==2)f.Inner.State=f.Inner.State with{Revision="external-edit"};};}
    if(scenario=="already-restored")f.Inner.State=f.Inner.State with{Phase="Restored",Active=false};
    f.Inner.Child=new(0,"RESTORED_RECEIPT",true,true);
    var retry=c.Run(request with{Mode=AssistedMode.Disable},null).GetAwaiter().GetResult();
    bool allowed=scenario=="ended-missing-receipt"||scenario=="already-restored";
    Check(allowed?retry.Outcome==AssistedOutcome.DisabledVerified&&!c.RecoveryPending:retry.Outcome==AssistedOutcome.RecoveryRequired&&c.RecoveryPending,"closeout same GUI recovery truth "+scenario);
    Check(f.Inner.Disables==(scenario=="ended-missing-receipt"?1:0),"closeout Restore admission "+scenario);
    if(allowed) {
     int disables=f.Inner.Disables;var repeated=c.Run(request with{Mode=AssistedMode.Disable},null).GetAwaiter().GetResult();
     Check(repeated.Outcome==AssistedOutcome.DisabledVerified&&!repeated.Changed&&f.Inner.Disables==disables,"closeout recovery repeat verified idempotent "+scenario);
    }
    Console.WriteLine("CLOSEOUT_RECOVERY "+scenario+" "+retry.Code+" "+string.Join(" > ",f.Events));
   }
   {
    var f=new CloseoutRecoveryFixture();var c=new AssistedCoordinator(f,()=>f.Inner.Now);var request=AssistedRequestFixture();c.Run(request,AcceptAssisted).GetAwaiter().GetResult();
    f.Quiet=true;int captures=f.Inner.Captures;var connect=c.Run(request,AcceptAssisted).GetAwaiter().GetResult();
    Check(connect.Outcome==AssistedOutcome.RecoveryRequired&&f.Inner.Applies==1&&f.Inner.Captures==captures,"closeout terminal child never silently grants reconnect permission");
   }
   foreach(string journal in new[]{"{}","{\"Journal\":null}","{\"Journal\":{\"Schema\":1,\"Owner\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"Phase\":\"Failed\",\"Rules\":[],\"Doh\":[{\"Ownership\":\"Pending\"}]}}"})
    Check(!Recovery.RetryJournalSafe(new("n","r","b","Failed",false,false,journal)),"closeout malformed or pending journal fails closed");
   Check(Recovery.RetryJournalSafe(new("n","r","b","Restored",false,false,"{\"Journal\":{\"Schema\":1,\"Owner\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"Phase\":\"Restored\",\"Rules\":[],\"Doh\":[]}}")),"closeout terminal protected journal authority recognized");
   foreach(string phase in new[]{"Applying","Restoring","Failed"}){
    var f=new CloseoutRecoveryFixture();var c=new AssistedCoordinator(f,()=>f.Inner.Now);var request=AssistedRequestFixture();c.Run(request,AcceptAssisted).GetAwaiter().GetResult();
    f.Quiet=true;f.Inner.State=f.Inner.State with{Phase=phase,Active=false};f.Inner.Child=new(0,"RESTORED_RECEIPT",true,true);
    var retry=c.Run(request with{Mode=AssistedMode.Disable},null).GetAwaiter().GetResult();
    Check(retry.Outcome==AssistedOutcome.DisabledVerified&&!c.RecoveryPending&&f.Inner.Disables==1,"closeout interrupted owned phase retries guarded Restore in same GUI "+phase);
   }
   var child=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe","-NoProfile -NonInteractive -Command Start-Sleep -Milliseconds 700"){UseShellExecute=false,CreateNoWindow=true});
   Recovery.TrackChild(child);Check(!Recovery.ReleaseCompletedChildren(),"closeout exact live own process handle blocks recovery");child.WaitForExit();
   Check(Recovery.ReleaseCompletedChildren(),"closeout exact completed own process handle permits reconciliation");
  }
 }
}
