using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

#nullable disable

namespace WholesomeAQ
{
    public class ProfileBuilder
    {
        private readonly string _profilePath;

        public ProfileBuilder() : this(null) { }

        public ProfileBuilder(string profilePath)
        {
            _profilePath = profilePath;
        }

        public string BuildProfileXml(
            IReadOnlyList<QuestPlanEntry> plan,
            QuestDatabase db,
            string zoneName,
            string playerName,
            int playerLevel,
            List<VendorEntry> vendors = null) =>
            BuildProfileXml(plan, db, zoneName, playerName, playerLevel, vendors, null);

        public string BuildProfileXml(
            IReadOnlyList<QuestPlanEntry> plan,
            QuestDatabase db,
            string zoneName,
            string playerName,
            int playerLevel,
            List<VendorEntry> vendors,
            QuestStrategyPack strategyPack)
        {
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));
            if (db == null)
                throw new ArgumentNullException(nameof(db));

            XDocument doc = new XDocument(
                new XElement("HBProfile",
                    new XElement("Name", $"WholesomeAQ - {zoneName}"),
                    new XElement("MinLevel", 1),
                    new XElement("MaxLevel", 80),
                    new XElement("MinDurability", "0.2"),
                    new XElement("MinFreeBagSlots", "2"),
                    new XElement("AvoidMobs"),
                    new XElement("Blackspots"),
                    new XElement("Mailboxes"),
                    BuildVendorsElement(vendors)
                )
            );

            XElement root = doc.Root;
            XElement questOrder = new XElement("QuestOrder");
            var questDefinitions = new Dictionary<int, XElement>();

            foreach (QuestPlanEntry entry in plan)
            {
                if (entry == null || entry.Quest == null)
                    continue;

                if (!questDefinitions.TryGetValue(entry.Quest.Id, out XElement questDefinition))
                {
                    questDefinition = new XElement("Quest",
                        new XAttribute("Id", entry.Quest.Id),
                        new XAttribute("Name", entry.Quest.Name ?? ""));
                    questDefinitions.Add(entry.Quest.Id, questDefinition);
                    root.Add(questDefinition);
                }

                if (entry.Stage == QuestWorkStage.Pickup)
                {
                    if (entry.Giver == null)
                        continue;
                    if (entry.Hotspots == null || entry.Hotspots.Count == 0)
                        continue;
                    questOrder.Add(BuildPickupGuard(entry));
                    continue;
                }

                if (entry.Stage == QuestWorkStage.TurnIn)
                {
                    if (entry.Ender == null)
                        continue;
                    if (entry.Hotspots == null || entry.Hotspots.Count == 0)
                        continue;
                    questOrder.Add(BuildTurnInGuard(entry));
                    continue;
                }

                if (entry.Stage == QuestWorkStage.Objective ||
                    entry.Stage == QuestWorkStage.AncestorCorrection)
                {
                    QuestObjective objective = entry.Quest.Objectives
                        .FirstOrDefault(value => value.Index == entry.ObjectiveIndex);
                    if (objective == null)
                        continue;
                    if (entry.Hotspots == null || entry.Hotspots.Count == 0)
                        continue;

                    QuestStrategyRecipe strategy = FindUseItemOnStrategy(
                        strategyPack, entry.Quest.Id, entry.ObjectiveIndex);
                    if (strategy != null)
                    {
                        XElement strategyGuard = BuildUseItemOnStrategyGuard(entry, strategy);
                        if (strategyGuard == null)
                            throw new InvalidDataException(
                                $"Quest strategy {entry.Quest.Id}/{entry.ObjectiveIndex} could not be materialized.");
                        questOrder.Add(strategyGuard);
                        continue;
                    }

                    QuestStrategyRecipe gossip = FindGossipEventStrategy(
                        strategyPack, entry.Quest.Id, entry.ObjectiveIndex);
                    if (gossip != null)
                    {
                        XElement gossipGuard = BuildGossipEventStrategyGuard(entry, gossip);
                        if (gossipGuard == null)
                            throw new InvalidDataException(
                                $"Gossip strategy {entry.Quest.Id}/{entry.ObjectiveIndex} could not be materialized.");
                        questOrder.Add(gossipGuard);
                        continue;
                    }

                    XElement definition = BuildObjectiveDefinition(objective, entry.Hotspots);
                    XElement guard = BuildObjectiveGuard(entry);
                    if (definition == null || guard == null)
                        continue;
                    questDefinition.Add(definition);
                    questOrder.Add(guard);
                    continue;
                }

            }

