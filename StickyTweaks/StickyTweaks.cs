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
using RoR2.Projectile;

namespace StickyTweaks
{
    [BepInDependency("com.rune580.riskofoptions")]

    // Soft Dependencies
    //[BepInDependency(LookingGlass.PluginInfo.PLUGIN_GUID, BepInDependency.DependencyFlags.SoftDependency)]

    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [NetworkCompatibility(CompatibilityLevel.NoNeedForSync, VersionStrictness.DifferentModVersionsAreOk)]
    public class Main : BaseUnityPlugin
    {
        public const string PluginGUID = PluginAuthor + "." + PluginName;
        public const string PluginAuthor = "Braquen";
        public const string PluginName = "Sticky_Tweaks";
        public const string PluginVersion = "1.0.1";


        public static BepInEx.PluginInfo pluginInfo;
        public static AssetBundle AssetBundle;

        public static ConfigEntry<float> Damage;
        public static ConfigEntry<float> ProcChance;
        public static ConfigEntry<float> ProcCoefficient;
        public static ConfigEntry<bool> OnHitAll;

        private static float DamageCoefficient;

        private GameObject StickyBombPrefab;
        private static ProjectileImpactExplosion StickyExplosion;

        public static ModdedProcType StickyProc = ProcTypeAPI.ReserveProcType();

        public void Awake()
        {
            

            Log.Init(Logger);

            pluginInfo = Info;

            var Config = new ConfigFile(System.IO.Path.Combine(Paths.ConfigPath, "braquen-stickytweaks.cfg"), true);

            Damage = Config.Bind("Stats", "Damage", 1.8f, "Sticky Bomb's damage multiplier.");
            ModSettingsManager.AddOption(new StepSliderOption(Damage,
                new StepSliderConfig { min = 0.1f, max = 18f, increment = 0.05f }));
            ProcChance = Config.Bind("Stats", "Proc Chance", 5f, "Sticky Bomb's chance to proc on hit.");
            ModSettingsManager.AddOption(new StepSliderOption(ProcChance,
                new StepSliderConfig { min = 1, max = 300, increment = 0.5f }));
            ProcCoefficient = Config.Bind("Extra Functionality", "Proc Coefficient", 0.5f, "Sticky Bomb's proc coefficient. Set 0 to restore base game functionality.");
            ModSettingsManager.AddOption(new StepSliderOption(ProcCoefficient,
                new StepSliderConfig { min = 0f, max = 10f, increment = 0.1f }));
            OnHitAll = Config.Bind("Extra Functionality", "Stick Anything", false, "Allows Sticky Bomb to proc when hitting anything, similar to Behemoth or Overloading Elite bombs.");
            ModSettingsManager.AddOption(new CheckBoxOption(OnHitAll));

            ModSettingsManager.SetModDescription("Tweaks sticky bomb to give it more potential for synergy.");
            var sprite = Addressables.LoadAssetAsync<Sprite>("RoR2/Base/StickyBomb/texStickyBombIcon.png").WaitForCompletion();
            ModSettingsManager.SetModIcon(sprite);

            StickyBombPrefab = Addressables.LoadAssetAsync<GameObject>("RoR2/Base/StickyBomb/StickyBomb.prefab").WaitForCompletion();
            //Set projectile proc to 1f.
            ProjectileController StickyController = StickyBombPrefab.GetComponent<ProjectileController>();
            StickyController.procCoefficient = 1f;

            //Set blast proc to config
            StickyExplosion = StickyBombPrefab.GetComponent<ProjectileImpactExplosion>();
            StickyExplosion.blastProcCoefficient = ProcCoefficient.Value;
            ProcCoefficient.SettingChanged += (a, b) => { StickyExplosion.blastProcCoefficient = ProcCoefficient.Value; };
            //Set damage to config
            DamageCoefficient = Damage.Value / 1.8f;
            Damage.SettingChanged += (a, b) => { DamageCoefficient = Damage.Value / 1.8f; };

            Damage.SettingChanged += (o,a) => UpdateText();
            ProcChance.SettingChanged += (o, a) => UpdateText();
            ProcCoefficient.SettingChanged += (o, a) => UpdateText();
            OnHitAll.SettingChanged += (o, a) => UpdateText();

            UpdateText();

            IL.RoR2.GlobalEventManager.ProcessHitEnemy += GlobalEventManager_ProcessHitEnemy;
            On.RoR2.GlobalEventManager.OnHitAllProcess += GlobalEventManager_OnHitAllProcess;
        }
        
        private static void GlobalEventManager_ProcessHitEnemy(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            Log.Debug("Changing proc chain mask");
            try
            {
                c.GotoNext(
                    MoveType.Before,
                    x => x.MatchLdstr("Prefabs/Projectiles/StickyBomb")
                );
                c.GotoNext(
                    MoveType.Before,
                    x => x.MatchCallOrCallvirt<ProjectileManager>(nameof(ProjectileManager.FireProjectileWithoutDamageType))
                );

                Log.Debug("Replacing fire projectile call");
                
                c.Next.OpCode = OpCodes.Ldarg_1;
                c.Index++; 
                
                //FireProjectile, but with procmask
                c.EmitDelegate<Action<ProjectileManager, GameObject, Vector3, Quaternion, GameObject, float, float, bool, DamageColorIndex, GameObject, float, DamageInfo>>(FireModifiedStickyBomb);
                c.Index--;
                c.MarkLabel();
            }
            catch (Exception e) { ErrorHookFailed("Add Sticky to mask", e); return; }


            Log.Debug("Adding Procchain conditional");
            try
            {
                ILLabel stickyConditional = default;

                Log.Debug("Locating sticky bomb if-block");
                c.GotoPrev(
                MoveType.Before,
                    x => x.MatchCallOrCallvirt<Inventory>(nameof (Inventory.GetItemCountEffective))
                );
                Log.Debug("GetItemCount call found");
                c.GotoNext(
                MoveType.After,
                    x => x.MatchBle(out stickyConditional)
                );
                Log.Debug("Sticky bomb condition block end found");

                Log.Debug("Adding procchain max condition");
                Log.Debug(stickyConditional.Target);

                //If hasModdedProc, dont stick
                c.Emit(OpCodes.Ldarg_1);
                c.EmitDelegate(CheckHitEnemyProc);
                c.Emit(OpCodes.Brtrue, stickyConditional);


                Log.Debug("Proc condition added, now adding new proc chance");

                //Go to where 5f proc chance is loaded
                c.GotoNext(
                    MoveType.After,
                    x => x.MatchLdcR4(5)
                );
                Log.Debug("Old proc chance found");

                //consume it and return our own chance
                c.EmitDelegate(InjectProcChance);

                Log.Debug("Added new proc chance");
            }
            catch (Exception e) { ErrorHookFailed("Check for sticky proc", e); }
            
        }

