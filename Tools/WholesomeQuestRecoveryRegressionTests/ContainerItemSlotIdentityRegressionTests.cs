using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Styx.WoWInternals.WoWObjects;

// Shared container-slot safety contract. Pure helpers use controlled GUID arrays;
// source-wiring assertions ensure the public owner no longer submits BagIndex/BagSlot
// independently. No Lua, inventory mutation, cursor action or game process is used.
internal static class ContainerItemSlotIdentityRegressionTests
{
    private sealed class Failure(string message):Exception(message){}
    private const BindingFlags Hidden =
        BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;

    [ModuleInitializer]
    internal static void Run()
    {
        MethodInfo? resolve=typeof(WoWItem).GetMethod("TryResolveContainerLocation",Hidden);
        MethodInfo? lua=typeof(WoWItem).GetMethod("BuildValidatedContainerUseLua",Hidden);
        MethodInfo? pickupLua=typeof(WoWItem).GetMethod("BuildValidatedContainerPickupLua",Hidden);
        MethodInfo? questInfoLua=typeof(WoWItem).GetMethod("BuildValidatedContainerQuestInfoLua",Hidden);

        var cases=new List<(string Name,Action Test)>
        {
            ("shared GUID resolver and validated Lua builder exist",()=>{
                Check(resolve!=null&&lua!=null,
                    "WoWItem lacks a shared slot-identity submission boundary");
            }),
            ("backpack item resolves to Lua bag0 and one-based slot",()=>{
                int bag,slot;
                Check(Resolve(resolve,22,new ulong[]{11,22,0},Array.Empty<ulong[]>(),out bag,out slot)
                    && bag==0&&slot==2,
                    "backpack GUID did not resolve to bag0/slot2");
            }),
            ("equipped bag item resolves to WoW Lua bag numbering",()=>{
                int bag,slot;
                var bags=new[]{new ulong[]{0,33},new ulong[]{44},Array.Empty<ulong>(),new ulong[]{55}};
                Check(Resolve(resolve,33,Array.Empty<ulong>(),bags,out bag,out slot)
                    && bag==1&&slot==2,"first physical bag did not map to Lua bag1");
                Check(Resolve(resolve,55,Array.Empty<ulong>(),bags,out bag,out slot)
                    && bag==4&&slot==1,"fourth physical bag did not map to Lua bag4");
            }),
            ("missing item never aliases backpack bag0 slot0",()=>{
                int bag,slot;
                Check(!Resolve(resolve,99,new ulong[]{11,22},new[]{new ulong[]{33}},out bag,out slot)
                    && bag==-1&&slot==-1,
                    "unresolved GUID became a usable container position");
            }),
            ("zero GUID is never a container identity",()=>{
                int bag,slot;
                Check(!Resolve(resolve,0,new ulong[]{0,0},new[]{new ulong[]{0}},out bag,out slot),
                    "empty slot GUID became an item identity");
            }),
            ("duplicate GUID snapshot fails closed",()=>{
                int bag,slot;
                Check(!Resolve(resolve,22,new ulong[]{22},new[]{new ulong[]{22}},out bag,out slot),
                    "non-atomic duplicate observation selected one arbitrary slot");
            }),
            ("validated Lua submission checks expected entry before use",()=>{
                string code=BuildLua(lua,2,3,7586);
                Check(code.Contains("GetContainerItemLink(2,3)",StringComparison.Ordinal)
                    && code.Contains("7586",StringComparison.Ordinal)
                    && code.Contains("UseContainerItem(2,3)",StringComparison.Ordinal)
                    && code.IndexOf("GetContainerItemLink",StringComparison.Ordinal)
                       < code.IndexOf("UseContainerItem",StringComparison.Ordinal),
                    "Lua owner does not validate the observed slot before item use");
            }),
            ("public UseContainerItem no longer submits independent BagIndex and BagSlot reads",()=>{
                string source=File.ReadAllText(Path.Combine(Root(),
                    "Styx","WoWInternals","WoWObjects","WoWItem.cs"));
                int start=source.IndexOf("public void UseContainerItem()",StringComparison.Ordinal);
                Check(start>=0,"public UseContainerItem owner is missing");
                int end=source.IndexOf("public void PickUp()",start,StringComparison.Ordinal);
                if(end<0)end=Math.Min(source.Length,start+1600);
                string region=source.Substring(start,end-start);
                Check(region.Contains("TryUseContainerItem",StringComparison.Ordinal)
                    && !region.Contains("BagIndex + 1",StringComparison.Ordinal)
                    && !region.Contains("BagSlot + 1",StringComparison.Ordinal),
                    "public owner still derives the two slot coordinates independently");
            }),
            ("live submission boundary revalidates GUID before Lua entry check",()=>{
                string source=File.ReadAllText(Path.Combine(Root(),
                    "Styx","WoWInternals","WoWObjects","WoWItem.cs"));
                int start=source.IndexOf("public bool TryUseContainerItem()",StringComparison.Ordinal);
                Check(start>=0,"TryUseContainerItem owner is missing");
                string region=source.Substring(start,Math.Min(3200,source.Length-start));
                int revalidate=region.IndexOf("IsContainerLocationCurrent",StringComparison.Ordinal);
                int luaCall=region.IndexOf("Lua.GetReturnVal<bool>",StringComparison.Ordinal);
                Check(revalidate>=0&&luaCall>revalidate,
                    "container GUID slot is not revalidated before Lua submission");
            }),
            ("validated quest-item info Lua builder exists",()=>{
                Check(questInfoLua!=null,
                    "WoWItem lacks a validated GetContainerItemQuestInfo boundary");
            }),
            ("quest-item info validates expected entry before original API query",()=>{
                string code=BuildQuestInfoLua(questInfoLua,2,3,7586);
                int link=code.IndexOf("GetContainerItemLink(2,3)",StringComparison.Ordinal);
                int expected=code.IndexOf("7586",StringComparison.Ordinal);
                int query=code.IndexOf("GetContainerItemQuestInfo(2,3)",StringComparison.Ordinal);
                Check(link>=0&&expected>link&&query>expected,
                    "quest-item query reads an unverified container slot");
                Check(code.Contains("isQuestItem",StringComparison.Ordinal)
                    && code.Contains("questId",StringComparison.Ordinal)
                    && code.Contains("isActive",StringComparison.Ordinal),
                    "quest-item query does not preserve original 3.3.5 return semantics");
            }),
            ("TryGetContainerItemQuestInfo retains validated GUID slot identity",()=>{
                string source=File.ReadAllText(Path.Combine(Root(),
                    "Styx","WoWInternals","WoWObjects","WoWItem.cs"));
                int start=source.IndexOf("public bool TryGetContainerItemQuestInfo",StringComparison.Ordinal);
                Check(start>=0,"TryGetContainerItemQuestInfo owner is missing");
                string region=source.Substring(start,Math.Min(4200,source.Length-start));
                int resolveCall=region.IndexOf("TryResolveContainerLocation",StringComparison.Ordinal);
                int revalidate=region.IndexOf("IsContainerLocationCurrent",StringComparison.Ordinal);
                int builder=region.IndexOf("BuildValidatedContainerQuestInfoLua",StringComparison.Ordinal);
                int returns=region.IndexOf("Lua.GetReturnValues",StringComparison.Ordinal);
                Check(resolveCall>=0&&revalidate>resolveCall&&builder>revalidate&&returns>builder,
                    "quest-item info does not keep one GUID/slot identity through the Lua query");
            }),
            ("validated pickup Lua builder exists",()=>{
                Check(pickupLua!=null,"WoWItem lacks a validated single-item pickup Lua boundary");
            }),
            ("pickup refuses every pre-existing cursor payload without clearing it",()=>{
                string code=BuildPickupLua(pickupLua,2,3,7586);
                int cursor=code.IndexOf("GetCursorInfo",StringComparison.Ordinal);
                int pickup=code.IndexOf("PickupContainerItem",StringComparison.Ordinal);
                Check(cursor>=0&&pickup>cursor
                    && !code.Contains("ClearCursor",StringComparison.Ordinal),
                    "single-item pickup does not fail closed on caller-owned cursor state");
            }),
            ("pickup validates expected entry before container mutation",()=>{
                string code=BuildPickupLua(pickupLua,2,3,7586);
                int link=code.IndexOf("GetContainerItemLink(2,3)",StringComparison.Ordinal);
                int expected=code.IndexOf("7586",StringComparison.Ordinal);
                int pickup=code.IndexOf("PickupContainerItem(2,3)",StringComparison.Ordinal);
                Check(link>=0&&expected>link&&pickup>expected,
                    "pickup mutates the slot before validating its expected item entry");
            }),
            ("pickup requires cursor-item acknowledgement after mutation",()=>{
                string code=BuildPickupLua(pickupLua,2,3,7586);
                int pickup=code.IndexOf("PickupContainerItem(2,3)",StringComparison.Ordinal);
                int ack=code.IndexOf("CursorHasItem",StringComparison.Ordinal);
                Check(pickup>=0&&ack>pickup,
                    "pickup does not require an original-client cursor item acknowledgement");
            }),
            ("pickup acknowledgement confirms expected cursor item entry",()=>{
                string code=BuildPickupLua(pickupLua,2,3,7586);
                int pickup=code.IndexOf("PickupContainerItem(2,3)",StringComparison.Ordinal);
                int cursor=pickup<0 ? -1 : code.IndexOf("GetCursorInfo",pickup,StringComparison.Ordinal);
                string tail=cursor<0 ? "" : code.Substring(cursor);
                Check(cursor>pickup
                    && tail.Contains("7586",StringComparison.Ordinal)
                    && (tail.Contains("'item'",StringComparison.Ordinal) || tail.Contains("\"item\"",StringComparison.Ordinal)),
                    "post-pickup acknowledgement does not prove the expected item entry owns the cursor");
            }),
            ("pickup builder is distinct from item-use submission",()=>{
                string code=BuildPickupLua(pickupLua,2,3,7586);
                Check(!code.Contains("UseContainerItem",StringComparison.Ordinal),
                    "cursor pickup accidentally reused item-use mutation semantics");
            }),
            ("TryPickUp resolves and revalidates GUID before Lua pickup",()=>{
                string source=File.ReadAllText(Path.Combine(Root(),
                    "Styx","WoWInternals","WoWObjects","WoWItem.cs"));
                int start=source.IndexOf("public bool TryPickUp()",StringComparison.Ordinal);
                Check(start>=0,"TryPickUp owner is missing");
                string region=source.Substring(start,Math.Min(3400,source.Length-start));
                int resolveCall=region.IndexOf("TryResolveContainerLocation",StringComparison.Ordinal);
                int revalidate=region.IndexOf("IsContainerLocationCurrent",StringComparison.Ordinal);
                int builder=region.IndexOf("BuildValidatedContainerPickupLua",StringComparison.Ordinal);
                int luaCall=region.IndexOf("Lua.GetReturnVal<bool>",StringComparison.Ordinal);
                Check(resolveCall>=0&&revalidate>resolveCall&&builder>revalidate&&luaCall>builder,
                    "TryPickUp does not retain one validated GUID/slot identity through Lua submission");
            }),
            ("public PickUp delegates without independent BagIndex BagSlot reads",()=>{
                string source=File.ReadAllText(Path.Combine(Root(),
                    "Styx","WoWInternals","WoWObjects","WoWItem.cs"));
                int start=source.IndexOf("public void PickUp()",StringComparison.Ordinal);
                Check(start>=0,"public PickUp owner is missing");
                int end=source.IndexOf("private static bool UseItem",start,StringComparison.Ordinal);
                if(end<0)end=Math.Min(source.Length,start+1800);
                string region=source.Substring(start,end-start);
                Check(region.Contains("TryPickUp()",StringComparison.Ordinal)
                    && !region.Contains("BagIndex + 1",StringComparison.Ordinal)
                    && !region.Contains("BagSlot + 1",StringComparison.Ordinal)
                    && !region.Contains("Lua.DoString(\"PickupContainerItem",StringComparison.Ordinal),
                    "public PickUp still derives or mutates an unverified container slot");
            })
        };

        int pass=0,assertions=0,unexpected=0;
        foreach(var c in cases)
        {
            try{c.Test();pass++;Console.WriteLine("PASS container slot identity: "+c.Name);}
            catch(Failure e){assertions++;Console.Error.WriteLine("FAIL container slot identity assertion: "+c.Name+": "+e.Message);}
            catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR container slot identity fixture: "+c.Name+": "+e);}
        }
        Console.WriteLine($"Container slot identity scenarios: {pass}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; pure GUID snapshots/source wiring; no Lua/game attached.");
        if(assertions+unexpected!=0)throw new InvalidOperationException("Container slot identity regressions");
    }

