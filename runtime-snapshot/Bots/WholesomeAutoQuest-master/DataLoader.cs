using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Styx.Logic.Questing.Recovery;

#nullable disable

namespace WholesomeAQ
{
    public class DataLoader
    {
        private readonly string _dataFile;
        private QuestDatabase _database;
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public QuestDatabase Database => _database;
        public string DatasetFingerprint { get; private set; } = "unknown";
        public string ExecutionFingerprint { get; private set; } = "unknown";
        public QuestDatasetSourceIdentity DatasetSourceIdentity { get; private set; } = new QuestDatasetSourceIdentity();
        public QuestStrategyPack StrategyPack { get; private set; } = new QuestStrategyPack();

        public DataLoader()
        {
            _dataFile = FindDataFile();
        }

        public DataLoader(string dataFile)
        {
            _dataFile = dataFile ?? throw new ArgumentNullException(nameof(dataFile));
        }

        private static string FindDataFile()
        {
            string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(asmDir))
            {
                string path = Path.Combine(asmDir, "quest_data", "quest_data.json");
                if (File.Exists(path))
                    return path;
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "Bots", "WholesomeAutoQuest-master", "quest_data", "quest_data.json"),
                Path.Combine(baseDir, "Bots", "WholesomeAutoQuest", "quest_data", "quest_data.json"),
                Path.Combine(baseDir, "Plugins", "WholesomeAutoQuester", "quest_data", "quest_data.json"),
                Path.Combine(Environment.CurrentDirectory, "quest_data", "quest_data.json")
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return Path.Combine(Environment.CurrentDirectory, "quest_data", "quest_data.json");
        }

