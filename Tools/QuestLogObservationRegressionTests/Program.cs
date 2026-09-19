using System.Reflection;
using System.Runtime.ExceptionServices;
using Styx;
using Styx.Logic.Questing;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using Styx.WoWInternals.WoWCache;
using WholesomeAQ;
var cases=new List<(string Name,Action Run)>();
void Test(string name,Action run)=>cases.Add((name,run));
void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
void Accept(uint id=867,bool hydrate=true,int slot=0)
{
 ObjectManager.Wow!.Slots[slot].Id=id;
 if(hydrate)StyxWoW.Cache.Entries[id]=new WoWCache.InfoBlock{Quest=new WoWCache.QuestCacheEntry{Id=id}};
}
object Invoke(object owner,string name,params object[] args)
{
 var method=owner.GetType().GetMethods().SingleOrDefault(m=>m.Name==name&&m.GetParameters().Length==args.Length);
 Check(method!=null,"missing observation contract: "+name);
 try{return method!.Invoke(owner,args)!;}catch(TargetInvocationException e)when(e.InnerException!=null){ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
}
object Capture()=>Invoke(StyxWoW.Me!.QuestLog,"CaptureSnapshot");
T Field<T>(object value,string name)=>(T)(value.GetType().GetProperty(name)?.GetValue(value)??throw new InvalidOperationException("missing snapshot field: "+name));
bool Complete(object snapshot)=>Field<bool>(snapshot,"IsComplete");
bool Current(object snapshot)=>(bool)Invoke(StyxWoW.Me!.QuestLog,"IsSnapshotCurrent",snapshot);
uint[] Ids(object snapshot)=>Field<IEnumerable<uint>>(snapshot,"AcceptedQuestIds").ToArray();
WholesomeAutoQuest Bot()
{
 var bot=new WholesomeAutoQuest();
 bot._dataLoader.Database!.Quests!.Add(new QuestEntry{Id=867,Objectives=new(){new Objective{ItemId=600}}});
 return bot;
}
void NoSale()=>Check(MerchantFrame.Instance.Calls==0,"incomplete or changed quest observation authorized a sale");
void CompletedHistory()=>QuestLog.TryGetAuthoritativeCompletedQuestsForIdentity("fixture","realm",()=>new List<uint>{867},out _);
Test("legacy list visibly omits occupied cache-miss slot (reproduction control)",()=>{Accept(hydrate:false);Check(StyxWoW.Me!.QuestLog.GetQuestId(0)==867&&StyxWoW.Me.QuestLog.GetAllQuests().Count==0,"expected raw occupied identity and absent materialization");});
Test("cache miss preserves accepted identity in completion result",()=>{Accept(hydrate:false);CompletedHistory();var q=StyxWoW.Me!.QuestLog.GetQuestCompletionSnapshot(867);Check(q.IsAccepted&&q.State==QuestCompletionState.Unknown,"accepted cache miss fell back to completed history");});
Test("occupied cache miss cannot authorize sale",()=>{Accept(hydrate:false);Bot().RunSale();NoSale();});
Test("same-count replacement during inventory scan cannot authorize sale",()=>{Accept();Consumable.OnFood=()=>{ObjectManager.Wow!.Slots[0].Id=999;};Bot().RunSale();NoSale();});
Test("hydrated stable accepted quest remains sell-safe",()=>{Accept();Bot().RunSale();Check(MerchantFrame.Instance.Calls==1&&MerchantFrame.Instance.Ids.Contains(600),"working sale/protection changed");});
Test("truly empty stable log still allows ordinary sale",()=>{Bot().RunSale();Check(MerchantFrame.Instance.Calls==1,"empty complete log blocked");});
Test("snapshot distinguishes occupied missing metadata from empty",()=>{Accept(hydrate:false);var s=Capture();Check(Ids(s).SequenceEqual(new uint[]{867})&&!Complete(s)&&Field<bool>(s,"IsIdentityComplete"),"raw identity lost or metadata advertised complete");});
Test("hydration later yields a new complete observation",()=>{Accept(hydrate:false);var old=Capture();Accept();var now=Capture();Check(!Complete(old)&&Complete(now)&&Ids(now).Single()==867,"hydration failed or old snapshot mutated");});
Test("zero-only slots yield complete empty snapshot",()=>{var s=Capture();Check(Complete(s)&&Ids(s).Length==0,"empty slots treated as unknown quests");});
Test("failed accepted ID is snapshotted separately from ready",()=>{Accept();ObjectManager.Wow!.Slots[0].Flags=WoWDescriptorQuestFlags.Failed;var s=Capture();var property=s.GetType().GetProperty("FailedQuestIds");Check(property!=null,"snapshot does not expose raw failed quest IDs");var failed=(IEnumerable<uint>)property!.GetValue(s)!;Check(failed.SequenceEqual(new uint[]{867})&&Field<IEnumerable<uint>>(s,"ReadyQuestIds").Count()==0,"failed quest was lost or conflated with ready");});
Test("same-count IDs changing during cache lookup invalidate capture",()=>{Accept();StyxWoW.Cache.AfterLookup=_=>ObjectManager.Wow!.Slots[0].Id=999;var s=Capture();Check(!Complete(s)&&Ids(s).Contains(867U),"replacement hidden by count or identity discarded");});
Test("same player wrapper with changed descriptor invalidates capture",()=>{Accept();StyxWoW.Cache.AfterLookup=_=>StyxWoW.Me!.Descriptor+=4096;Check(!Complete(Capture()),"descriptor identity replacement accepted");});
Test("player replacement invalidates capture",()=>{Accept();StyxWoW.Cache.AfterLookup=_=>StyxWoW.Me=new LocalPlayer();Check(!Complete(Capture()),"player owner replacement accepted");});
Test("same wrapper with changed GUID invalidates capture",()=>{Accept();StyxWoW.Cache.AfterLookup=_=>StyxWoW.Me!.Guid++;Check(!Complete(Capture()),"GUID change hidden by object reference");});
Test("duplicate occupied IDs are not a complete log",()=>{Accept();Accept(slot:1);Check(!Complete(Capture()),"duplicate identities authorized scheduling");});
Test("invalid occupied uint identity is retained but never complete",()=>{Accept(uint.MaxValue);var s=Capture();Check(!Complete(s)&&Ids(s).Contains(uint.MaxValue),"invalid raw identity was silently dropped");});
Test("changed completion flags invalidate capture",()=>{Accept();StyxWoW.Cache.AfterLookup=_=>ObjectManager.Wow!.Slots[0].Flags=WoWDescriptorQuestFlags.Completed;Check(!Complete(Capture()),"progress changed during materialization");});
Test("changed failed flag invalidates capture",()=>{Accept();StyxWoW.Cache.AfterLookup=_=>ObjectManager.Wow!.Slots[0].Flags=WoWDescriptorQuestFlags.Failed;Check(!Complete(Capture()),"failed status changed during materialization without invalidating the snapshot");});
Test("read failure is incomplete rather than empty",()=>{ObjectManager.Wow!.Failure=new InvalidOperationException("read failure");Check(!Complete(Capture()),"failed read authorized empty-log semantics");});
Test("cache read exception preserves raw identities",()=>{Accept();StyxWoW.Cache.Failure=new InvalidOperationException("cache unavailable");var s=Capture();Check(!Complete(s)&&Ids(s).Contains(867U),"cache failure discarded observed occupied identity");});
Test("snapshot bypasses thread-local memory cache and restores setting",()=>{Accept();var memory=ObjectManager.Wow!;Capture();Check(memory.CacheEnabled&&!memory.ReadWhileCached,"stale read cache hid a changing quest log");});
Test("revalidation detects new acceptance without hydrating again",()=>{Accept();var s=Capture();int n=StyxWoW.Cache.Lookups;Accept(999,slot:1);Check(!Current(s)&&StyxWoW.Cache.Lookups==n,"revalidation failed or rematerialized unnecessarily");});
Test("revalidation detects different memory/session owner",()=>{Accept();var s=Capture();ObjectManager.Wow=new GreenMagic.Memory();Accept();Check(!Current(s),"memory/session owner replaced");});
Test("snapshot collections cannot be modified through list interface",()=>{Accept();var s=Capture();var ids=Field<IEnumerable<uint>>(s,"AcceptedQuestIds");if(ids is IList<uint> list){try{list[0]=999;throw new InvalidOperationException("mutable snapshot");}catch(NotSupportedException){}}Check(Ids(s).Single()==867,"snapshot mutated");});
Test("capture cancellation propagates",()=>{Accept();StyxWoW.Cache.Failure=new System.Threading.ThreadInterruptedException();try{Capture();throw new InvalidOperationException("cancellation swallowed");}catch(System.Threading.ThreadInterruptedException){} });
Test("cache hydration timeout does not invent completion",()=>{Accept(hydrate:false);StyxWoW.Cache.Failure=new TimeoutException();var s=Capture();Check(!Complete(s)&&Ids(s).Contains(867U),"timeout erased identity");});
Test("seeded hydration/accept/abandon observations match raw identity model",()=>{var random=new Random(12340);for(int i=0;i<250;i++){int slot=random.Next(5);uint id=(uint)(1000+slot);ObjectManager.Wow!.Slots[slot].Id=random.Next(2)==0?0:id;if(random.Next(2)==0)StyxWoW.Cache.Entries.Remove(id);else StyxWoW.Cache.Entries[id]=new(){Quest=new(){Id=id}};var s=Capture();var raw=ObjectManager.Wow.Slots.Where(q=>q.Id!=0).Select(q=>q.Id).Order().ToArray();Check(Ids(s).Order().SequenceEqual(raw),"sequence lost raw IDs at "+i);Check(Complete(s)==raw.All(StyxWoW.Cache.Entries.ContainsKey),"sequence confused hydration at "+i);Check(ObjectManager.Wow.ArrayReads<=(i+1)*4,"unbounded retry loop");}});
int failed=0;
foreach(var test in cases)
{
 ObjectManager.Me=new();ObjectManager.Wow=new();ObjectManager.IsInGame=true;ObjectManager.Executor=null;StyxWoW.Cache=new();
 QuestLog.GetCompletedQuestCacheStatusForIdentity(null!,null!);
 MerchantFrame.Instance=new();Consumable.OnFood=null;
 try{test.Run();Console.WriteLine("PASS observation: "+test.Name);}catch(Exception e){failed++;Console.WriteLine("FAIL observation: "+test.Name+": "+e.GetType().Name+": "+e.Message);}
}
Console.WriteLine($"Quest-log observation scenarios: {cases.Count-failed}/{cases.Count}; full production QuestLog/PlayerQuest, actual sale method, controlled external boundaries; no client attached.");
Environment.ExitCode=failed==0?0:1;
