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
using R2API;
using HarmonyLib;

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

        public static bool Enabled => PluginConfig.enabled.Value;

        public static bool rooInstalled => Chainloader.PluginInfos.ContainsKey("com.rune580.riskofoptions");
        public static bool riskyInstalled => Chainloader.PluginInfos.ContainsKey("com.RiskyLives.RiskyMod");

        public static List<EquipmentIndex> lowLvl = [];
        public static List<EquipmentIndex> highLvl = [];
        public static List<EquipmentIndex> gigaChadLvl = [];

        public delegate CharacterMaster orig_TryApply(IEnumerable<CharacterMaster> cm);
        public delegate CharacterMaster hook_TryApply(orig_TryApply orig, IEnumerable<CharacterMaster> cm);

        public delegate bool orig_IsDronemeldEnabledFor(string masterPrefabName);
        public delegate bool hook_IsDronemeldEnabledFor(orig_IsDronemeldEnabledFor orig, string masterPrefabName);

        public static Dictionary<DevotedLemurianController, List<ItemIndex>> lemItemDict = new Dictionary<DevotedLemurianController, List<ItemIndex>>();
        public static DevotedLemurianController meldRef;

        private static bool showAllInv = false;
        private static bool shareItems = true;

        private static void Log(IEnumerable<string> message, LogType? type = null) { message.Do(msg => Log(msg, type)); }
        private static void Log(string message, LogType? type = null)
        {
            if (PluginConfig.enableDebugging.Value)
            {
                switch(type ?? LogType.Log)
                {
                    default:
                    case LogType.Log:
                        _logger.LogMessage(message); break;
                    case LogType.Warning:
                        _logger.LogWarning(message); break;
                    case LogType.Error:
                        _logger.LogError(message); break;
                    case LogType.Exception:
                        _logger.LogFatal(message); break;
                }
            }
        }

        public void Awake()
        {
            _logger = Logger;
            PluginConfig.myConfig = Config;
            PluginConfig.ReadConfig();

            if (!Enabled) return;

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
            IL.RoR2.DevotionInventoryController.EvolveDevotedLumerian += DevotionInventoryController_EvolveDevotedLumerian;
            IL.RoR2.DevotionInventoryController.GenerateEliteBuff += DevotionInventoryController_GenerateEliteBuff;
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
                    lemItemDict.Add(meldRef, new List<ItemIndex>());
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
                Log("Bye nux");
                MonoBehaviour.Destroy(nux.Instance);
            }
            
            // config
            showAllInv = PluginConfig.showAllMinions.Value;
            shareItems = PluginConfig.shareItems.Value;

            var eliteTiers = EliteAPI.GetCombatDirectorEliteTiers().ToList();

            // linq is just fun tbh
            var highLvl = eliteTiers
                .SelectMany(t => t.eliteTypes)
                .Where(e => e.eliteEquipmentDef != null && !e.name.EndsWith("Honor"));

            var lowLvl = eliteTiers[1].eliteTypes
                .Where(e => e.eliteEquipmentDef != null);

            DronemeldFixPlugin.lowLvl = lowLvl.Select(e => e.eliteEquipmentDef.equipmentIndex).Distinct().ToList();
            DronemeldFixPlugin.highLvl = highLvl.Select(e => e.eliteEquipmentDef.equipmentIndex).Distinct().ToList();
            DronemeldFixPlugin.gigaChadLvl = DronemeldFixPlugin.highLvl.Except(DronemeldFixPlugin.lowLvl).ToList();

            Log(lowLvl.Select(e => e.name));
            Log(highLvl.Select(e => e.name));
            Log(highLvl.Except(lowLvl).Select(e => e.name));
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

                                if (!showAllInv)
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
                            var item = (ItemIndex)idx;
                            if (meldRef != null)
                            {
                                lemItemDict[meldRef].Add(item);
                            }

                            if (shareItems)
                                lemInvCtrl.GiveItem(item);
                            else
                                meldRef?.LemurianInventory.GiveItem(item);

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
                        List<EquipmentIndex> list = PluginConfig.allowT1Elites.Value ? 
                            DronemeldFixPlugin.highLvl : DronemeldFixPlugin.gigaChadLvl;
                        Log("DevotionInventoryController_EvolveDevotedLumerian");
                        Log(list.Select(e => ((int)e).ToString()));

                        int index = UE.Random.Range(0, list.Count);
                        Log(index.ToString());
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

            //untracked items
            IEnumerable<(ItemIndex, int)> itemStacks = null;
            if (!shareItems && lemItemDict.TryGetValue(lemCtrl, out var list))
            {
                var stacks = lemCtrl.LemurianInventory.itemStacks;
                itemStacks = list.Distinct().Select(idx => (idx, stacks[(int)idx]));
            }

            orig(self, lemCtrl, shouldEvolve);

            if (stackCount > 0)
                lemCtrl.LemurianInventory.GiveItem(DM.DronemeldPlugin.stackItem, stackCount);

            if (PluginConfig.disableFallDamage.Value)
                lemCtrl.LemurianBody.bodyFlags |= CharacterBody.BodyFlags.IgnoreFallDamage;

            //return untracked items
            if (itemStacks != null)
            {
                foreach (var item in itemStacks)
                {
                    lemCtrl.LemurianInventory.GiveItem(item.Item1, item.Item2);
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
                foreach(var item in lemList)
                {
                    ItemDef itemDef = ItemCatalog.GetItemDef(item);
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
                            PickupDropletController.CreatePickupDroplet(pickupIndex, self.LemurianBody.corePosition, UE.Random.insideUnitCircle * 15f);

                            if (shareItems)
                                self._devotionInventoryController.RemoveItem(item);
                        }
                    }
                }
                lemItemDict.Remove(self);
                meldRef = null;
            }

            orig(self);
        }
        #endregion
    }
}
