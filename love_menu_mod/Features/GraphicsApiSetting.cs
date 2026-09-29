using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using FlyMod.Core;

namespace FlyMod.Features
{
    // Which graphics API the game starts with. Unity picks it at launch, so
    // this can't change a running game: the choice is saved to
    // BepInEx\LoveMenu\graphics.txt and the launcher passes the matching
    // Unity flag (-force-d3d12 / -force-vulkan, -force-gfx-jobs native)
    // when it starts the game next time.
    //
    // The game's engine contains DirectX 11, DirectX 12 and Vulkan and ships
    // DirectX 12 upscalers, but runs on DirectX 11 by default. Other APIs
    // are experimental: they may be faster, slower, or look wrong.
    //
    // Safety net: after the game has run for StableSeconds on the chosen API,
    // graphics-ok.txt is written. If the launcher finds the last non-default
    // launch never got that far, it goes back to DirectX 11 by itself.
    internal static class GraphicsApiSetting
    {
        public enum Api { DirectX11, DirectX12, Vulkan }

        public static readonly string[] Names = { "DirectX 11", "DirectX 12", "Vulkan" };
        private static readonly string[] Keys = { "d3d11", "d3d12", "vulkan" };
        private const float StableSeconds = 60f;

        public static Api Chosen { get; private set; } = Api.DirectX11;
        public static bool NativeGraphicsJobs { get; private set; }
        public static string Folder;

        private static bool _okWritten;

        public static string Running
        {
            get
            {
                switch (SystemInfo.graphicsDeviceType)
                {
                    case GraphicsDeviceType.Direct3D11: return "DirectX 11";
                    case GraphicsDeviceType.Direct3D12: return "DirectX 12";
                    case GraphicsDeviceType.Vulkan: return "Vulkan";
                    default: return SystemInfo.graphicsDeviceType.ToString();
                }
            }
        }

        public static string RunningThreading
        {
            get
            {
                try { return SystemInfo.renderingThreadingMode.ToString(); }
                catch { return "unknown"; }
            }
        }

        public static bool RestartNeeded =>
            Running != Names[(int)Chosen] ||
            (Chosen != Api.DirectX11 && NativeGraphicsJobs != RunningThreading.IndexOf("Native", StringComparison.OrdinalIgnoreCase) >= 0);

        public static void Load(string folder)
        {
            Folder = folder;
            try
            {
                string path = Path.Combine(folder, "graphics.txt");
                if (!File.Exists(path))
                    return;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string[] parts = raw.Split(new[] { '=' }, 2);
                    if (parts.Length != 2)
                        continue;
                    string key = parts[0].Trim(), value = parts[1].Trim();
                    if (key == "api")
                        Chosen = (Api)Math.Max(0, Array.IndexOf(Keys, value));
                    else if (key == "gfxjobs")
                        NativeGraphicsJobs = value == "native";
                }
            }
            catch (Exception exception)
            {
                DebugLog.Warn("Could not read graphics.txt: " + exception.Message);
            }
            DebugLog.Info("Graphics: running " + Running + " (" + RunningThreading + "), chosen for next launch " + Names[(int)Chosen] +
                (NativeGraphicsJobs ? " with native graphics jobs" : ""));
        }

        public static void Choose(Api api, bool nativeGraphicsJobs)
        {
            Chosen = api;
            NativeGraphicsJobs = api != Api.DirectX11 && nativeGraphicsJobs;
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(Path.Combine(Folder, "graphics.txt"),
                    "# Written by Love Menu. Read by the launcher when it starts the game.\r\n" +
                    "api=" + Keys[(int)Chosen] + "\r\n" +
                    "gfxjobs=" + (NativeGraphicsJobs ? "native" : "default") + "\r\n");
            }
            catch (Exception exception)
            {
                DebugLog.Warn("Could not save graphics.txt: " + exception.Message);
            }
        }

        // Tells the launcher this API works on this PC.
        public static void Tick()
        {
            if (_okWritten || Folder == null || Time.realtimeSinceStartup < StableSeconds)
                return;
            _okWritten = true;
            try
            {
                File.WriteAllText(Path.Combine(Folder, "graphics-ok.txt"), Running + " " + DateTime.UtcNow.ToString("o"));
            }
            catch
            {
                // best effort
            }
        }
    }
}
