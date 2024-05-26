using System;
using System.Linq;
using System.Collections.Generic;
using BepInEx;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using RoR2;
using RoR2.CharacterAI;
using UnityEngine;
using UnityEngine.Networking;
using JetBrains.Annotations;
using MonoMod.RuntimeDetour.HookGen;
using BepInEx.Bootstrap;

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
        public const string PluginVersion = "1.0.4";

        internal static LOG.ManualLogSource _logger;

        public static List<EquipmentIndex> lowLvl = [];
        public static List<EquipmentIndex> highLvl = [];
        public static List<EquipmentIndex> gigaChadLvl = [];

        public static PickupIndex scrapWhite = PickupIndex.none;
        public static PickupIndex scrapGreen = PickupIndex.none;
        public static PickupIndex scrapRed = PickupIndex.none;
        public static PickupIndex scrapYellow = PickupIndex.none;

        public delegate CharacterMaster orig_TryApply(IEnumerable<CharacterMaster> cm);
        public delegate CharacterMaster hook_TryApply(orig_TryApply orig, IEnumerable<CharacterMaster> cm);

        private static Dictionary<CharacterMaster, List<ItemIndex>> fuckingStupid = [];
        private static CharacterMaster fuckthis = null;

        public void Awake()
        {
            _logger = Logger;

            //       //
            // hooks //
            //       //

            // inventory display
            On.RoR2.UI.ScoreboardController.Rebuild += AddLemurianInventory;

            // artifact enable
            RunArtifactManager.onArtifactEnabledGlobal += RunArtifactManager_onArtifactEnabledGlobal;

            // summon
            IL.RoR2.CharacterAI.LemurianEggController.SummonLemurian += LemurianEggController_SummonLemurian;

            // evolve
            IL.RoR2.DevotionInventoryController.EvolveDevotedLumerian += DevotionInventoryController_EvolveDevotedLumerian;
            IL.RoR2.DevotionInventoryController.GenerateEliteBuff += DevotionInventoryController_GenerateEliteBuff;
            On.RoR2.DevotionInventoryController.UpdateMinionInventory += DevotionInventoryController_UpdateMinionInventory;

            //die
            On.RoR2.DevotionInventoryController.DropScrapOnDeath += DevotionInventoryController_DropScrapOnDeath;

            //this fix is so fucking stupid, it makes me genuinely upset that it works
            HookEndpointManager.Add<hook_TryApply>(typeof(DM.DronemeldPlugin).GetMethod("TryApply", [typeof(IEnumerable<CharacterMaster>)]), TryApply);
        }

        #region worst hook ever
        private static CharacterMaster TryApply(orig_TryApply orig, IEnumerable<CharacterMaster> cm)
        {
            var targetMaster = orig(cm);
            if (targetMaster != null)
            {
                fuckthis = targetMaster;
                if (!fuckingStupid.ContainsKey(targetMaster))
                {
                    fuckingStupid.Add(targetMaster, []);
                }
            }
            return targetMaster;
        }
        #endregion

        #region Artifact Setup
        [SystemInitializer([typeof(ItemCatalog)])]
        private static void HideLemItems()
        {
            ItemDef[] itemDefs = ItemCatalog.itemDefs;
            foreach (ItemDef itemDef in itemDefs)
            {
                if (itemDef.nameToken == "ITEM_BOOSTDAMAGE_NAME" || itemDef.nameToken == "ITEM_BOOSTHP_NAME")
                {
                    itemDef.hidden = true;
                }
            }
        }

        private static void RunArtifactManager_onArtifactEnabledGlobal([NotNull] RunArtifactManager runArtifactManager, [NotNull] ArtifactDef artifactDef)
        {
            if (artifactDef != CU8Content.Artifacts.Devotion) return;

            if (Chainloader.PluginInfos.TryGetValue("com.Nuxlar.DevotionInventoryDisplay", out var nux) && nux != null && nux.Instance != null)
            {
                // sorry nux
                _logger.LogWarning("Bye nux");
                MonoBehaviour.Destroy(nux.Instance);
            }

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


            if (!PickupCatalog.nameToPickupIndex.TryGetValue("ItemIndex.ScrapWhite", out var scrapWhite)) scrapWhite = PickupIndex.none;
            if (!PickupCatalog.nameToPickupIndex.TryGetValue("ItemIndex.ScrapGreen", out var scrapGreen)) scrapGreen = PickupIndex.none;
            if (!PickupCatalog.nameToPickupIndex.TryGetValue("ItemIndex.ScrapRed", out var scrapRed)) scrapRed = PickupIndex.none;
            if (!PickupCatalog.nameToPickupIndex.TryGetValue("ItemIndex.ScrapYellow", out var scrapYellow)) scrapYellow = PickupIndex.none;
        }

        private static void AddLemurianInventory(On.RoR2.UI.ScoreboardController.orig_Rebuild orig, RoR2.UI.ScoreboardController self)
        {
            orig(self);
            if (!RunArtifactManager.instance || !RunArtifactManager.instance.IsArtifactEnabled(CU8Content.Artifacts.Devotion))
            {
                return;
            }

            List<CharacterMaster> masters = [];
            foreach (var instance in PlayerCharacterMasterController.instances)
            {
                masters.Add(instance.master);
            }

            var master = LocalUserManager.readOnlyLocalUsersList.First().cachedMasterController.master;
            if (master)
            {
                var minionGroup = MinionOwnership.MinionGroup.FindGroup(master.netId);
                if (minionGroup != null)
                {
                    foreach (MinionOwnership minionOwnership in minionGroup.members)
                    {
                        if (minionOwnership)
                        {
                            var lem = minionOwnership.GetComponent<CharacterMaster>();
                            if (lem != null && lem.gameObject.name.StartsWith("DevotedLemurian"))
                                masters.Add(lem);
                        }
                    }
                }
            }

            self.SetStripCount(masters.Count);
            for (int i = 0; i < masters.Count; i++)
            {
                self.stripAllocator.elements[i].SetMaster(masters[i]);
            }
        }
        #endregion

        #region Summon
        private static void LemurianEggController_SummonLemurian(ILContext il)
        {
            var c = new ILCursor(il);

            if (c.TryGotoNext(MoveType.AfterLabel,
                i => i.MatchLdarg(0),
                i => i.MatchCall<UE.Component>("get_gameObject"),
                i => i.MatchCall<UE.Object>("Destroy")))
            {
                c.Emit(OpCodes.Ldarg_0);
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
                            if (fuckthis != null)
                            {
                                fuckingStupid[fuckthis].Add((ItemIndex)item);
                                fuckthis = null;
                            }

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

        private void DevotionInventoryController_UpdateMinionInventory(On.RoR2.DevotionInventoryController.orig_UpdateMinionInventory orig,
            DevotionInventoryController self, DevotedLemurianController lemCtrl, bool shouldEvolve)
        {
            if (!NetworkServer.active)
            {
                orig(self, lemCtrl, shouldEvolve);
                return;
            }

            var stackCount = lemCtrl.LemurianInventory.GetItemCount(DM.DronemeldPlugin.stackItem);

            orig(self, lemCtrl, shouldEvolve);

            if (stackCount > 0)
                lemCtrl.LemurianInventory.GiveItem(DM.DronemeldPlugin.stackItem, stackCount);

            if (fuckingStupid.TryGetValue(lemCtrl._lemurianMaster, out var dumbList))
            {
                foreach(var dumbItem in dumbList)
                {
                    lemCtrl.LemurianInventory.GiveItem(dumbItem);
                }
            }
        }
        #endregion

        #region On Death
        private static void DevotionInventoryController_DropScrapOnDeath(On.RoR2.DevotionInventoryController.orig_DropScrapOnDeath orig, 
            DevotionInventoryController self, ItemIndex devotionItem, CharacterBody minionBody)
        {
            orig(self, devotionItem, minionBody);

            if (NetworkServer.active && fuckingStupid.TryGetValue(minionBody.master, out var stupidList))
            {
                foreach(var shit in stupidList)
                {
                    ItemDef itemDef = ItemCatalog.GetItemDef(shit);
                    if (itemDef != null)
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
                stupidList.Clear();
                fuckingStupid.Remove(minionBody.master);
            }
        }
        #endregion
    }
}
