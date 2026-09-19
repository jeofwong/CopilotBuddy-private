using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

// Cursor/equipment ownership contract for the real tracked EquipItem behavior and
// AutoEquip2 plugin. Owners are compiled through the production SourceCompiler
// where practical, but never enabled/ticked against a game process. Source
// assertions require stable item/cursor/slot ownership before production repair.
internal static class EquipCursorOwnershipRegressionTests
{
    private sealed class Failure(string message) : Exception(message) { }

    [ModuleInitializer]
    internal static void Run()
    {
        string root = Root();
        string behaviorPath = Path.Combine(root, "runtime-snapshot", "Quest Behaviors", "EquipItem.cs");
        string autoDir = Path.Combine(root, "runtime-snapshot", "Plugins", "AutoEquip2");
        string autoPath = Path.Combine(autoDir, "AutoEquip.cs");
        string behavior = File.ReadAllText(behaviorPath);
        string auto = File.ReadAllText(autoPath);

        var behaviorCompile = CompileSource(behaviorPath);
        var autoCompile = CompileSource(autoDir, addDrawing: true);

        var cases = new List<(string Name, Action Test)>
        {
            ("tracked EquipItem compiles through production source compiler", () =>
            {
                Check(behaviorCompile.Error == null && behaviorCompile.Assembly != null,
                    "EquipItem compile failed: " + (behaviorCompile.Error?.Message ?? string.Join(" | ", behaviorCompile.Messages)));
                Check(behaviorCompile.Assembly!.GetType("Styx.Bot.Quest_Behaviors.EquipItem", false) != null,
                    "compiled assembly does not contain EquipItem owner");
            }),
            ("tracked AutoEquip2 compiles through production source compiler", () =>
            {
                Check(autoCompile.Error == null && autoCompile.Assembly != null,
                    "AutoEquip2 compile failed: " + (autoCompile.Error?.Message ?? string.Join(" | ", autoCompile.Messages)));
                Check(autoCompile.Assembly!.GetType("Styx.Bot.Plugins.AutoEquip2.AutoEquip", false) != null,
                    "compiled assembly does not contain AutoEquip owner");
            }),
            ("EquipItem explicit slot does not derive BagIndex and BagSlot independently", () =>
            {
                string region = MethodRegion(behavior, "protected override Composite CreateBehavior()");
                Check(!region.Contains("item.BagIndex + 1", StringComparison.Ordinal)
                    && !region.Contains("item.BagSlot + 1", StringComparison.Ordinal),
                    "EquipItem explicit-slot path still independently derives container coordinates");
            }),
            ("EquipItem has an explicit pending equip owner", () =>
            {
                Check(behavior.Contains("_pendingEquipGuid", StringComparison.Ordinal)
                    && behavior.Contains("_pendingEquipEntry", StringComparison.Ordinal)
                    && behavior.Contains("_pendingEquipSlot", StringComparison.Ordinal),
                    "EquipItem does not retain exact pending item and equipment-slot ownership");
            }),
            ("EquipItem observes safe pickup success before explicit equip", () =>
            {
                string region = MethodRegion(behavior, "private RunStatus TickPendingEquip()");
                Check(region.Contains("TryPickUp(", StringComparison.Ordinal)
                    && !region.Contains("PickupContainerItem(", StringComparison.Ordinal),
                    "EquipItem explicit-slot path bypasses shared validated pickup");
            }),
            ("EquipItem never clears a foreign cursor", () =>
            {
                Check(!behavior.Contains("ClearCursor", StringComparison.Ordinal),
                    "EquipItem clears a cursor it may not own");
            }),
            ("EquipItem explicit equip validates exact cursor entry", () =>
            {
                Check(behavior.Contains("GetCursorInfo", StringComparison.Ordinal)
                    && behavior.Contains("CursorHasItem", StringComparison.Ordinal)
                    && behavior.Contains("_pendingEquipEntry", StringComparison.Ordinal)
                    && behavior.Contains("EquipCursorItem", StringComparison.Ordinal),
                    "EquipItem does not prove exact cursor item ownership before equip mutation");
            }),
            ("EquipItem completion waits for intended equipment-slot acknowledgement", () =>
            {
                Check(behavior.Contains("_pendingEquipSlot", StringComparison.Ordinal)
                    && (behavior.Contains("Inventory.Equipped", StringComparison.Ordinal)
                        || behavior.Contains("GetInventoryItemID", StringComparison.Ordinal))
                    && behavior.Contains("_pendingEquipGuid", StringComparison.Ordinal),
                    "EquipItem does not acknowledge the exact intended equipment result");
                string region = MethodRegion(behavior, "protected override Composite CreateBehavior()");
                Check(!region.Contains("_isBehaviorDone = true;", StringComparison.Ordinal)
                    || region.Contains("TickPendingEquip", StringComparison.Ordinal),
                    "EquipItem still marks done immediately after fire-and-forget equip submission");
            }),
            ("EquipItem pending transaction has a bounded lifetime", () =>
            {
                Check((behavior.Contains("EquipTimeout", StringComparison.Ordinal)
                        || behavior.Contains("PendingEquipTimeout", StringComparison.Ordinal))
                    && (behavior.Contains("_pendingEquipSince", StringComparison.Ordinal)
                        || behavior.Contains("_pendingEquipStart", StringComparison.Ordinal)),
                    "EquipItem pending equip can remain unresolved indefinitely");
            }),
            ("EquipItem bind confirmation is scoped to its owned equipment slot", () =>
            {
                string region = MethodRegion(behavior, "private void ConfirmOwnedEquipPopup()");
                Check(region.Contains("_pendingEquipSlot == InventorySlot.None", StringComparison.Ordinal)
                    && region.Contains("StaticPopup_FindVisible('EQUIP_BIND')", StringComparison.Ordinal)
                    && region.Contains("StaticPopup_FindVisible('AUTOEQUIP_BIND')", StringComparison.Ordinal)
                    && region.Contains("p.data", StringComparison.Ordinal)
                    && region.Contains("_pendingEquipSlot", StringComparison.Ordinal),
                    "EquipItem can confirm a same-type bind popup without proving the popup slot belongs to its transaction");
            }),
            ("AutoEquip does not clear a foreign cursor", () =>
            {
                Check(!auto.Contains("ClearCursor()", StringComparison.Ordinal),
                    "AutoEquip still clears a cursor before equip");
            }),
            ("AutoEquip does not independently derive bag coordinates for equip", () =>
            {
                string region = MethodRegion(auto, "private void EquipItemIntoSlot");
                string helper = MethodRegion(auto, "private void BeginEquip");
                Check(!region.Contains("item.BagIndex", StringComparison.Ordinal)
                    && !region.Contains("item.BagSlot", StringComparison.Ordinal)
                    && !helper.Contains("PickupContainerItem", StringComparison.Ordinal),
                    "AutoEquip still passes independently observed BagIndex/BagSlot into raw pickup");
            }),
            ("AutoEquip has one pending equip owner", () =>
            {
                Check(auto.Contains("_pendingEquipGuid", StringComparison.Ordinal)
                    && auto.Contains("_pendingEquipEntry", StringComparison.Ordinal)
                    && auto.Contains("_pendingEquipSlot", StringComparison.Ordinal)
                    && (auto.Contains("HasPendingEquip", StringComparison.Ordinal)
                        || auto.Contains("_pendingEquipGuid != 0", StringComparison.Ordinal)),
                    "AutoEquip has no single pending item/slot ownership boundary");
            }),
            ("AutoEquip exact equip mutation validates owned cursor entry", () =>
            {
                Check(auto.Contains("GetCursorInfo", StringComparison.Ordinal)
                    && auto.Contains("CursorHasItem", StringComparison.Ordinal)
                    && auto.Contains("EquipCursorItem", StringComparison.Ordinal)
                    && auto.Contains("_pendingEquipEntry", StringComparison.Ordinal),
                    "AutoEquip does not validate exact owned cursor item before EquipCursorItem");
            }),
            ("AutoEquip does not click a generic popup", () =>
            {
                Check(!auto.Contains("StaticPopup1Button1", StringComparison.Ordinal),
                    "AutoEquip still confirms an arbitrary visible StaticPopup1");
            }),
            ("AutoEquip bind confirmation is scoped to its owned equipment slot", () =>
            {
                string region = MethodRegion(auto, "private void ConfirmOwnedEquipPopup()");
                Check(region.Contains("_pendingEquipSlot == InventorySlot.None", StringComparison.Ordinal)
                    && region.Contains("StaticPopup_FindVisible('EQUIP_BIND')", StringComparison.Ordinal)
                    && region.Contains("StaticPopup_FindVisible('AUTOEQUIP_BIND')", StringComparison.Ordinal)
                    && region.Contains("p.data", StringComparison.Ordinal)
                    && region.Contains("_pendingEquipSlot", StringComparison.Ordinal),
                    "AutoEquip can confirm a same-type bind popup without proving the popup slot belongs to its transaction");
            }),
            ("AutoEquip requires equipment acknowledgement before starting another equip", () =>
            {
                Check(auto.Contains("_pendingEquipGuid", StringComparison.Ordinal)
                    && (auto.Contains("Inventory.Equipped", StringComparison.Ordinal)
                        || auto.Contains("GetInventoryItemID", StringComparison.Ordinal))
                    && (auto.Contains("TickPendingEquip", StringComparison.Ordinal)
                        || auto.Contains("HasPendingEquip", StringComparison.Ordinal)),
                    "AutoEquip has no pending acknowledgement gate between equip requests");
            }),
            ("AutoEquip pending transaction has bounded lifecycle and safe disposal", () =>
            {
                Check((auto.Contains("EquipTimeout", StringComparison.Ordinal)
                        || auto.Contains("PendingEquipTimeout", StringComparison.Ordinal))
                    && (auto.Contains("_pendingEquipSince", StringComparison.Ordinal)
                        || auto.Contains("_pendingEquipStart", StringComparison.Ordinal)),
                    "AutoEquip pending equip has no bounded lifetime");
                string dispose = MethodRegion(auto, "public override void Dispose()");
                Check((dispose.Contains("pending", StringComparison.OrdinalIgnoreCase)
                        || dispose.Contains("ResetEquip", StringComparison.Ordinal))
                    && !dispose.Contains("ClearCursor", StringComparison.Ordinal),
                    "AutoEquip disposal does not revoke managed ownership safely");
            }),
            ("first equip repair preserves ammo scoring loot-roll and bag-selection owners", () =>
            {
                Check(auto.Contains("CheckAndEquipAmmo", StringComparison.Ordinal)
                    && auto.Contains("FindBestItem", StringComparison.Ordinal)
                    && auto.Contains("HandleLootRoll", StringComparison.Ordinal)
                    && auto.Contains("FindBestBag", StringComparison.Ordinal),
                    "equip cursor slice unexpectedly removed unrelated AutoEquip owners");
            })
        };

        int passed=0, assertions=0, unexpected=0;
        foreach (var item in cases)
        {
            try { item.Test(); passed++; Console.WriteLine("PASS equip cursor ownership: " + item.Name); }
            catch (Failure error) { assertions++; Console.Error.WriteLine("FAIL equip cursor ownership: " + item.Name + ": " + error.Message); }
            catch (Exception error) { unexpected++; Console.Error.WriteLine("ERROR equip cursor ownership: " + item.Name + ": " + error); }
        }
        Console.WriteLine($"Equip cursor ownership scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; real tracked owner compilation/source contracts; no game attached.");
        if(assertions+unexpected!=0) throw new InvalidOperationException("Equip cursor ownership regression");
    }