        private static bool CheckHitEnemyProc(DamageInfo info)
        {
            //if onHitAll, wait till that hook is called so we dont double dip procs
            return OnHitAll.Value || ProcTypeAPI.HasModdedProc(info.procChainMask, StickyProc);
        }

        //Consumes old proc chance, returns new one
        private static float InjectProcChance(float ogChance)
        {
            return ProcChance.Value;
        }
        private static void FireModifiedStickyBomb(ProjectileManager instance, GameObject prefab, Vector3 pos, Quaternion rot, GameObject owner, float damage, float force, bool crit, DamageColorIndex col, GameObject target, float speedOverride, DamageInfo info)
        {
            //if (info.procChainMask.HasModdedProc(StickyProc)) return;
            FireProjectileInfo fireProjectileInfo = default;
            fireProjectileInfo.projectilePrefab = prefab;
            fireProjectileInfo.position = pos;
            fireProjectileInfo.rotation = rot;
            fireProjectileInfo.owner = owner;
            fireProjectileInfo.damage = damage * DamageCoefficient;
            fireProjectileInfo.force = force;
            fireProjectileInfo.crit = crit;
            fireProjectileInfo.damageColorIndex = col;
            fireProjectileInfo.target = target;
            fireProjectileInfo.speedOverride = speedOverride;
            fireProjectileInfo.fuseOverride = -1f;
            fireProjectileInfo.procChainMask = info.procChainMask;

            fireProjectileInfo.procChainMask.AddModdedProc(StickyProc);

            instance.FireProjectile(fireProjectileInfo);
        }
        internal static void ErrorHookFailed(string name, Exception e)
        {
            Log.Error(name + " hook failed: " + e.Message);
        }

        private void GlobalEventManager_OnHitAllProcess(On.RoR2.GlobalEventManager.orig_OnHitAllProcess orig, GlobalEventManager self, DamageInfo damageInfo, GameObject hitObject)
        {
            orig(self, damageInfo, hitObject);

            if (!OnHitAll.Value || damageInfo.procCoefficient == 0f || damageInfo.rejected)
            {
                return;
            }
            if (!damageInfo.attacker)
            {
                return;
            }
            CharacterBody component = damageInfo.attacker.GetComponent<CharacterBody>();
            if (!component)
            {
                return;
            }
            CharacterMaster master = component.master;
            if (!master)
            {
                return;
            }
            Inventory inventory = master.inventory;
            if (!master.inventory)
            {
                return;
            }
            CharacterBody victimBody = hitObject.GetComponent<CharacterBody>();
            if (!damageInfo.procChainMask.HasModdedProc(StickyProc))
            {
                int itemCount10 = inventory.GetItemCountEffective(RoR2Content.Items.StickyBomb);
                if (itemCount10 > 0 && Util.CheckRoll(ProcChance.Value * (float)itemCount10 * damageInfo.procCoefficient, master))
                {
                    //Ever so slightly move it up so it doesnt clip in the ground
                    Vector3 position = damageInfo.position + 0.1f * Vector3.upVector;
                    Vector3 forward = victimBody ? victimBody.corePosition - position : Vector3.zero;
                    float magnitude = forward.magnitude;
                    Quaternion rotation = (magnitude != 0f) ? Util.QuaternionSafeLookRotation(forward) : UnityEngine.Random.rotationUniform;
                    float damageCoefficient7 = 1.8f;
                    float damage = Util.OnHitProcDamage(damageInfo.damage, component.damage, damageCoefficient7);
                    FireModifiedStickyBomb(ProjectileManager.instance, LegacyResourcesAPI.Load<GameObject>("Prefabs/Projectiles/StickyBomb"), position, rotation, damageInfo.attacker, damage, 100f, damageInfo.crit, DamageColorIndex.Item, null, -1f, damageInfo);
                }
            }
        }

        private void UpdateText()
        {

            string token = "ITEM_STICKYBOMB_DESC";
            string text = "<style=cIsDamage>" + ProcChance.Value +"%</style> <style=cStack>(+" + ProcChance.Value + "% per stack)</style> chance on hit to attach a <style=cIsDamage>bomb</style>" + (OnHitAll.Value ? "" : " to an enemy") + ", detonating for <style=cIsDamage>" + Damage.Value*100 + "%</style> TOTAL damage.";
            string token2 = "ITEM_STICKYBOMB_PICKUP";
            string text2 = "Chance on hit to attach a bomb to enemies";
            text2 += OnHitAll.Value ? " and terrain." : ".";

            ReplaceString(token, text);
            ReplaceString(token2, text2);
        }

        private void ReplaceString(string token, string newtext)
        {
            LanguageAPI.Add(token, newtext);
        }
    }
}