        public QuestDatabase Load()
        {
            if (_database != null)
            {
                PublishDependencies(_database);
                return _database;
            }

            QuestPrerequisiteAuthority.ClearPublishedDependencyAuthority();
            if (!File.Exists(_dataFile))
                return null;

            // Fingerprint the exact snapshots being deserialized/validated, not later file reads.
            // Provenance is optional: no sidecar means the dataset remains explicitly unknown.
            byte[] snapshot = File.ReadAllBytes(_dataFile);
            string provenancePath = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(_dataFile)) ?? Environment.CurrentDirectory,
                "quest_data.provenance.json");
            byte[] provenanceSnapshot = File.Exists(provenancePath)
                ? File.ReadAllBytes(provenancePath)
                : null;
            QuestDatasetSourceIdentity sourceIdentity = provenanceSnapshot == null
                ? new QuestDatasetSourceIdentity()
                : ParseProvenance(provenanceSnapshot, snapshot);
            string strategyPath = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(_dataFile)) ?? Environment.CurrentDirectory,
                "quest_strategies.json");
            QuestStrategyPack strategyPack = QuestStrategyPackLoader.Load(
                strategyPath,
                Digest(snapshot),
                out string strategyContentSha256);
            string fingerprint = FingerprintManifest(
                provenanceSnapshot == null
                    ? new[] { (Role: LogicalRole(_dataFile), Digest: FingerprintDigest(snapshot)) }
                    : new[]
                    {
                        (Role: LogicalRole(_dataFile), Digest: FingerprintDigest(snapshot)),
                        (Role: LogicalRole(provenancePath), Digest: FingerprintDigest(provenanceSnapshot))
                    });
            string json;
            using (var stream = new MemoryStream(snapshot, writable: false))
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                json = reader.ReadToEnd();

            QuestDatabase database = JsonSerializer.Deserialize<QuestDatabase>(json, JsonOptions);
            if (database == null)
                return null;
            if (database.Quests == null || database.Quests.Any(quest => quest == null || quest.PreviousQuestsIds == null))
                throw new InvalidDataException("Quest dependency records cannot be null.");

            // If prerequisite validation/publication throws, a later Load must retry
            // rather than returning a partially initialized cached database.
            PublishDependencies(database);
            DatasetFingerprint = fingerprint;
            ExecutionFingerprint = string.IsNullOrEmpty(strategyContentSha256)
                ? fingerprint
                : CreateExecutionFingerprint(fingerprint, strategyContentSha256);
            DatasetSourceIdentity = sourceIdentity;
            StrategyPack = strategyPack;
            _database = database;
            return _database;
        }

        private static void PublishDependencies(QuestDatabase database)
        {
            var byId = database.Quests
                .Where(quest => quest.Id > 0)
                .GroupBy(quest => quest.Id)
                .ToDictionary(group => group.Key, group => group.First());
            var negativeGroups = database.Quests
                .Where(quest => quest.Id > 0 && quest.ExclusiveGroup < 0)
                .GroupBy(quest => quest.ExclusiveGroup)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(quest => quest.Id).Distinct().OrderBy(id => id).ToArray());

            QuestPrerequisiteAuthority.PublishAuthoritativeDependencies(
                database.Quests.SelectMany(quest =>
                {
                    // PreviousQuestsIds is the dependent-predecessor input. If a
                    // referenced predecessor belongs to a negative ExclusiveGroup,
                    // every known member of that group protects the dependent.
                    var dependent = quest.PreviousQuestsIds
                        .Where(id => id > 0)
                        .SelectMany(id =>
                        {
                            if (byId.TryGetValue(id, out QuestEntry predecessor) &&
                                predecessor.ExclusiveGroup < 0 &&
                                negativeGroups.TryGetValue(predecessor.ExclusiveGroup, out int[] group))
                                return group;
                            return new[] { id };
                        })
                        .Select(id => new QuestDependencyEvidence(
                            unchecked((uint)quest.Id), unchecked((uint)id), false, true));

                    // Direct PrevQuestID stays a direct edge, including the
                    // separately supported signed active-parent form.
                    var direct = quest.PrevQuestID != 0 && quest.PrevQuestID != int.MinValue
                        ? new[]
                        {
                            new QuestDependencyEvidence(
                                unchecked((uint)quest.Id),
                                unchecked((uint)Math.Abs(quest.PrevQuestID)),
                                false,
                                true)
                        }
                        : Array.Empty<QuestDependencyEvidence>();
                    var forward = quest.NextQuestID > 0
                        ? new[]
                        {
                            new QuestDependencyEvidence(
                                unchecked((uint)quest.NextQuestID),
                                unchecked((uint)quest.Id),
                                false,
                                true)
                        }
                        : Array.Empty<QuestDependencyEvidence>();
                    return dependent.Concat(direct).Concat(forward);
                }),
                database.Quests.Where(quest => quest.Id > 0).Select(quest => unchecked((uint)quest.Id)));
        }

        private static QuestDatasetSourceIdentity ParseProvenance(byte[] snapshot, byte[] questDataSnapshot)
        {
            string text;
            using (var stream = new MemoryStream(snapshot, writable: false))
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                text = reader.ReadToEnd();

            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Quest dataset provenance must be a JSON object.");

            string[] required =
            {
                "Schema", "ClientBuild", "SourceCore", "SourceBranch", "CoreRevision",
                "DatabaseRevision", "Exporter", "ExporterVersion", "QuestDataSha256",
                "RealmOverridesDeclared"
            };
            var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Length != required.Length ||
                !required.OrderBy(value => value, StringComparer.Ordinal)
                    .SequenceEqual(names.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal))
                throw new InvalidDataException("Quest dataset provenance has missing or unsupported fields.");

            JsonElement root = document.RootElement;
            string schema = RequiredString(root, "Schema");
            if (schema != "quest-dataset-provenance-v1")
                throw new InvalidDataException("Unsupported quest dataset provenance schema.");

            if (!root.GetProperty("ClientBuild").TryGetInt32(out int clientBuild) || clientBuild != 12340)
                throw new InvalidDataException("Quest dataset provenance requires original client build 12340.");

            string sourceCore = RequiredString(root, "SourceCore");
            if (sourceCore != "trinitycore-3.3.5" && sourceCore != "azerothcore-wotlk")
                throw new InvalidDataException("Quest dataset provenance uses an unsupported source core.");

            string sourceBranch = RequiredString(root, "SourceBranch");
            string coreRevision = RequiredString(root, "CoreRevision");
            string databaseRevision = RequiredString(root, "DatabaseRevision");
            string exporter = RequiredString(root, "Exporter");
            string exporterVersion = RequiredString(root, "ExporterVersion");
            string questDataSha256 = RequiredString(root, "QuestDataSha256").ToLowerInvariant();
            if (questDataSha256.Length != 64 || questDataSha256.Any(value => !Uri.IsHexDigit(value)))
                throw new InvalidDataException("Quest dataset provenance has an invalid quest-data SHA256.");
            if (!string.Equals(questDataSha256, Digest(questDataSnapshot), StringComparison.Ordinal))
                throw new InvalidDataException("Quest dataset provenance does not match the quest-data snapshot.");

            JsonElement overrides = root.GetProperty("RealmOverridesDeclared");
            if (overrides.ValueKind != JsonValueKind.True && overrides.ValueKind != JsonValueKind.False)
                throw new InvalidDataException("RealmOverridesDeclared must be an explicit boolean.");

            return new QuestDatasetSourceIdentity
            {
                Status = QuestDatasetSourceStatus.DeclaredAndBound,
                ClientBuild = clientBuild,
                SourceCore = sourceCore,
                SourceBranch = sourceBranch,
                CoreRevision = coreRevision,
                DatabaseRevision = databaseRevision,
                Exporter = exporter,
                ExporterVersion = exporterVersion,
                QuestDataSha256 = questDataSha256,
                RealmOverridesDeclared = overrides.GetBoolean()
            };
        }

        private static string RequiredString(JsonElement root, string name)
        {
            JsonElement value = root.GetProperty(name);
            if (value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Quest dataset provenance field " + name + " must be text.");
            string result = value.GetString();
            if (string.IsNullOrWhiteSpace(result) || result.Length > 512)
                throw new InvalidDataException("Quest dataset provenance field " + name + " is blank or oversized.");
            return result;
        }

        private static string Digest(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static string FingerprintDigest(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes));

        internal static string CreateDatasetFingerprint(IEnumerable<string> dataFiles)
        {
            if (dataFiles == null)
                throw new ArgumentNullException(nameof(dataFiles));
            return FingerprintManifest(dataFiles.Select(path =>
            {
                string role = LogicalRole(path);
                using (var stream = File.OpenRead(path))
                    return (Role: role, Digest: Convert.ToHexString(SHA256.HashData(stream)));
            }));
        }

        private static string LogicalRole(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A dataset file path is required.", nameof(path));
            string role = Path.GetFileName(path).ToLowerInvariant();
            if (string.IsNullOrEmpty(role))
                throw new InvalidDataException("A dataset file must have a logical filename.");
            return role;
        }

        internal static string CreateExecutionFingerprint(string datasetFingerprint, string strategyContentSha256)
        {
            if (string.IsNullOrWhiteSpace(datasetFingerprint) || datasetFingerprint.Length != 64 ||
                datasetFingerprint.Any(value => !Uri.IsHexDigit(value)))
                throw new InvalidDataException("Dataset fingerprint must be a SHA256 hexadecimal digest.");
            if (string.IsNullOrWhiteSpace(strategyContentSha256) || strategyContentSha256.Length != 64 ||
                strategyContentSha256.Any(value => !Uri.IsHexDigit(value)))
                throw new InvalidDataException("Strategy-pack fingerprint must be a SHA256 hexadecimal digest.");

            string manifest = "quest-execution-content-v1\n"
                + datasetFingerprint.ToLowerInvariant() + "\n"
                + strategyContentSha256.ToLowerInvariant();
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        }

        private static string FingerprintManifest(IEnumerable<(string Role, string Digest)> entries)
        {
            var ordered = entries.OrderBy(entry => entry.Role, StringComparer.Ordinal).ToArray();
            if (ordered.Length == 0)
                throw new InvalidDataException("A dataset manifest must contain at least one file.");
            if (ordered.Select(entry => entry.Role).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
                throw new InvalidDataException("Dataset logical filenames must be unique, regardless of directory.");

            // Versioning intentionally invalidates old metadata-derived fingerprints.
            // Base64 roles and fixed-length digests make separators unambiguous.
            string manifest = "quest-dataset-content-v2\n" + string.Join("\n", ordered.Select(entry =>
                Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Role)) + ":" + entry.Digest));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        }
    }

    public static class QuestStrategyPackLoader
    {
        private static readonly HashSet<string> SourceKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "curated-profile", "trinitycore-3.3.5", "azerothcore-wotlk"
        };

        public static QuestStrategyPack Load(string path, string expectedQuestDataSha256) =>
            Load(path, expectedQuestDataSha256, out _);

        public static QuestStrategyPack Load(
            string path,
            string expectedQuestDataSha256,
            out string contentSha256)
        {
            contentSha256 = "";
            ValidateSha(expectedQuestDataSha256, "expected quest-data SHA256");
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A quest strategy pack path is required.", nameof(path));
            if (!File.Exists(path))
                return new QuestStrategyPack();

            byte[] snapshot = File.ReadAllBytes(path);
            contentSha256 = Convert.ToHexString(SHA256.HashData(snapshot)).ToLowerInvariant();
            string text;
            using (var stream = new MemoryStream(snapshot, writable: false))
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                text = reader.ReadToEnd();

            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Quest strategy pack must be a JSON object.");

            RequireExactFields(root,
                new[] { "Schema", "ClientBuild", "QuestDataSha256", "SourceKind", "SourceRevision", "Recipes" },
                "quest strategy pack");

            if (RequiredString(root, "Schema") != "quest-strategy-pack-335-v1")
                throw new InvalidDataException("Unsupported quest strategy pack schema.");
            if (!root.GetProperty("ClientBuild").TryGetInt32(out int clientBuild) || clientBuild != 12340)
                throw new InvalidDataException("Quest strategy pack requires original client build 12340.");

            string questDataSha256 = RequiredString(root, "QuestDataSha256").ToLowerInvariant();
            ValidateSha(questDataSha256, "QuestDataSha256");
            if (!string.Equals(questDataSha256, expectedQuestDataSha256.ToLowerInvariant(), StringComparison.Ordinal))
                throw new InvalidDataException("Quest strategy pack does not match the quest dataset.");

            string sourceKind = RequiredString(root, "SourceKind");
            if (!SourceKinds.Contains(sourceKind))
                throw new InvalidDataException("Quest strategy pack has an unsupported source kind.");
            string sourceRevision = RequiredString(root, "SourceRevision");

            JsonElement recipesNode = root.GetProperty("Recipes");
            if (recipesNode.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Quest strategy pack Recipes must be an array.");
            if (recipesNode.GetArrayLength() > 4096)
                throw new InvalidDataException("Quest strategy pack has too many recipes.");

            var recipes = new List<QuestStrategyRecipe>();
            var owners = new HashSet<(int QuestId, int ObjectiveIndex)>();
            foreach (JsonElement recipeNode in recipesNode.EnumerateArray())
            {
                QuestStrategyRecipe recipe = ParseRecipe(recipeNode);
                if (!owners.Add((recipe.QuestId, recipe.ObjectiveIndex)))
                    throw new InvalidDataException("Quest strategy pack has duplicate quest/objective owners.");
                recipes.Add(recipe);
            }

            return new QuestStrategyPack
            {
                Status = QuestStrategyPackStatus.DeclaredAndBound,
                ClientBuild = clientBuild,
                QuestDataSha256 = questDataSha256,
                SourceKind = sourceKind,
                SourceRevision = sourceRevision,
                Recipes = recipes
            };
        }

        private static QuestStrategyRecipe ParseRecipe(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Quest strategy recipe must be a JSON object.");

            int questId = RequiredPositiveInt(node, "QuestId");
            int objectiveIndex = RequiredNonNegativeInt(node, "ObjectiveIndex");
            string kindText = RequiredString(node, "Kind");
            if (!Enum.TryParse(kindText, ignoreCase: false, out QuestStrategyKind kind))
                throw new InvalidDataException("Quest strategy recipe has an unsupported Kind.");
            string sourceRef = RequiredString(node, "SourceRef", maximumLength: 1024);
            int targetId = RequiredPositiveInt(node, "TargetId");
            QuestStrategyTargetType targetType = RequiredEnum<QuestStrategyTargetType>(node, "TargetType");
            double range = RequiredFinitePositive(node, "Range", maximum: 100.0);
            bool requireLos = RequiredBool(node, "RequireLos");
            int maxAttempts = RequiredBoundedInt(node, "MaxAttempts", 1, 20);
            QuestStrategySuccessEvidence success = RequiredEnum<QuestStrategySuccessEvidence>(node, "SuccessEvidence");

            QuestStrategyRecipe recipe;
            if (kind == QuestStrategyKind.UseItemOn)
            {
                RequireExactFields(node,
                    new[] { "QuestId", "ObjectiveIndex", "Kind", "SourceRef", "ItemId",
                        "TargetType", "TargetId", "TargetState", "Range", "RequireLos",
                        "MaxAttempts", "SuccessEvidence" },
                    "UseItemOn strategy");
                recipe = new QuestStrategyRecipe
                {
                    QuestId = questId,
                    ObjectiveIndex = objectiveIndex,
                    Kind = kind,
                    SourceRef = sourceRef,
                    ItemId = RequiredPositiveInt(node, "ItemId"),
                    TargetType = targetType,
                    TargetId = targetId,
                    TargetState = RequiredEnum<QuestStrategyTargetState>(node, "TargetState"),
                    Range = range,
                    RequireLos = requireLos,
                    MaxAttempts = maxAttempts,
                    SuccessEvidence = success
                };
            }
            else if (kind == QuestStrategyKind.GossipEvent)
            {
                RequireExactFields(node,
                    new[] { "QuestId", "ObjectiveIndex", "Kind", "SourceRef", "TargetType",
                        "TargetId", "GossipOptionIndex", "Range", "RequireLos", "MaxAttempts",
                        "SuccessEvidence" },
                    "GossipEvent strategy");
                if (targetType != QuestStrategyTargetType.Creature)
                    throw new InvalidDataException("GossipEvent strategy target must be a creature.");
                recipe = new QuestStrategyRecipe
                {
                    QuestId = questId,
                    ObjectiveIndex = objectiveIndex,
                    Kind = kind,
                    SourceRef = sourceRef,
                    TargetType = targetType,
                    TargetId = targetId,
                    GossipOptionIndex = RequiredBoundedInt(node, "GossipOptionIndex", 0, 64),
                    Range = range,
                    RequireLos = requireLos,
                    MaxAttempts = maxAttempts,
                    SuccessEvidence = success
                };
            }
            else
            {
                RequireExactFields(node,
                    new[] { "QuestId", "ObjectiveIndex", "Kind", "SourceRef", "TargetType",
                        "TargetId", "Range", "RequireLos", "MaxAttempts", "SuccessEvidence" },
                    "Escort strategy");
                if (targetType != QuestStrategyTargetType.Creature)
                    throw new InvalidDataException("Escort strategy target must be a creature.");
                recipe = new QuestStrategyRecipe
                {
                    QuestId = questId,
                    ObjectiveIndex = objectiveIndex,
                    Kind = kind,
                    SourceRef = sourceRef,
                    TargetType = targetType,
                    TargetId = targetId,
                    Range = range,
                    RequireLos = requireLos,
                    MaxAttempts = maxAttempts,
                    SuccessEvidence = success
                };
            }

            return recipe;
        }

        private static void RequireExactFields(JsonElement node, IEnumerable<string> expected, string label)
        {
            var required = expected.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var actual = node.EnumerateObject().Select(property => property.Name)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (!required.SequenceEqual(actual, StringComparer.Ordinal))
                throw new InvalidDataException(label + " has missing or unsupported fields.");
        }

        private static string RequiredString(JsonElement node, string name, int maximumLength = 512)
        {
            if (!node.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Quest strategy field " + name + " must be text.");
            string result = value.GetString();
            if (string.IsNullOrWhiteSpace(result) || result.Length > maximumLength)
                throw new InvalidDataException("Quest strategy field " + name + " is blank or oversized.");
            return result;
        }

        private static int RequiredPositiveInt(JsonElement node, string name) =>
            RequiredBoundedInt(node, name, 1, int.MaxValue);

        private static int RequiredNonNegativeInt(JsonElement node, string name) =>
            RequiredBoundedInt(node, name, 0, int.MaxValue);

        private static int RequiredBoundedInt(JsonElement node, string name, int minimum, int maximum)
        {
            if (!node.TryGetProperty(name, out JsonElement value) || !value.TryGetInt32(out int result) ||
                result < minimum || result > maximum)
                throw new InvalidDataException("Quest strategy field " + name + " is outside its supported range.");
            return result;
        }

        private static bool RequiredBool(JsonElement node, string name)
        {
            if (!node.TryGetProperty(name, out JsonElement value) ||
                (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False))
                throw new InvalidDataException("Quest strategy field " + name + " must be boolean.");
            return value.GetBoolean();
        }

        private static double RequiredFinitePositive(JsonElement node, string name, double maximum)
        {
            if (!node.TryGetProperty(name, out JsonElement value) || !value.TryGetDouble(out double result) ||
                double.IsNaN(result) || double.IsInfinity(result) || result <= 0 || result > maximum)
                throw new InvalidDataException("Quest strategy field " + name + " is outside its supported range.");
            return result;
        }

        private static T RequiredEnum<T>(JsonElement node, string name) where T : struct
        {
            string text = RequiredString(node, name);
            if (!Enum.TryParse(text, ignoreCase: false, out T value) || !Enum.IsDefined(typeof(T), value))
                throw new InvalidDataException("Quest strategy field " + name + " has an unsupported value.");
            return value;
        }

        private static void ValidateSha(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64 ||
                value.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException(name + " must be a SHA256 hexadecimal digest.");
        }
    }

}