using BepInEx;
using R2API;
using R2API.Utils;
using RoR2;
using UnityEngine;
using RiskOfOptions;
using UnityEngine.AddressableAssets;
using MonoMod.Cil;
using Mono.Cecil.Cil;
using System;
using BepInEx.Configuration;
using RiskOfOptions.OptionConfigs;
using RiskOfOptions.Options;

namespace BehemothProc
{
    [BepInDependency("com.rune580.riskofoptions")]

    // Soft Dependencies
    //[BepInDependency(LookingGlass.PluginInfo.PLUGIN_GUID, BepInDependency.DependencyFlags.SoftDependency)]

    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [NetworkCompatibility(CompatibilityLevel.NoNeedForSync, VersionStrictness.DifferentModVersionsAreOk)]
    public class MountainItemPlugin : BaseUnityPlugin
    {

        public const string PluginGUID = PluginAuthor + "." + PluginName;
        public const string PluginAuthor = "Braquen";
        public const string PluginName = "Let_Behemoth_Proc";
        public const string PluginVersion = "1.0.0";


        public static BepInEx.PluginInfo pluginInfo;
        public static AssetBundle AssetBundle;

        public static ConfigEntry<float> ProcCoefficient;

        public void Awake()
        {
            Log.Init(Logger);

            pluginInfo = Info;

            var Config = new ConfigFile(System.IO.Path.Combine(Paths.ConfigPath, "braquen-LetBehemothProc.cfg"), true);

            ProcCoefficient = Config.Bind("LET HIM PROC!", "Proc Coefficient", 0.5f, "This number is multiplied by the proc coefficient of the original attack to determine the explosion's proc coefficient, i.e. its the Proc Coefficient's Coefficient. Set to 0 to return to normal functionality.");

            //Set the max to 10x proc chance, because its a free country.
            ModSettingsManager.AddOption(new StepSliderOption(ProcCoefficient,
                new StepSliderConfig { min = 0, max = 10, increment = 0.1f }));

            ModSettingsManager.SetModDescription("A mod to allow Behemoth explosions to proc other items.");
            var sprite = Addressables.LoadAssetAsync<Sprite>("RoR2/Base/Behemoth/texBehemothIcon.png").WaitForCompletion();
            ModSettingsManager.SetModIcon(sprite);

            IL.RoR2.GlobalEventManager.OnHitAllProcess += GlobalEventManager_OnHitAllProcess;
        }

        private static void GlobalEventManager_OnHitAllProcess(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            Log.Debug("Changing proc chain mask");
            try
            {
                c.GotoNext(
                MoveType.Before,
                x => x.MatchDup(),
                x => x.MatchLdarg(1),
                x => x.MatchLdfld<DamageInfo>("procChainMask"),
                x => x.MatchStfld<BlastAttack>("procChainMask")
                );
                
                c.Index += 3;
                c.EmitDelegate(AddBehemothProc);
            }
            catch(Exception e) { ErrorHookFailed("Add Behemoth to mask", e); }

            Log.Debug("Changing proc coefficient");
            try {
                c.GotoNext(
                MoveType.Before,
                x => x.MatchDup(),
                x => x.MatchLdcR4(0f),
                x => x.MatchStfld<BlastAttack>("procCoefficient")
                );

                c.Index += 1;
                c.Remove();
                c.Emit(OpCodes.Ldarg, 1);
                c.EmitDelegate(InsertProcCoefficient);
            }
            catch (Exception e) { ErrorHookFailed("Edit Behemoth Proc Coefficient", e); }
        }

        private static ProcChainMask AddBehemothProc(ProcChainMask procChainMask)
        {
            procChainMask.AddProc(ProcType.Behemoth);
            return procChainMask;
        }

        private static float InsertProcCoefficient(DamageInfo damageInfo)
        {
            return damageInfo.procCoefficient * ProcCoefficient.Value;
        }

        internal static void ErrorHookFailed(string name, Exception e)
        {
            Log.Error(name + " hook failed: " + e.Message);
        }
    }
}

