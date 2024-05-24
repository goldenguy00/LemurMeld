using BepInEx;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using R2API;
using RoR2;
using RoR2.CharacterAI;
using System.Linq;
using ThinkInvisible.Dronemeld;
using UnityEngine;
using UE = UnityEngine;

namespace DronemeldDevotionFix
{
    [BepInDependency(TILER2.TILER2Plugin.ModGuid)]
    [BepInDependency(DronemeldPlugin.ModGuid)]
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class DronemeldFixPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.score.DronemeldDevotionFix";
        public const string PluginName = "DronemeldDevotionFix";
        public const string PluginVersion = "1.0.2";

        public void Awake()
        {
            // hooks
            //On.RoR2.DevotionInventoryController.OnDevotionArtifactEnabled += DevotionInventoryController_OnDevotionArtifactEnabled;
            IL.RoR2.CharacterAI.LemurianEggController.SummonLemurian += LemurianEggController_SummonLemurian;
        }

        private static void LemurianEggController_SummonLemurian(ILContext il)
        {
            var c = new ILCursor(il);

            if (c.TryGotoNext(MoveType.Before,
                i => i.MatchCall<UE.Component>("get_gameObject"),
                i => i.MatchCall<UE.Object>("Destroy")))
            {
                c.RemoveRange(2);
                c.Emit(OpCodes.Ldloc_1);
                c.Emit(OpCodes.Ldarga_S, (byte)1);
                c.Emit<PickupIndex>(OpCodes.Call, "get_itemIndex");
                c.EmitDelegate((LemurianEggController self, CharacterMaster cm, int item) =>
                {
                    if (!cm)
                    {
                        var lemInventory = DevotionInventoryController.GetOrCreateDevotionInventoryController(self.interactor);
                        if (lemInventory)
                        {
                            lemInventory.GiveItem(DronemeldPlugin.stackItem.itemIndex, 1);
#pragma warning disable CS0618 // Type or member is obsolete
                            lemInventory.GiveItem((ItemIndex)item, 1);
#pragma warning restore CS0618 // Type or member is obsolete
                            lemInventory.UpdateAllMinions(false);
                            Util.PlaySound(self.sfxLocator.openSound, self.gameObject);
                            EffectManager.SpawnEffect(LegacyResourcesAPI.Load<GameObject>("Prefabs/Effects/LemurianEggHatching"), new EffectData
                            {
                                origin = self.gameObject.transform.position
                            }, true);
                        }
                    }
                    Destroy(self.gameObject);
                });
                
            }
            else
            {
                Debug.LogError("DronemeldDevotionFix: Nah you suck sorry guy");
            }
        }

        /*
        private void DevotionInventoryController_OnDevotionArtifactEnabled(On.RoR2.DevotionInventoryController.orig_OnDevotionArtifactEnabled orig, RunArtifactManager runArtifactManager, ArtifactDef artifactDef)
        {
            orig.Invoke(runArtifactManager, artifactDef);
            if (artifactDef != CU8Content.Artifacts.Devotion || EliteAPI.EliteDefinitions?.Any() != true)
            {
                return;
            }
            foreach (var elite in EliteAPI.EliteDefinitions)
            {
                if (elite?.EliteDef != null && elite.EliteDef.IsAvailable())
                {
                    // all types can become high lvl
                    DevotionInventoryController.highLevelEliteBuffs.Add(elite.EliteDef.eliteEquipmentDef.equipmentIndex);

                    if (EliteAPI.VanillaFirstTierDef.eliteTypes.Any(e => e.eliteIndex == elite.EliteDef.eliteIndex))
                        DevotionInventoryController.lowLevelEliteBuffs.Add(elite.EliteDef.eliteEquipmentDef.equipmentIndex);
                };
            }
        }
        */
    }
}
