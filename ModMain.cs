using RedLoader;
using SonsSdk;
using UnityEngine;
using HarmonyLib;

namespace FastTravel
{
    public class FastTravelMod : SonsMod
    {
        private HarmonyLib.Harmony _harmony;

        public FastTravelMod()
        {
            // Patch manually for deterministic behavior on this RedLoader version.
            HarmonyPatchAll = false;
        }

        protected override void OnInitializeMod()
        {
            _harmony = new HarmonyLib.Harmony("com.yourname.fasttravel.manual");
            _harmony.PatchAll(typeof(FastTravelMod).Assembly);
            FastTravelNetworkingRuntime.Install();
            FastTravel.UI.FastTravelUI.Initialize();

            ModMain.LogMessage("FastTravel: Mod initialized.");
            ModMain.LogMessage("FastTravel: Harmony PatchAll applied.");
        }
    }

    public static class ModMain
    {
        public static void LogMessage(string message)
        {
            string line = "[FastTravel] " + message;
            Debug.Log(line);

            // Also write to console so RedLoader's main log capture can include this output.
            try
            {
                System.Console.WriteLine(line);
            }
            catch
            {
            }
        }
    }
}
