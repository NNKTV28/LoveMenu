using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using VWW.Clients.Curio.ClientUI;
using VWW.CoreLibs.Shared;
using FlyMod.Core;

namespace FlyMod.Features
{
    // "Clicking a link in chat sometimes opens it, sometimes just puts the
    // sender's name in the chat box." The game finds the letter under the
    // mouse by re-measuring the message with a different text system (an
    // IMGUI style) than the one that drew it (uGUI Text), 30 px narrower and
    // off by one letter - on wrapped lines and near the end of links it
    // misses, and a miss counts as a click on the message: the name mention.
    //
    // This asks the chat line's own text layout (Text.cachedTextGenerator,
    // the positions of the letters as actually drawn) which letter is under
    // the mouse, and opens the link if it's one. Anything else is left to
    // the game as before.
    [HarmonyPatch]
    internal static class ChatLinkClickFix
    {
        public static bool Enabled;

        private static readonly Regex Link = new Regex(@"(https?://|vww://|www\.)[^\s<]+", RegexOptions.IgnoreCase);
        private static readonly Regex Tags = new Regex("<[^>]*>");
        private static FieldInfo _textField;
        private static MethodInfo _navigate;

        static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("VWW.Clients.Curio.ClientUI.ChatMessageBehaviour"), "OnLeftClick");

        static bool Prepare() => TargetMethod() != null;

        static bool Prefix(object __instance)
        {
            if (!Enabled)
                return true;
            try
            {
                if (_textField == null)
                    _textField = AccessTools.Field(__instance.GetType(), "m_Text");
                var text = _textField?.GetValue(__instance) as Text;
                string url = text != null ? LinkUnderMouse(text) : null;
                if (url == null)
                    return true;
                if (!Uri.TryCreate(url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + url : url, UriKind.Absolute, out Uri uri))
                    return true;
                if (_navigate == null)
                    _navigate = AccessTools.Method(typeof(UIManager), "OnScriptNavigate", new[] { typeof(Uri), typeof(string) });
                _navigate.Invoke(Singleton<UIManager>.Current, new object[] { uri, null });
                return false;
            }
            catch (Exception exception)
            {
                DebugLog.Detail("Chat link click fix failed: " + exception.Message);
                return true;
            }
        }

        private static string LinkUnderMouse(Text text)
        {
            string raw = text.text ?? "";
            if (Link.Match(raw).Success == false)
                return null;

            Canvas canvas = text.canvas;
            Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(text.rectTransform, Input.mousePosition, camera, out Vector2 local))
                return null;
            Vector2 point = local * text.pixelsPerUnit;

            TextGenerator generator = text.cachedTextGenerator;
            IList<UILineInfo> lines = generator.lines;
            IList<UICharInfo> characters = generator.characters;
            if (lines.Count == 0 || characters.Count == 0)
                return null;

            // The line under the mouse, then the letter on it.
            int line = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                float top = lines[i].topY;
                if (point.y <= top && point.y >= top - lines[i].height)
                {
                    line = i;
                    break;
                }
            }
            if (line < 0)
                return null;
            int start = lines[line].startCharIdx;
            int end = line + 1 < lines.Count ? lines[line + 1].startCharIdx : characters.Count;
            int index = -1;
            for (int i = start; i < end && i < characters.Count; i++)
            {
                UICharInfo character = characters[i];
                if (point.x >= character.cursorPos.x && point.x < character.cursorPos.x + Mathf.Max(character.charWidth, 1f))
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
                return null;

            // Depending on the Unity version the layout counts the rich-text
            // tags as (invisible) letters or leaves them out.
            string laidOut = characters.Count >= raw.Length ? raw : Tags.Replace(raw, "");
            foreach (Match match in Link.Matches(laidOut))
                if (index >= match.Index && index < match.Index + match.Length)
                    return Tags.Replace(match.Value, "").TrimEnd('.', ',', '!', '?', ')');
            return null;
        }
    }
}
