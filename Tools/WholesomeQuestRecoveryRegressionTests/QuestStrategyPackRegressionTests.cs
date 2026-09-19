using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

// Strict source-bound special-quest strategy-pack contract.
// Controlled files only; no profile execution, Lua, movement, client or server attached.
internal static class QuestStrategyPackRegressionTests
{
    private sealed class AssertionFailure(string message) : Exception(message) { }

    [ModuleInitializer]
    internal static void Run()
    {
        var cases = new List<(string Name, Action<string> Test)>
        {
            ("missing pack is explicit and empty", root =>
            {
                object pack = Load(Path.Combine(root, "missing.json"), Sha("{\"Quests\":[]}"));
                Check(Value(pack, "Status") == "Missing", "missing strategy pack was not explicit");
                Check(Recipes(pack).Count == 0, "missing pack invented executable recipes");
            }),
            ("valid UseItemOn recipe preserves authoritative identity and conditions", root =>
            {
                string data = "{\"Quests\":[]}";
                string path = Write(root, Pack(data,
                    "{\"QuestId\":2118,\"ObjectiveIndex\":0,\"Kind\":\"UseItemOn\",\"SourceRef\":\"controlled://profile/2118\",\"ItemId\":7586,\"TargetType\":\"Creature\",\"TargetId\":2164,\"TargetState\":\"Alive\",\"Range\":5,\"RequireLos\":true,\"MaxAttempts\":3,\"SuccessEvidence\":\"ObjectiveProgress\"}"));
                object pack = Load(path, Sha(data));
                Check(Value(pack, "Status") == "DeclaredAndBound", "valid strategy pack was not bound");
                IList recipes = Recipes(pack);
                Check(recipes.Count == 1, "valid recipe count changed");
                object recipe = recipes[0]!;
                Check(Value(recipe, "Kind") == "UseItemOn"
                    && Value(recipe, "QuestId") == "2118"
                    && Value(recipe, "ItemId") == "7586"
                    && Value(recipe, "TargetType") == "Creature"
                    && Value(recipe, "TargetId") == "2164"
                    && Value(recipe, "SuccessEvidence") == "ObjectiveProgress",
                    "UseItemOn recipe fields were rewritten or lost");
            }),
            ("wrong client build fails closed", root =>
            {
                string data = "{\"Quests\":[]}";
                string json = Pack(data, "", clientBuild:12341);
                Throws<InvalidDataException>(() => Load(Write(root,json), Sha(data)), "wrong client build was accepted");
            }),
            ("quest data digest mismatch fails closed", root =>
            {
                string data = "{\"Quests\":[]}";
                string json = Pack(data, "", questSha:new string('0',64));
                Throws<InvalidDataException>(() => Load(Write(root,json), Sha(data)), "strategy pack was not bound to quest dataset bytes");
            }),
            ("unsupported source kind is not inferred from realm branding", root =>
            {
                string data = "{\"Quests\":[]}";
                string json = Pack(data, "", sourceKind:"warmane-custom");
                Throws<InvalidDataException>(() => Load(Write(root,json), Sha(data)), "realm label became strategy authority");
            }),
            ("unknown top-level fields are rejected", root =>
            {
                string data = "{\"Quests\":[]}";
                string json = Pack(data, "").TrimEnd('}') + ",\"Credentials\":\"forbidden\"}";
                Throws<InvalidDataException>(() => Load(Write(root,json), Sha(data)), "unknown pack field was silently ignored");
            }),
            ("duplicate quest objective strategy is rejected", root =>
            {
                string data = "{\"Quests\":[]}";
                string r = "{\"QuestId\":2118,\"ObjectiveIndex\":0,\"Kind\":\"UseItemOn\",\"SourceRef\":\"controlled://a\",\"ItemId\":7586,\"TargetType\":\"Creature\",\"TargetId\":2164,\"TargetState\":\"Alive\",\"Range\":5,\"RequireLos\":true,\"MaxAttempts\":3,\"SuccessEvidence\":\"ObjectiveProgress\"}";
                string json = Pack(data, r + "," + r);
                Throws<InvalidDataException>(() => Load(Write(root,json), Sha(data)), "duplicate strategy owners were accepted");
            }),
            ("UseItemOn cannot omit target state range LOS or bounded attempts", root =>
            {
                string data = "{\"Quests\":[]}";
                string r = "{\"QuestId\":2118,\"ObjectiveIndex\":0,\"Kind\":\"UseItemOn\",\"SourceRef\":\"controlled://incomplete\",\"ItemId\":7586,\"TargetType\":\"Creature\",\"TargetId\":2164,\"SuccessEvidence\":\"ObjectiveProgress\"}";
                Throws<InvalidDataException>(() => Load(Write(root,Pack(data,r)), Sha(data)), "incomplete UseItemOn recipe was accepted");
            }),
            ("local invocation count cannot be success authority", root =>
            {
                string data = "{\"Quests\":[]}";
                string r = "{\"QuestId\":2118,\"ObjectiveIndex\":0,\"Kind\":\"UseItemOn\",\"SourceRef\":\"controlled://bad-success\",\"ItemId\":7586,\"TargetType\":\"Creature\",\"TargetId\":2164,\"TargetState\":\"Alive\",\"Range\":5,\"RequireLos\":true,\"MaxAttempts\":3,\"SuccessEvidence\":\"InvocationCount\"}";
                Throws<InvalidDataException>(() => Load(Write(root,Pack(data,r)), Sha(data)), "local repetition count became server credit");
            }),
            ("gossip event requires explicit option bounded attempts and quest progress acknowledgement", root =>
            {
                string data = "{\"Quests\":[]}";
                string r = "{\"QuestId\":999,\"ObjectiveIndex\":0,\"Kind\":\"GossipEvent\",\"SourceRef\":\"controlled://gossip\",\"TargetType\":\"Creature\",\"TargetId\":123,\"GossipOptionIndex\":1,\"Range\":5,\"RequireLos\":true,\"MaxAttempts\":2,\"SuccessEvidence\":\"QuestComplete\"}";
                object pack = Load(Write(root,Pack(data,r)), Sha(data));
                object recipe = Recipes(pack)[0]!;
                Check(Value(recipe,"GossipOptionIndex")=="1" && Value(recipe,"SuccessEvidence")=="QuestComplete",
                    "gossip recipe lost explicit action/acknowledgement");
            }),
            ("escort destination arrival alone is not authoritative success", root =>
            {
                string data = "{\"Quests\":[]}";
                string r = "{\"QuestId\":1000,\"ObjectiveIndex\":0,\"Kind\":\"Escort\",\"SourceRef\":\"controlled://escort\",\"TargetType\":\"Creature\",\"TargetId\":456,\"Range\":20,\"RequireLos\":true,\"MaxAttempts\":2,\"SuccessEvidence\":\"DestinationReached\"}";
                Throws<InvalidDataException>(() => Load(Write(root,Pack(data,r)), Sha(data)), "escort arrival became quest completion authority");
            }),
            ("empty recipe set is allowed but grants no execution", root =>
            {
                string data = "{\"Quests\":[]}";
                object pack = Load(Write(root,Pack(data,"")), Sha(data));
                Check(Value(pack,"Status")=="DeclaredAndBound" && Recipes(pack).Count==0,
                    "empty validated pack invented a strategy");
            })
        };

        int passed=0, assertions=0, unexpected=0;
        foreach (var item in cases)
        {
            string root=Path.Combine(Path.GetTempPath(),"cb-strategy-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { item.Test(root); passed++; Console.WriteLine("PASS quest strategy pack: "+item.Name); }
            catch (AssertionFailure e) { assertions++; Console.Error.WriteLine("FAIL quest strategy pack: "+item.Name+": "+e.Message); }
            catch (Exception e) { unexpected++; Console.Error.WriteLine("ERROR quest strategy pack: "+item.Name+": "+e); }
            finally { Directory.Delete(root,true); }
        }
        Console.WriteLine($"Quest strategy-pack scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; controlled files; no profile/client/server execution.");
        if(assertions+unexpected!=0) throw new InvalidOperationException("Quest strategy-pack regression");
    }

    private static object Load(string path,string questSha)
    {
        Type? type=typeof(WholesomeAQ.DataLoader).Assembly.GetType("WholesomeAQ.QuestStrategyPackLoader",false);
        if(type==null) throw new AssertionFailure("QuestStrategyPackLoader contract is missing");
        MethodInfo? method=type.GetMethod("Load",BindingFlags.Public|BindingFlags.Static,new[]{typeof(string),typeof(string)});
        if(method==null) throw new AssertionFailure("QuestStrategyPackLoader.Load(path,questSha) is missing");
        try { return method.Invoke(null,new object[]{path,questSha}) ?? throw new AssertionFailure("strategy pack result is null"); }
        catch(TargetInvocationException e) when(e.InnerException!=null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static IList Recipes(object pack)
    {
        object? value=pack.GetType().GetProperty("Recipes")?.GetValue(pack);
        return value as IList ?? throw new AssertionFailure("strategy pack Recipes is missing/not list");
    }

    private static string Value(object obj,string property)
    {
        object? value=obj.GetType().GetProperty(property)?.GetValue(obj);
        if(value==null) return "";
        return Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private static string Write(string root,string json)
    {
        string path=Path.Combine(root,"quest_strategies.json");
        File.WriteAllText(path,json,Encoding.UTF8);
        return path;
    }

    private static string Sha(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Pack(string questData,string recipes,int clientBuild=12340,
        string sourceKind="curated-profile",string? questSha=null)
    {
        string q=questSha ?? Sha(questData);
        return "{"+
            "\"Schema\":\"quest-strategy-pack-335-v1\","+
            "\"ClientBuild\":"+clientBuild+","+
            "\"QuestDataSha256\":\""+q+"\","+
            "\"SourceKind\":\""+sourceKind+"\","+
            "\"SourceRevision\":\"controlled-revision\","+
            "\"Recipes\":["+recipes+"]}";
    }

    private static void Throws<T>(Action action,string reason) where T:Exception
    {
        try { action(); } catch(T) { return; }
        throw new AssertionFailure(reason);
    }

    private static void Check(bool ok,string reason)
    {
        if(!ok) throw new AssertionFailure(reason);
    }
}