    private static bool Resolve(MethodInfo? method,ulong guid,ulong[] backpack,ulong[][] bags,out int bag,out int slot)
    {
        if(method==null)throw new Failure("TryResolveContainerLocation is missing");
        object?[] args={guid,backpack,bags,-1,-1};
        bool ok=(bool)(method.Invoke(null,args)??false);
        bag=(int)args[3]!;slot=(int)args[4]!;
        return ok;
    }

    private static string BuildLua(MethodInfo? method,int bag,int slot,uint entry)
    {
        if(method==null)throw new Failure("BuildValidatedContainerUseLua is missing");
        return Convert.ToString(method.Invoke(null,new object[]{bag,slot,entry}),
            System.Globalization.CultureInfo.InvariantCulture)??"";
    }

    private static string BuildQuestInfoLua(MethodInfo? method,int bag,int slot,uint entry)
    {
        if(method==null)throw new Failure("BuildValidatedContainerQuestInfoLua is missing");
        return Convert.ToString(method.Invoke(null,new object[]{bag,slot,entry}),
            System.Globalization.CultureInfo.InvariantCulture)??"";
    }

    private static string BuildPickupLua(MethodInfo? method,int bag,int slot,uint entry)
    {
        if(method==null)throw new Failure("BuildValidatedContainerPickupLua is missing");
        return Convert.ToString(method.Invoke(null,new object[]{bag,slot,entry}),
            System.Globalization.CultureInfo.InvariantCulture)??"";
    }

    private static string Root()
    {
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d!=null;d=d.Parent)
            if(File.Exists(Path.Combine(d.FullName,"CopilotBuddy.csproj")))return d.FullName;
        throw new Failure("tracked checkout required");
    }

    private static void Check(bool ok,string why){if(!ok)throw new Failure(why);}
}
