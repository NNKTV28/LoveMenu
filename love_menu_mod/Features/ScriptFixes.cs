using System;
using FlyMod.Core;

namespace FlyMod.Features
{
    // Fixes for bugs in the game's world scripts (the JavaScript the server
    // sends: character builder, quests, quizzes...). Each script passes
    // through WorldScriptRunPatch just before it runs, so a fix can correct
    // its text there. Every fix checks the text it expects is really there
    // and leaves the script alone otherwise, so a game update can't be
    // broken by a stale fix.
    internal static class ScriptFixes
    {
        // "Items won't come off" - pkg://characterbuilder/content/Clothing.js.
        // An inventory item remembers the slot of the tab it was last shown
        // in, and the eight Accessories tabs all show the same items. Wear a
        // necklace in Accessories 2, open Accessories 5 and untick it, and the
        // script removes whatever is in slot 5 - nothing - so the necklace
        // stays on and can't be put on again ("already present"). The fix
        // removes the item from the slot it is actually worn in.
        public static bool ClothingRemoveFix;
        public static int Applied { get; private set; }

        // Each Accessories tab is a radio group: ticking an item first unticks
        // the others in the list - including accessories worn in other slots.
        // Those automatic unticks must not take anything off (the first
        // version of this fix did, and equipping one accessory removed the
        // rest). So an untick for an item worn in another slot waits 60 ms:
        // if something was put on meanwhile it was the automatic kind and is
        // ignored; otherwise it was the player, and the item comes off the
        // slot it's really in. Unticks in the item's own slot work as before.
        private const string ClothingRemoveCall = "Component.RemoveOverlay(sender.Hookpoint)";
        private const string ClothingRemoveFixed = "__loveMenuRemove(typeof me !== 'undefined' ? me : this, sender)";
        private const string ClothingEquipCall = "Component.SetOverlay(sender.Hookpoint, sender.Item.ItemID);";
        private const string ClothingEquipFixed = "__loveMenuEquipAt = Date.now(); Component.SetOverlay(sender.Hookpoint, sender.Item.ItemID);";
        private const string ClothingHelper =
            "var __loveMenuEquipAt = 0;\n" +
            "function __loveMenuRemove(owner, sender) {\n" +
            "    var worn = null;\n" +
            "    try {\n" +
            "        var map = owner && owner._hookpointItemIDs;\n" +
            "        if (map && sender && sender.Item)\n" +
            "            for (const [hookpoint, itemID] of map)\n" +
            "                if (itemID == sender.Item.ItemID) { worn = hookpoint; break; }\n" +
            "    } catch (e) { }\n" +
            "    if (worn == null || worn == sender.Hookpoint) {\n" +
            "        Component.RemoveOverlay(sender.Hookpoint);\n" +
            "        return;\n" +
            "    }\n" +
            "    var asked = Date.now();\n" +
            "    Script.StartTimer(function () {\n" +
            "        if (__loveMenuEquipAt >= asked - 5) return;\n" +
            "        var map = owner && owner._hookpointItemIDs;\n" +
            "        if (map && map.get(worn) == sender.Item.ItemID)\n" +
            "            Component.RemoveOverlay(worn);\n" +
            "    }, 60);\n" +
            "}\n";

        // "When I change channel, both channels show on top of each other" -
        // pkg://chat/chat/Channel.js and ChatWindow.js. Each channel has its
        // own message panel; switching hides the old one and shows the new
        // one. But a channel created later (joining a group, a private
        // message opening a tab, channels re-joined after a room change or
        // reconnect) makes a panel that starts visible and is never hidden,
        // so it's drawn over the channel you're reading. The fix hides a new
        // panel unless it's the active channel, and after every channel
        // switch makes sure only the active channel's panel is visible.
        public static bool ChatOverlapFix;

