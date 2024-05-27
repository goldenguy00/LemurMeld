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

using RiskyMod.Allies;
using HarmonyLib;
using R2API;

namespace DronemeldDevotionFix
{
    [BepInDependency(T2.TILER2Plugin.ModGuid)]
    [BepInDependency(DM.DronemeldPlugin.ModGuid)]
    [BepInDependency("com.rune580.riskofoptions", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.RiskyLives.RiskyMod", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class DronemeldFixPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.score.DronemeldDevotionFix";
        public const string PluginName = "DronemeldDevotionFix";
        public const string PluginVersion = "1.0.5";

        internal static LOG.ManualLogSource _logger;

        public static bool rooInstalled => Chainloader.PluginInfos.ContainsKey("com.rune580.riskofoptions");
        public static bool riskyInstalled => Chainloader.PluginInfos.ContainsKey("com.RiskyLives.RiskyMod");

        public static List<EquipmentIndex> lowLvl = [];
        public static List<EquipmentIndex> highLvl = [];
        public static List<EquipmentIndex> gigaChadLvl = [];

        public delegate CharacterMaster orig_TryApply(IEnumerable<CharacterMaster> cm);
        public delegate CharacterMaster hook_TryApply(orig_TryApply orig, IEnumerable<CharacterMaster> cm);

        public delegate bool orig_IsDronemeldEnabledFor(string masterPrefabName);
        public delegate bool hook_IsDronemeldEnabledFor(orig_IsDronemeldEnabledFor orig, string masterPrefabName);

        public static Dictionary<DevotedLemurianController, SortedList<int, int>> lemItemDict = [];
        public static DevotedLemurianController meldRef;

        public void Awake()
        {
            _logger = Logger;
            PluginConfig.myConfig = Config;
            PluginConfig.ReadConfig();

            if (!PluginConfig.enabled.Value) return;

            //       //
            // hooks //
            //       //

            // inventory display
            // can probably just use nuxlar's at this point but whatever, its here now.
            On.RoR2.UI.ScoreboardController.Rebuild += AddLemurianInventory;

            // artifact enable
            RunArtifactManager.onArtifactEnabledGlobal += RunArtifactManager_onArtifactEnabledGlobal;

            // summon
            if (riskyInstalled) On.RoR2.DevotionInventoryController.GetOrCreateDevotionInventoryController += DevotionInventoryController_GetOrCreateDevotionInventoryController;
            IL.RoR2.CharacterAI.LemurianEggController.SummonLemurian += LemurianEggController_SummonLemurian;

            // evolve
            IL.RoR2.DevotionInventoryController.GenerateEliteBuff += DevotionInventoryController_GenerateEliteBuff;
            IL.RoR2.DevotionInventoryController.EvolveDevotedLumerian += DevotionInventoryController_EvolveDevotedLumerian;
            On.RoR2.DevotionInventoryController.UpdateMinionInventory += DevotionInventoryController_UpdateMinionInventory;

            // die
            On.DevotedLemurianController.OnDevotedBodyDead += DevotedLemurianController_OnDevotedBodyDead;

            // dronemeld hooks
            HookEndpointManager.Add<hook_TryApply>(typeof(DM.DronemeldPlugin).GetMethod("TryApply", [typeof(IEnumerable<CharacterMaster>)]), TryApply);
            HookEndpointManager.Add<hook_IsDronemeldEnabledFor>(typeof(DM.DronemeldPlugin).GetMethod("IsDronemeldEnabledFor"), IsDronemeldEnabledFor);
        }

        #region Dronemeld hooks
        private static bool IsDronemeldEnabledFor(orig_IsDronemeldEnabledFor orig, string masterPrefabName)
        {
            if (masterPrefabName == "DevotedLemurianMaster" ||
                masterPrefabName == "DevotedLemurianBruiserMaster")
                return true;

            return orig(masterPrefabName);
        }

        private static CharacterMaster TryApply(orig_TryApply orig, IEnumerable<CharacterMaster> cm)
        {
            var targetMaster = orig(cm);
            meldRef = null;

            if (targetMaster != null && targetMaster.TryGetComponent<DevotedLemurianController>(out var lem))
            {
                meldRef = lem;
                if (!lemItemDict.ContainsKey(meldRef))
                    lemItemDict.Add(meldRef, []);
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
                MonoBehaviour.Destroy(nux.Instance);
            }

            DronemeldFixPlugin.lowLvl = [];
            DronemeldFixPlugin.highLvl = [];
            DronemeldFixPlugin.gigaChadLvl = [];

            // thank you moffein for showing me the way, fuck this inconsistent bs
            // holy shit this is horrible i hate it i hate it i hate it
            foreach (var etd in EliteAPI.GetCombatDirectorEliteTiers().ToList())
            {
                if (etd != null && etd.eliteTypes.Length > 0)
                {
                    //Super scuffed. Checking for the Elite Type directly didn't work.
                    if (etd.eliteTypes != null)
                    {
                        var isT2 = false;
                        var isT1 = false;
                        foreach (EliteDef ed in etd.eliteTypes)
                        {
                            if (ed != null && !ed.name.EndsWith("Honor"))
                            {
                                if (ed.eliteEquipmentDef == RoR2Content.Equipment.AffixPoison || ed.eliteEquipmentDef == RoR2Content.Equipment.AffixLunar)
                                {
                                    isT2 = true;
                                    break;
                                }
                                else if (ed.eliteEquipmentDef == RoR2Content.Equipment.AffixBlue)
                                {
                                    isT1 = true;
                                    break;
                                }
                            }
                        }

                        if (isT1 || isT2)
                        {
                            foreach (var ed in etd.eliteTypes)
                            {
                                //Check if EliteDef has an associated buff and the character doesn't already have the buff.
                                bool isBuffValid = ed && ed.eliteEquipmentDef
                                    && ed.eliteEquipmentDef.equipmentIndex != EquipmentIndex.None
                                    && ed.eliteEquipmentDef.passiveBuffDef
                                    && ed.eliteEquipmentDef.passiveBuffDef.isElite;

                                if (!isBuffValid) continue;

                                if (isT1)
                                {
                                    if (PluginConfig.enableDebugging.Value) _logger.LogInfo("t1 " + ed.eliteIndex +" " + ed.eliteEquipmentDef.nameToken);
                                    lowLvl.Add(ed.eliteEquipmentDef.equipmentIndex);
                                    highLvl.Add(ed.eliteEquipmentDef.equipmentIndex);
                                }
                                else if (isT2)
                                {
                                    if (PluginConfig.enableDebugging.Value) _logger.LogInfo("t2 " + ed.eliteIndex + " " + ed.eliteEquipmentDef.nameToken);
                                    highLvl.Add(ed.eliteEquipmentDef.equipmentIndex);
                                    gigaChadLvl.Add(ed.eliteEquipmentDef.equipmentIndex);
                                }
                            }
                        }
                    }
                }
            }
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
                MinionOwnership.MinionGroup minionGroup = MinionOwnership.MinionGroup.FindGroup(master.netId);
                if (minionGroup != null)
                {
                    foreach (MinionOwnership minionOwnership in minionGroup.members)
                    {
                        if (minionOwnership)
                        {
                            var lem = minionOwnership.GetComponent<CharacterMaster>();
                            if (lem.gameObject.name.StartsWith("DevotedLemurian"))
                            {
                                masters.Add(lem);

                                if (!PluginConfig.showAllMinions.Value)
                                    break;
                            }
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
                c.EmitDelegate((LemurianEggController self, CharacterMaster cm, EffectData ed, int idx) =>
                {
                    if (!cm)
                    {
                        var lemInvCtrl = DevotionInventoryController.GetOrCreateDevotionInventoryController(self.interactor);
                        if (lemInvCtrl)
                        {
                            if (meldRef != null)
                            {
                                var sortedList = lemItemDict[meldRef];
                                if (sortedList.ContainsKey(idx)) sortedList[idx]++;
                                else sortedList.Add(idx, 1);
                            }

                            if (PluginConfig.shareItems.Value)
                                lemInvCtrl.GiveItem((ItemIndex)idx);
                            else
                                meldRef?.LemurianInventory.GiveItem((ItemIndex)idx);

                            lemInvCtrl.UpdateAllMinions(false);
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

        private DevotionInventoryController DevotionInventoryController_GetOrCreateDevotionInventoryController(
            On.RoR2.DevotionInventoryController.orig_GetOrCreateDevotionInventoryController orig, Interactor summoner)
        {
            var ctrl = orig(summoner);

            if (ctrl != null && ctrl._devotionMinionInventory)
            {
                var inventory = ctrl._devotionMinionInventory;
                if (inventory.GetItemCount(AllyItems.AllyMarkerItem) <= 0)
                {
                    inventory.GiveItem(AllyItems.AllyMarkerItem);
                    inventory.GiveItem(AllyItems.AllyRegenItem, 40);
                }
            }
            return ctrl;
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
                    if (PluginConfig.randomizeElites.Value)
                    {
                        List<EquipmentIndex> list = DronemeldFixPlugin.gigaChadLvl;

                        int index = UE.Random.Range(0, list.Count);
                        body.inventory.SetEquipmentIndex(list[index]);
                    }
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
                i => i.MatchLdsfld<DevotionInventoryController>(nameof(DevotionInventoryController.lowLevelEliteBuffs))
                ))
            {
                // fuck it just nuke it all
                c.RemoveRange(4);
                c.EmitDelegate<Func<bool, List<EquipmentIndex>>>((isLowLvl) =>
                {
                    return isLowLvl ? DronemeldFixPlugin.lowLvl : DronemeldFixPlugin.highLvl;
                });
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

            if (PluginConfig.disableFallDamage.Value)
                lemCtrl.LemurianBody.bodyFlags |= CharacterBody.BodyFlags.IgnoreFallDamage;

            lemCtrl._leashDistSq = PluginConfig.teleportDistance.Value * PluginConfig.teleportDistance.Value;

            //return untracked items
            if (!PluginConfig.shareItems.Value && lemItemDict.TryGetValue(lemCtrl, out var itemStacks))
            {
                foreach (var item in itemStacks)
                {
                    lemCtrl.LemurianInventory.GiveItem((ItemIndex)item.Key, item.Value);
                }
            }
        }
        #endregion

        #region On Death
        private static void DevotedLemurianController_OnDevotedBodyDead(On.DevotedLemurianController.orig_OnDevotedBodyDead orig, DevotedLemurianController self)
        {
            if (self._devotionInventoryController.HasItem(RoR2Content.Items.ExtraLife))
            {
                orig(self);
                return;
            }

            if (lemItemDict.TryGetValue(self, out var lemList))
            {
                if (PluginConfig.enableDebugging.Value) _logger.LogInfo("Found lem");
                foreach(var item in lemList)
                {
                    ItemDef itemDef = ItemCatalog.GetItemDef((ItemIndex)item.Key);
                    if (itemDef != null)
                    {
                        PickupIndex pickupIndex = PickupIndex.none;
                        switch (itemDef.tier)
                        {
                            case ItemTier.Tier1:
                                pickupIndex = PickupCatalog.FindPickupIndex("ItemIndex.ScrapWhite");
                                break;
                            case ItemTier.Tier2:
                                pickupIndex = PickupCatalog.FindPickupIndex("ItemIndex.ScrapGreen");
                                break;
                            case ItemTier.Tier3:
                                pickupIndex = PickupCatalog.FindPickupIndex("ItemIndex.ScrapRed");
                                break;
                            case ItemTier.Boss:
                                pickupIndex = PickupCatalog.FindPickupIndex("ItemIndex.ScrapYellow");
                                break;
                        }
                        if (pickupIndex != PickupIndex.none)
                        {
                            if (PluginConfig.enableDebugging.Value) _logger.LogInfo("Dropping item " + itemDef.nameToken);
                            PickupDropletController.CreatePickupDroplet(pickupIndex, self.LemurianBody.corePosition, UE.Random.insideUnitCircle * 15f);

                            if (PluginConfig.shareItems.Value)
                                self._devotionInventoryController.RemoveItem((ItemIndex)item.Key, item.Value);
                        }
                    }
                }
                lemItemDict.Remove(self);
                meldRef = null;
            }
            else
            {
                if (PluginConfig.enableDebugging.Value) _logger.LogError("Could not find target lem :(");
            }

            orig(self);
        }
        #endregion
    }
}
