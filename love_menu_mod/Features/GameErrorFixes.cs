using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using VWW.Clients.Curio.Performance;
using VWW.Clients.Curio.Scene;
using VWW.Clients.Curio.Scene.Loaders;
using FlyMod.Core;

namespace FlyMod.Features
{
    // Fixes for errors collected in errors.log. Each is a switch in the
    // Crashes page's experimental list, off by default.

    // "Error setting up DynamicBones! ... Cannot deserialize the current JSON
    // array into type DynBone because the type requires a JSON object".
    // An avatar's hair/cloth physics settings arrive as a list of bone
    // entries, but some items (seen on "Human Female Lovecraft") are
    // wrapped in an extra list, so the game rejects the whole thing and
    // that avatar gets no hair or cloth physics. This flattens the extra
    // lists before the game reads them. The same error came right before
    // two confirmed crashes.
    [HarmonyPatch(typeof(DynamicBoneHelper), nameof(DynamicBoneHelper.SetupDynamicBones))]
    internal static class DynamicBonesJsonFixPatch
    {
        public static bool Enabled;
        private static int _logged;

        private static readonly Type BoneArrayType =
            AccessTools.Inner(typeof(DynamicBoneHelper), "DynBone")?.MakeArrayType();
        private static int _samplesLogged;

        static void Prefix(ref string dynamicBones, Transform transform)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(dynamicBones))
                return;
            try
            {
                JArray flat;
                try
                {
                    JToken root = JToken.Parse(dynamicBones);
                    if (!(root is JArray array) || !HasNestedArray(array))
                    {
                        LogSampleIfGameWillFail(dynamicBones, transform);
                        return;
                    }
                    flat = new JArray();
                    Flatten(array, flat);
                }
                catch (Newtonsoft.Json.JsonReaderException)
                {
                    // Seen in errors.log: the settings are cut off part way
                    // ("Unexpected end of content"), so they can't be read
                    // at all. Keep every bone entry that arrived whole.
                    flat = SalvageCompleteObjects(dynamicBones);
                    if (flat.Count == 0)
                        return;
                }
                if (_logged++ < 3)
                    DebugLog.Info("DynamicBones fix: repaired settings on " + (transform != null ? transform.name : "?") +
                        ", kept " + flat.Count + " bones. Original (" + dynamicBones.Length + " chars): " +
                        (dynamicBones.Length > 800 ? dynamicBones.Substring(0, 800) + "..." : dynamicBones));
                dynamicBones = flat.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception exception)
            {
                // Leave it to the game, which logs its own error.
                DebugLog.Detail("DynamicBones fix could not read the settings: " + exception.Message);
            }
        }

        // Settings this fix doesn't recognise: try the game's own read, and
        // if it would fail, put the raw settings in errors.log (as an error,
        // so the error logger keeps it) to work out their shape.
        private static void LogSampleIfGameWillFail(string json, Transform transform)
        {
            if (BoneArrayType == null || _samplesLogged >= 5)
                return;
            try
            {
                Newtonsoft.Json.JsonConvert.DeserializeObject(json, BoneArrayType);
            }
            catch (Exception exception)
            {
                _samplesLogged++;
                Debug.LogError("[LoveMenu][BONE-JSON] Settings the DynamicBones fix does not handle yet, on " +
                    (transform != null ? transform.name : "?") + " (" + exception.Message + "): " +
                    (json.Length > 3000 ? json.Substring(0, 3000) + "..." : json));
            }
        }

        // Reads objects one by one, at any list depth, until the text breaks off.
        private static JArray SalvageCompleteObjects(string json)
        {
            var found = new JArray();
            using (var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(json)))
            {
                try
                {
                    while (reader.Read())
                    {
                        if (reader.TokenType == Newtonsoft.Json.JsonToken.StartObject)
                            found.Add(JObject.Load(reader));
                    }
                }
                catch (Newtonsoft.Json.JsonReaderException)
                {
                    // End of the usable part.
                }
            }
            return found;
        }

        private static bool HasNestedArray(JArray array)
        {
            foreach (JToken item in array)
                if (item is JArray)
                    return true;
            return false;
        }

        private static void Flatten(JArray array, JArray into)
        {
            foreach (JToken item in array)
            {
                if (item is JArray inner)
                    Flatten(inner, into);
                else if (item is JObject)
                    into.Add(item);
            }
        }
    }

    // "Failed to load DOMRenderable 'Scripted_menu_Bulava' ... result was:
    // bulava (UnityEngine.AnimatorController)". When a downloaded bundle
    // holds several assets and none has the exact expected name, the game
    // guesses the main one by file type, and ranks animation controllers
    // above .fbx models - so an object whose model is an .fbx gets its
    // animator instead and fails to load. When that happens, this picks the
    // bundle's model (a GameObject) instead: preferably one with the same
    // name, otherwise the only one.
    [HarmonyPatch(typeof(DOMLoader), "LoadFromResource")]
    internal static class WrongMainAssetFixPatch
    {
        public static bool Enabled;

        static void Postfix(DOMLoader __instance, ref GameObject __result)
        {
            if (!Enabled || __result != null)
                return;
            try
            {
                object request = __instance.ResourceHandle?.Request;
                if (request == null || !(Traverse.Create(request).Field("m_Cache").GetValue() is Dictionary<string, UnityEngine.Object> cache) || cache.Count < 2)
                    return;

                string wanted = __instance.ObjectName ?? "";
                string match = null;
                int gameObjects = 0;
                string onlyGameObject = null;
                foreach (var pair in cache)
                {
                    if (!(pair.Value is GameObject))
                        continue;
                    gameObjects++;
                    onlyGameObject = pair.Key;
                    if (wanted.Length > 0 && Path.GetFileNameWithoutExtension(pair.Key).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                        match = pair.Key;
                }
                string key = match ?? (gameObjects == 1 ? onlyGameObject : null);
                if (key == null)
                    return;

                // An exact key makes the game return that asset, instantiated.
                var method = AccessTools.Method(request.GetType(), "GetObject", new[] { typeof(string), typeof(IResourceRequester), typeof(Type) });
                if (method?.Invoke(request, new object[] { key, __instance, null }) is GameObject loaded)
                {
                    __result = loaded;
                    DebugLog.Info("Wrong-asset fix: loaded " + key + " for " + wanted);
                }
            }
            catch (Exception exception)
            {
                DebugLog.Detail("Wrong-asset fix failed: " + exception.Message);
            }
        }
    }
}