        private const string ChannelCreatedAnchor = "this._surface.LinkColor = \"#87ceeb\";";
        private const string ChannelCreatedFix =
            " try { let __w = this.group.window; if (__w && __w.activeChannel !== this) this._surface.Visible = false; } catch (__e) { }";
        private const string ChannelSwitchedAnchor = "this.activeChannel.focus = true;";
        private const string ChannelSwitchedFix =
            " try { for (let __g in this._channelGroups) for (let __c of this._channelGroups[__g].channels)" +
            " if (__c !== this.activeChannel && __c._surface) __c._surface.Visible = false; } catch (__e) { }";

        // "Player is busy" until F5 - pkg://groupmanager/Main.js. The group
        // window (shared poses, dances, other actions) finds "you" in the
        // member list for its Exit button, but compares the wrong values:
        // only the group leader is ever found, so for everyone else Exit is
        // disabled. They can't leave, the server keeps them in the group,
        // and every new action or invite says they're in another group.
        public static bool GroupExitFix;

        private const string GroupExitFind = "GROUP.members.find(obj => PERSONA_ID === groupMasterId)";
        private const string GroupExitFixed = "GROUP.members.find(obj => obj.PersonaID === PERSONA_ID)";

        public static void Apply(ref string script, string source)
        {
            if (string.IsNullOrEmpty(script) || string.IsNullOrEmpty(source))
                return;
            try
            {
                if (ChatOverlapFix && source.EndsWith("chat/chat/Channel.js", StringComparison.OrdinalIgnoreCase) &&
                    CountOf(script, ChannelCreatedAnchor) == 1)
                {
                    script = script.Replace(ChannelCreatedAnchor, ChannelCreatedAnchor + ChannelCreatedFix);
                    Applied++;
                    DebugLog.Info("Script fix: chat overlap fix applied to " + source);
                }
                if (ChatOverlapFix && source.EndsWith("chat/chat/ChatWindow.js", StringComparison.OrdinalIgnoreCase) &&
                    CountOf(script, ChannelSwitchedAnchor) == 1 && script.Contains("_channelGroups"))
                {
                    script = script.Replace(ChannelSwitchedAnchor, ChannelSwitchedAnchor + ChannelSwitchedFix);
                    Applied++;
                    DebugLog.Info("Script fix: chat overlap fix applied to " + source);
                }

                if (GroupExitFix && source.EndsWith("groupmanager/Main.js", StringComparison.OrdinalIgnoreCase) &&
                    CountOf(script, GroupExitFind) == 1)
                {
                    script = script.Replace(GroupExitFind, GroupExitFixed);
                    Applied++;
                    DebugLog.Info("Script fix: group exit fix applied to " + source);
                }

                if (ClothingRemoveFix && source.EndsWith("content/Clothing.js", StringComparison.OrdinalIgnoreCase) &&
                    script.Contains(ClothingRemoveCall) && script.Contains("_hookpointItemIDs"))
                {
                    int count = CountOf(script, ClothingRemoveCall);
                    script = script.Replace(ClothingRemoveCall, ClothingRemoveFixed);
                    script = script.Replace(ClothingEquipCall, ClothingEquipFixed);
                    script = InsertAfterImports(script, ClothingHelper);
                    Applied++;
                    DebugLog.Info("Script fix: clothing remove fix applied to " + source + " (" + count + " place(s))");
                }
            }
            catch (Exception exception)
            {
                DebugLog.Warn("Script fix failed for " + source + ": " + exception.Message);
            }
        }

        private static int CountOf(string text, string part)
        {
            int count = 0;
            for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        // A module's import lines must stay first.
        private static string InsertAfterImports(string script, string code)
        {
            int position = 0;
            while (true)
            {
                int lineEnd = script.IndexOf('\n', position);
                if (lineEnd < 0)
                    break;
                string line = script.Substring(position, lineEnd - position).Trim();
                if (line.StartsWith("import ", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal) || line.Length == 0)
                {
                    position = lineEnd + 1;
                    continue;
                }
                break;
            }
            return script.Insert(position, code);
        }
    }
}