            root.Add(questOrder);

            return doc.Declaration + Environment.NewLine + doc.ToString();
        }

        private static string ProfileRelationType(QuestObjectType type)
        {
            switch (type)
            {
                case QuestObjectType.Creature: return "Npc";
                case QuestObjectType.GameObject: return "GameObject";
                default: throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported quest relation type.");
            }
        }

        private static XElement BuildPickupGuard(QuestPlanEntry entry) =>
            new XElement("If",
                new XAttribute("Condition", $"!HasQuest({entry.Quest.Id}) && !IsQuestCompleted({entry.Quest.Id})"),
                entry.Hotspots.Select(point =>
                    new XElement("PickUp",
                        new XAttribute("QuestName", entry.Quest.Name ?? ""),
                        new XAttribute("QuestId", entry.Quest.Id),
                        new XAttribute("GiverName", entry.Giver.GiverName ?? ""),
                        new XAttribute("GiverId", entry.Giver.GiverId),
                        new XAttribute("GiverType", ProfileRelationType(entry.Giver.GiverType)),
                        LocationAttributes(point))));

        private static QuestStrategyRecipe FindUseItemOnStrategy(
            QuestStrategyPack strategyPack,
            int questId,
            int objectiveIndex)
        {
            if (strategyPack == null ||
                strategyPack.Status != QuestStrategyPackStatus.DeclaredAndBound ||
                strategyPack.Recipes == null)
                return null;

            QuestStrategyRecipe[] matches = strategyPack.Recipes
                .Where(recipe => recipe != null
                    && recipe.Kind == QuestStrategyKind.UseItemOn
                    && recipe.QuestId == questId
                    && recipe.ObjectiveIndex == objectiveIndex)
                .ToArray();
            if (matches.Length > 1)
                throw new InvalidDataException(
                    $"Multiple UseItemOn strategies own quest {questId} objective {objectiveIndex}.");
            return matches.SingleOrDefault();
        }

        private static QuestStrategyRecipe FindGossipEventStrategy(
            QuestStrategyPack strategyPack,
            int questId,
            int objectiveIndex)
        {
            if (strategyPack == null ||
                strategyPack.Status != QuestStrategyPackStatus.DeclaredAndBound ||
                strategyPack.Recipes == null)
                return null;

            QuestStrategyRecipe[] matches = strategyPack.Recipes
                .Where(recipe => recipe != null
                    && recipe.Kind == QuestStrategyKind.GossipEvent
                    && recipe.QuestId == questId
                    && recipe.ObjectiveIndex == objectiveIndex)
                .ToArray();
            if (matches.Length > 1)
                throw new InvalidDataException(
                    $"Multiple GossipEvent strategies own quest {questId} objective {objectiveIndex}.");
            return matches.SingleOrDefault();
        }

        private static XElement BuildUseItemOnStrategyGuard(
            QuestPlanEntry entry,
            QuestStrategyRecipe strategy)
        {
            if (entry?.Quest == null || entry.Hotspots == null || entry.Hotspots.Count == 0 ||
                strategy == null || strategy.ItemId <= 0 || strategy.TargetId <= 0 ||
                strategy.Range <= 0 || strategy.MaxAttempts <= 0)
                return null;

            // The v1 pack records the BelowHp state but not the required percentage.
            // Do not invent a threshold or silently fall back to generic objective work.
            if (strategy.TargetState == QuestStrategyTargetState.BelowHp)
                throw new InvalidDataException(
                    $"UseItemOn strategy {strategy.QuestId}/{strategy.ObjectiveIndex} requires an explicit BelowHp threshold before runtime execution.");

            SpawnPoint anchor = entry.Hotspots[0];
            string mobType = strategy.TargetType == QuestStrategyTargetType.Creature
                ? "Npc"
                : "GameObject";
            XElement behavior = new XElement("CustomBehavior",
                new XAttribute("File", "UseItemOn"),
                new XAttribute("QuestId", strategy.QuestId),
                new XAttribute("ObjectiveIndex", strategy.ObjectiveIndex),
                new XAttribute("ItemId", strategy.ItemId),
                new XAttribute("MobId", strategy.TargetId),
                new XAttribute("MobType", mobType),
                new XAttribute("MobState", strategy.TargetState.ToString()),
                new XAttribute("Range", strategy.Range),
                new XAttribute("RequireLos", strategy.RequireLos),
                new XAttribute("MaxAttempts", strategy.MaxAttempts),
                new XAttribute("SuccessEvidence", strategy.SuccessEvidence.ToString()),
                new XAttribute("WaitForNpcs", true),
                new XAttribute("AcknowledgementTimeout", 5000),
                LocationAttributes(anchor));

            return new XElement("If",
                new XAttribute("Condition", BuildObjectiveAdmissionCondition(entry)),
                BuildFreewindDescentGuard(entry.Hotspots),
                BuildGreatLiftAscentGuard(entry.Hotspots),
                behavior);
        }

        private static XElement BuildGossipEventStrategyGuard(
            QuestPlanEntry entry,
            QuestStrategyRecipe strategy)
        {
            if (entry?.Quest == null || entry.Hotspots == null || entry.Hotspots.Count == 0 ||
                strategy == null || strategy.Kind != QuestStrategyKind.GossipEvent ||
                strategy.TargetType != QuestStrategyTargetType.Creature ||
                strategy.TargetId <= 0 || strategy.GossipOptionIndex < 0 ||
                strategy.Range <= 0 || strategy.MaxAttempts <= 0)
                return null;

            QuestObjective sourceObjective = entry.Quest.Objectives
                .FirstOrDefault(value => value.Index == entry.ObjectiveIndex);
            if (sourceObjective == null || sourceObjective.MobId != strategy.TargetId)
                throw new InvalidDataException(
                    $"GossipEvent strategy {strategy.QuestId}/{strategy.ObjectiveIndex} cannot borrow hotspots from objective target {sourceObjective?.MobId ?? 0}; target {strategy.TargetId} requires matching source location authority.");

            SpawnPoint anchor = entry.Hotspots[0];
            XElement behavior = new XElement("CustomBehavior",
                new XAttribute("File", "GossipEvent"),
                new XAttribute("QuestId", strategy.QuestId),
                new XAttribute("ObjectiveIndex", strategy.ObjectiveIndex),
                new XAttribute("MobId", strategy.TargetId),
                // Strategy-pack indices are zero-based. GossipFrame owns the
                // conversion to WoW Lua's one-based SelectGossipOption argument.
                new XAttribute("GossipOptionIndex", strategy.GossipOptionIndex),
                new XAttribute("Range", strategy.Range),
                new XAttribute("RequireLos", strategy.RequireLos),
                new XAttribute("MaxAttempts", strategy.MaxAttempts),
                new XAttribute("SuccessEvidence", strategy.SuccessEvidence.ToString()),
                new XAttribute("WaitForNpcs", true),
                new XAttribute("AcknowledgementTimeout", 5000),
                new XAttribute("GossipOpenTimeout", 3000),
                new XAttribute("TargetWaitTimeout", 30000),
                new XAttribute("NavigationTimeout", 120000),
                LocationAttributes(anchor));

            return new XElement("If",
                new XAttribute("Condition", BuildObjectiveAdmissionCondition(entry)),
                BuildFreewindDescentGuard(entry.Hotspots),
                BuildGreatLiftAscentGuard(entry.Hotspots),
                behavior);
        }

        private static string BuildObjectiveAdmissionCondition(QuestPlanEntry entry)
        {
            string condition = $"HasQuest({entry.Quest.Id})";
            QuestObjective objective = entry.Quest.Objectives
                .FirstOrDefault(value => value.Index == entry.ObjectiveIndex);
            // Re-read collected items before transport or custom behavior on every
            // entry/restart. The action's tool item is not the collection objective.
            if (objective != null && objective.ItemId > 0 && objective.CollectCount > 0 &&
                (objective.Type == ObjectiveType.CollectItem ||
                 objective.Type == ObjectiveType.CollectFromGameObject))
                condition += $" && GetItemCount({objective.ItemId}) < {objective.CollectCount}";
            return condition;
        }

        private static XElement BuildObjectiveGuard(QuestPlanEntry entry)
        {
            QuestObjective objective = entry.Quest.Objectives
                .FirstOrDefault(value => value.Index == entry.ObjectiveIndex);
            if (objective == null)
                return null;
            XElement node = BuildObjectiveOrderNode(entry.Quest, objective);
            return node == null
                ? null
                : new XElement("If",
                    new XAttribute("Condition", BuildObjectiveAdmissionCondition(entry)),
                    BuildFreewindDescentGuard(entry.Hotspots),
                    BuildGreatLiftAscentGuard(entry.Hotspots),
                    node);
        }

        private static XElement BuildFreewindDescentGuard(IReadOnlyList<SpawnPoint> hotspots)
        {
            bool targetsLowerFreewind = hotspots?.Any(point =>
                point.Map == 1 && point.X > -5500 && point.X < -5000 &&
                point.Y > -2700 && point.Y < -2150 && point.Z < 20) == true;
            if (!targetsLowerFreewind)
                return null;

            return new XElement("If",
                new XAttribute("Condition",
                    "Me.MapId == 1 && Me.Z > 40 && Me.X > -5500 && Me.X < -5300 && Me.Y > -2600 && Me.Y < -2350"),
                new XElement("CustomBehavior",
                    new XAttribute("File", "UseTransport"),
                    new XAttribute("DestName", "Freewind Post lower level"),
                    new XAttribute("TransportId", 11899),
                    new XAttribute("BoardingDockTolerance", "0.5"),
                    new XAttribute("DismountAtWait", false),
                    new XAttribute("RequireObservedDeparture", true),
                    new XAttribute("ApproachAtX", "-5425.65"),
                    new XAttribute("ApproachAtY", "-2448.40"),
                    new XAttribute("ApproachAtZ", "89.28"),
                    new XAttribute("WaitAtX", "-5383.621"),
                    new XAttribute("WaitAtY", "-2486.721"),
                    new XAttribute("WaitAtZ", "89.06526"),
                    new XAttribute("TransportStartX", "-5382.5"),
                    new XAttribute("TransportStartY", "-2489.42"),
                    new XAttribute("TransportStartZ", "89.02528"),
                    new XAttribute("TransportEndX", "-5382.5"),
                    new XAttribute("TransportEndY", "-2489.42"),
                    new XAttribute("TransportEndZ", "-40.5284"),
                    new XAttribute("GetOffX", "-5375.26"),
                    new XAttribute("GetOffY", "-2489.24"),
                    new XAttribute("GetOffZ", "-40.56239")));
        }

        private static XElement BuildGreatLiftAscentGuard(IReadOnlyList<SpawnPoint> hotspots)
        {
            // A northbound destination well beyond the Thousand Needles cliff must
            // use the Great Lift. The ordinary mesh route otherwise walks a very
            // long detour and can strand the player on the cliff face.
            bool targetsNorthOfGreatLift = hotspots?.Any(point =>
                point.Map == 1 && point.X > -4400 && point.Z > 35) == true;
            if (!targetsNorthOfGreatLift)
                return null;

            return new XElement("If",
                new XAttribute("Condition",
                    "Me.MapId == 1 && Me.Z < 20 && Me.X > -5400 && Me.X < -4400 && Me.Y > -2300 && Me.Y < -1300"),
                new XElement("CustomBehavior",
                    new XAttribute("File", "UseTransport"),
                    new XAttribute("DestName", "The Great Lift upper level"),
                    new XAttribute("TransportId", 11898),
                    new XAttribute("BoardingDockTolerance", "1.5"),
                    new XAttribute("DismountAtWait", false),
                    new XAttribute("RequireObservedDeparture", true),
                    new XAttribute("ApproachAtX", "-4720.00"),
                    new XAttribute("ApproachAtY", "-1827.80"),
                    new XAttribute("ApproachAtZ", "-44.10"),
                    new XAttribute("WaitAtX", "-4675.50"),
                    new XAttribute("WaitAtY", "-1827.75"),
                    new XAttribute("WaitAtZ", "-44.10"),
                    new XAttribute("TransportStartX", "-4665.43"),
                    new XAttribute("TransportStartY", "-1827.67"),
                    new XAttribute("TransportStartZ", "-44.14"),
                    new XAttribute("TransportEndX", "-4665.43"),
                    new XAttribute("TransportEndY", "-1827.67"),
                    new XAttribute("TransportEndZ", "85.41"),
                    new XAttribute("GetOffX", "-4654.50"),
                    new XAttribute("GetOffY", "-1827.67"),
                    new XAttribute("GetOffZ", "85.50")));
        }

        private static XElement BuildTurnInGuard(QuestPlanEntry entry) =>
            new XElement("If",
                new XAttribute("Condition", $"HasQuest({entry.Quest.Id})"),
                entry.Hotspots.Select(point =>
                    new XElement("TurnIn",
                        new XAttribute("QuestName", entry.Quest.Name ?? ""),
                        new XAttribute("QuestId", entry.Quest.Id),
                        new XAttribute("TurnInName", entry.Ender.EnderName ?? ""),
                        new XAttribute("TurnInId", entry.Ender.EnderId),
                        new XAttribute("TurnInType", ProfileRelationType(entry.Ender.EnderType)),
                        LocationAttributes(point))));

        private static XElement BuildObjectiveDefinition(
            QuestObjective objective,
            IReadOnlyList<SpawnPoint> hotspots)
        {
            XElement node;
            if (objective.Type == ObjectiveType.KillMob && objective.MobId > 0)
            {
                node = new XElement("Objective",
                    new XAttribute("Type", "KillMob"),
                    new XAttribute("MobId", objective.MobId),
                    new XAttribute("KillCount", objective.KillCount));
            }
            else if (objective.Type == ObjectiveType.CollectItem && objective.ItemId > 0)
            {
                node = new XElement("Objective",
                    new XAttribute("Type", "CollectItem"),
                    new XAttribute("ItemId", objective.ItemId),
                    new XAttribute("CollectCount", objective.CollectCount));
            }
            else if (objective.Type == ObjectiveType.CollectFromGameObject && objective.GameObjectId > 0)
            {
                node = objective.ItemId > 0
                    ? new XElement("Objective",
                        new XAttribute("Type", "CollectItem"),
                        new XAttribute("ItemId", objective.ItemId),
                        new XAttribute("CollectCount", objective.CollectCount),
                        new XElement("CollectFrom",
                            new XElement("GameObject",
                                new XAttribute("Name", objective.GameObjectName ?? $"GameObject_{objective.GameObjectId}"),
                                new XAttribute("Id", objective.GameObjectId))))
                    : new XElement("Objective",
                        new XAttribute("Type", "UseObject"),
                        new XAttribute("ObjectId", objective.GameObjectId),
                        new XAttribute("UseCount", objective.CollectCount));
            }
            else
            {
                return null;
            }

            node.Add(new XElement("Hotspots", hotspots.Select(BuildHotspot)));
            return node;
        }

        private static XElement BuildObjectiveOrderNode(QuestEntry quest, QuestObjective objective)
        {
            if (objective.Type == ObjectiveType.KillMob && objective.MobId > 0)
            {
                return new XElement("Objective",
                    new XAttribute("QuestName", quest.Name ?? ""),
                    new XAttribute("QuestId", quest.Id),
                    new XAttribute("Index", objective.Index),
                    new XAttribute("Type", "KillMob"),
                    new XAttribute("MobId", objective.MobId),
                    new XAttribute("KillCount", objective.KillCount));
            }
            if (objective.Type == ObjectiveType.CollectItem && objective.ItemId > 0)
            {
                var node = new XElement("Objective",
                    new XAttribute("QuestName", quest.Name ?? ""),
                    new XAttribute("QuestId", quest.Id),
                    new XAttribute("Index", objective.Index),
                    new XAttribute("Type", "CollectItem"),
                    new XAttribute("ItemId", objective.ItemId),
                    new XAttribute("CollectCount", objective.CollectCount));
                if (objective.MobId > 0)
                    node.Add(new XAttribute("MobId", objective.MobId));
                return node;
            }
            if (objective.Type == ObjectiveType.CollectFromGameObject && objective.GameObjectId > 0)
            {
                XElement node = new XElement("Objective",
                    new XAttribute("QuestName", quest.Name ?? ""),
                    new XAttribute("QuestId", quest.Id),
                    new XAttribute("Index", objective.Index));
                if (objective.ItemId > 0)
                {
                    node.Add(
                        new XAttribute("Type", "CollectItem"),
                        new XAttribute("ItemId", objective.ItemId),
                        new XAttribute("CollectCount", objective.CollectCount));
                }
                else
                {
                    node.Add(
                        new XAttribute("Type", "UseObject"),
                        new XAttribute("ObjectId", objective.GameObjectId),
                        new XAttribute("UseCount", objective.CollectCount));
                }
                return node;
            }
            return null;
        }

        private static XElement BuildHotspot(SpawnPoint point) =>
            new XElement("Hotspot",
                new XAttribute("X", point.X),
                new XAttribute("Y", point.Y),
                new XAttribute("Z", point.Z));

        private static IEnumerable<XAttribute> LocationAttributes(SpawnPoint point)
        {
            yield return new XAttribute("X", point.X);
            yield return new XAttribute("Y", point.Y);
            yield return new XAttribute("Z", point.Z);
        }

        public string WriteProfile(string xml)
        {
            if (_profilePath == null)
                return null;
            File.WriteAllText(_profilePath, xml);
            return _profilePath;
        }

        public string BuildEmptyProfile(string zoneName, int playerLevel,
            List<VendorEntry> vendors = null)
        {
            XDocument doc = new XDocument(
                new XElement("HBProfile",
                    new XElement("Name", $"WholesomeAQ - {zoneName} (empty)"),
                    new XElement("MinLevel", 1),
                    new XElement("MaxLevel", 80),
                    new XElement("MinDurability", "0.2"),
                    new XElement("MinFreeBagSlots", "2"),
                    new XElement("AvoidMobs"),
                    new XElement("Blackspots"),
                    new XElement("Mailboxes"),
                    BuildVendorsElement(vendors),
                    new XElement("QuestOrder")
                )
            );

            string xml = doc.Declaration + Environment.NewLine + doc.ToString();
            return WriteProfile(xml);
        }

        private static XElement BuildVendorsElement(List<VendorEntry> vendors)
        {
            if (vendors == null || vendors.Count == 0)
                return new XElement("Vendors");

            XElement ve = new XElement("Vendors");
            foreach (VendorEntry v in vendors)
            {
                XElement vendor = new XElement("Vendor",
                    new XAttribute("Name", v.Name ?? ""),
                    new XAttribute("Entry", v.Entry),
                    new XAttribute("Type", v.Type ?? ""),
                    new XAttribute("X", v.X),
                    new XAttribute("Y", v.Y),
                    new XAttribute("Z", v.Z)
                );

                if (!string.IsNullOrEmpty(v.TrainClass))
                    vendor.Add(new XAttribute("TrainClass", v.TrainClass));

                ve.Add(vendor);
            }
            return ve;
        }
    }
}