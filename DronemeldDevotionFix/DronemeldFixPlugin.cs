using System;
using System.Linq;
using System.Collections.Generic;
using BepInEx;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using RoR2;
using RoR2.CharacterAI;
using UnityEngine;
using JetBrains.Annotations;

using DM = ThinkInvisible.Dronemeld;
using T2 = TILER2;

using UE = UnityEngine;
using LOG = BepInEx.Logging;

namespace DronemeldDevotionFix
{
    [BepInDependency(T2.TILER2Plugin.ModGuid)]
    [BepInDependency(DM.DronemeldPlugin.ModGuid)]
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class DronemeldFixPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.score.DronemeldDevotionFix";
        public const string PluginName = "DronemeldDevotionFix";
        public const string PluginVersion = "1.0.3";

        internal static LOG.ManualLogSource _logger;

        public static List<EquipmentIndex> lowLvl = [];
        public static List<EquipmentIndex> highLvl = [];
        public static List<EquipmentIndex> gigaChadLvl = [];

        public void Awake()
        {
            _logger = Logger;

            //       //
            // hooks //
            //       //

            // artifact enable
            RunArtifactManager.onArtifactEnabledGlobal += RunArtifactManager_onArtifactEnabledGlobal;

            // summon
            IL.RoR2.CharacterAI.LemurianEggController.SummonLemurian += LemurianEggController_SummonLemurian;

            // evolve
            IL.RoR2.DevotionInventoryController.EvolveDevotedLumerian += DevotionInventoryController_EvolveDevotedLumerian;
            IL.RoR2.DevotionInventoryController.GenerateEliteBuff += DevotionInventoryController_GenerateEliteBuff;

            //die
            On.RoR2.DevotionInventoryController.DropScrapOnDeath += DevotionInventoryController_DropScrapOnDeath;
        }

        #region Artifact Enabled
        private static void RunArtifactManager_onArtifactEnabledGlobal([NotNull] RunArtifactManager runArtifactManager, [NotNull] ArtifactDef artifactDef)
        {
            if (artifactDef != CU8Content.Artifacts.Devotion) return;

            // linq is just fun tbh
            var highLvl = CombatDirector.eliteTiers
                .Where(t => !t.canSelectWithoutAvailableEliteDef)
                .SelectMany(t => t.eliteTypes)
                .Where(e => e.eliteEquipmentDef && !e.name.EndsWith("Honor"));

            var lowLvl = CombatDirector.eliteTiers[1].eliteTypes
                .Where(e => e.eliteEquipmentDef);

            DronemeldFixPlugin.lowLvl = lowLvl.Select(e => e.eliteEquipmentDef.equipmentIndex).Distinct().ToList();
            DronemeldFixPlugin.highLvl = highLvl.Select(e => e.eliteEquipmentDef.equipmentIndex).Distinct().ToList();
            DronemeldFixPlugin.gigaChadLvl = DronemeldFixPlugin.highLvl.Except(DronemeldFixPlugin.lowLvl).ToList();
        }
        #endregion

        #region Summon
        private static void LemurianEggController_SummonLemurian(ILContext il)
        {
            var c = new ILCursor(il);

            if (c.TryGotoNext(MoveType.AfterLabel,
                i => i.MatchCall<UE.Component>("get_gameObject"),
                i => i.MatchCall<UE.Object>("Destroy")))
            {
                c.Emit(OpCodes.Ldloc_1);
                c.Emit(OpCodes.Ldloc_2);
                c.Emit(OpCodes.Ldarga_S, (byte)1);
                c.Emit<PickupIndex>(OpCodes.Call, "get_itemIndex");
                c.EmitDelegate((LemurianEggController self, CharacterMaster cm, EffectData ed, int item) =>
                {
                    if (!cm)
                    {
                        var lemInventory = DevotionInventoryController.GetOrCreateDevotionInventoryController(self.interactor);
                        if (lemInventory)
                        {
                            lemInventory.GiveItem(DM.DronemeldPlugin.stackItem.itemIndex, 1);
                            lemInventory.GiveItem((ItemIndex)item, 1);
                            lemInventory.UpdateAllMinions(false);
                            Util.PlaySound(self.sfxLocator.openSound, self.gameObject);
                            EffectManager.SpawnEffect(LegacyResourcesAPI.Load<GameObject>("Prefabs/Effects/LemurianEggHatching"), ed, true);
                        }
                    }
                });
            }
            else
            {
                _logger.LogError("Sorry guy, ILHook failed for LemurianEggController::SummonLemurian");
            }
        }
        #endregion