    private static (Assembly? Assembly, Exception? Error, List<string> Messages) CompileSource(string path, bool addDrawing=false)
    {
        Assembly? assembly=null;
        Exception? failure=null;
        var messages=new List<string>();
        try
        {
            Type compilerType=typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler", true)!;
            object compiler=Activator.CreateInstance(compilerType,new object[]{path})!;
            if(addDrawing)
            {
                string? drawing=((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                    ?.Split(Path.PathSeparator)
                    .FirstOrDefault(p=>string.Equals(Path.GetFileName(p),"System.Drawing.Common.dll",StringComparison.OrdinalIgnoreCase));
                if(!string.IsNullOrEmpty(drawing))
                    compilerType.GetMethod("AddReference",BindingFlags.Instance|BindingFlags.Public)!
                        .Invoke(compiler,new object[]{drawing});
            }
            var results=(CompilerResults?)compilerType.GetMethod("Compile",BindingFlags.Instance|BindingFlags.Public)!.Invoke(compiler,null);
            if(results==null) messages.Add("SourceCompiler returned no results");
            else
            {
                foreach(CompilerError e in results.Errors) if(!e.IsWarning) messages.Add(e.ToString());
                assembly=compilerType.GetProperty("CompiledAssembly",BindingFlags.Instance|BindingFlags.Public)!.GetValue(compiler) as Assembly;
            }
        }
        catch(Exception e){ failure=Unwrap(e); }
        return (assembly,failure,messages);
    }

    private static Exception Unwrap(Exception error)
    {
        while(error is TargetInvocationException tie && tie.InnerException!=null) error=tie.InnerException;
        return error;
    }

    private static string MethodRegion(string source,string marker)
    {
        int start=source.IndexOf(marker,StringComparison.Ordinal);
        if(start<0) throw new Failure(marker+" is missing");
        int brace=source.IndexOf('{',start);
        if(brace<0) return source.Substring(start,Math.Min(5000,source.Length-start));
        int depth=0;
        for(int i=brace;i<source.Length;i++)
        {
            if(source[i]=='{') depth++;
            else if(source[i]=='}')
            {
                depth--;
                if(depth==0) return source.Substring(start,i-start+1);
            }
        }
        return source.Substring(start);
    }

    private static string Root()
    {
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d!=null;d=d.Parent)
            if(File.Exists(Path.Combine(d.FullName,"CopilotBuddy.csproj"))) return d.FullName;
        throw new Failure("tracked checkout required");
    }

    private static void Check(bool ok,string reason){ if(!ok) throw new Failure(reason); }
}