        #region Evolution
        private static void DevotionInventoryController_EvolveDevotedLumerian(ILContext il)
        {
            var c = new ILCursor(il);

            if (c.TryGotoNext(MoveType.AfterLabel,
                i => i.MatchLdstr("shouldn't evolve!"),
                i => i.MatchCall<UE.Debug>("LogError")))
            {
                c.Emit(OpCodes.Ldloc_0);
                c.EmitDelegate<Action<CharacterBody>>((body) =>
                {
                    List<EquipmentIndex> list = DronemeldFixPlugin.gigaChadLvl;

                    int index = UE.Random.Range(0, list.Count);
                    body.inventory.SetEquipmentIndex(list[index]);
                });
                c.RemoveRange(2);
            }
            else
            {
                _logger.LogError("Sorry guy, ILHook failed for DevotionInventoryController::EvolveDevotedLumerian");
            }
        }

        private static void DevotionInventoryController_GenerateEliteBuff(ILContext ll)
        {
            var c = new ILCursor(ll);

            if (c.TryGotoNext(MoveType.Before,
                i => i.MatchBrtrue(out _),
                i => i.MatchLdsfld<DevotionInventoryController>(nameof(DevotionInventoryController.highLevelEliteBuffs)),
                i => i.MatchBr(out _),
                i => i.MatchLdsfld<DevotionInventoryController>(nameof(DevotionInventoryController.lowLevelEliteBuffs)),
                i => i.MatchStloc(0)
                ))
            {
                // fuck it just nuke it all
                c.RemoveRange(5);
                ILLabel label1 = c.DefineLabel();
                ILLabel label2 = c.DefineLabel();

                c.Emit(OpCodes.Brtrue_S, label1);
                c.Emit<DronemeldFixPlugin>(OpCodes.Ldsfld, nameof(highLvl));
                c.Emit(OpCodes.Br_S, label2);
                c.MarkLabel(label1);
                c.Emit<DronemeldFixPlugin>(OpCodes.Ldsfld, nameof(lowLvl));
                c.MarkLabel(label2);
                c.Emit(OpCodes.Stloc_0);
            }
            else
            {
                _logger.LogError("Sorry guy, ILHook failed for DevotionInventoryController::GenerateEliteBuff");
            }
        }
        #endregion

        #region On Death
        private static void DevotionInventoryController_DropScrapOnDeath(On.RoR2.DevotionInventoryController.orig_DropScrapOnDeath orig, 
            DevotionInventoryController self, ItemIndex devotionItem, CharacterBody minionBody)
        {
            orig(self, devotionItem, minionBody);

            if (!self || !self._devotionMinionInventory || !minionBody) return;

            var stackCount = self._devotionMinionInventory.GetItemCount(DM.DronemeldPlugin.stackItem);
            if (stackCount > 0)
            {
                self.RemoveItem(DM.DronemeldPlugin.stackItem.itemIndex, stackCount);
            }

            if (!PickupCatalog.nameToPickupIndex.TryGetValue("ItemIndex.ScrapWhite", out var scrapWhite)) scrapWhite = PickupIndex.none;
            if (!PickupCatalog.nameToPickupIndex.TryGetValue("ItemIndex.ScrapGreen", out var scrapGreen)) scrapGreen = PickupIndex.none;
            if (!PickupCatalog.nameToPickupIndex.TryGetValue("ItemIndex.ScrapRed", out var scrapRed)) scrapRed = PickupIndex.none;
            if (!PickupCatalog.nameToPickupIndex.TryGetValue("ItemIndex.ScrapYellow", out var scrapYellow)) scrapYellow = PickupIndex.none;

            foreach (var item in self._devotionMinionInventory.itemAcquisitionOrder)
            {
                if (item == devotionItem) continue;

                ItemDef itemDef = ItemCatalog.GetItemDef(item);
                if (itemDef != null && !itemDef.hidden)
                {
                    PickupIndex pickupIndex;
                    switch (itemDef.tier)
                    {
                        case ItemTier.Tier1:
                            pickupIndex = scrapWhite;
                            break;
                        case ItemTier.Tier2:
                            pickupIndex = scrapGreen;
                            break;
                        case ItemTier.Tier3:
                            pickupIndex = scrapRed;
                            break;
                        case ItemTier.Boss:
                            pickupIndex = scrapYellow;
                            break;
                        default:
                            continue;
                    }
                    if (pickupIndex != PickupIndex.none)
                    {
                        PickupDropletController.CreatePickupDroplet(pickupIndex, minionBody.corePosition, UE.Random.insideUnitCircle * 15f);
                    }
                }
            }
        }
        #endregion
    }
}
