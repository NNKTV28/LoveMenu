using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using VWW.Clients.Curio;
using VWW.CoreLibs.ClientAPI;
using VWW.CoreLibs.ClientAPI.Social;
using VWW.CoreLibs.Network;
using VWW.CoreLibs.Network.Chat;
using VWW.CoreLibs.Network.Data;
using VWW.CoreLibs.Shared;

namespace FlyMod.Features
{
    internal class ChatHistoryEntry
    {
        public readonly string SenderName;
        public readonly string Message;

        public ChatHistoryEntry(string senderName, string message)
        {
            SenderName = senderName;
            Message = message;
        }
    }

    // Wire format for persisting HistoryBySender across game relaunches -
    // kept separate from the runtime ChatHistoryEntry (whose fields are
    // readonly) so JsonUtility always has plain mutable fields to write
    // into regardless of how it constructs objects.
    [Serializable]
    internal class PersistedChatMessage
    {
        public string SenderName;
        public string Message;
    }

    [Serializable]
    internal class PersistedChatThread
    {
        public string PartnerName;
        public List<PersistedChatMessage> Messages = new List<PersistedChatMessage>();
    }

    [Serializable]
    internal class PersistedChatHistory
    {
        public List<PersistedChatThread> Threads = new List<PersistedChatThread>();
    }

    // Auto-ignore calls the game's own real SocialManager.InitiateAddIgnore -
    // the same server-validated request a manual "Block User" click would
    // send - triggered when any incoming message contains a trigger word.
    //
    // Also keeps a per-person message log for DM (private-channel) chats,
    // persisted across relaunches via EncodeHistory/DecodeHistory.
    // ChatChannelInfo itself is IEnumerable<IChatMessage> - the game already
    // retains the full back-and-forth for a channel internally - so instead
    // of hand-recording one message at a time (which missed our own sent
    // messages, since those don't fire this event), every time this event
    // fires for a private channel the whole thread is re-read straight from
    // the channel, keyed by ChannelMemberOfWhomITryToCommunicateWith.
    //
    // A separate attempt to replay saved history into the game's own chat
    // webview (via SocialManager.ChatChannelEnter + QueueChatMessage) was
    // tried and pulled back out - it coincided with the chat input field
    // losing typed text, and without visibility into the webview's JS side
    // there was no way to be confident that was or wasn't the cause. Not
    // worth that risk for a convenience feature when the Love Menu Chat tab
    // already shows the same history reliably.
    internal class ChatController
    {
        private const int MaxMessagesPerSender = 200;

        private readonly ManualLogSource _log;

        public readonly List<string> IgnoreTriggerWords = new List<string>();

        public readonly List<string> RecentSenders = new List<string>();
        public readonly Dictionary<string, List<ChatHistoryEntry>> HistoryBySender = new Dictionary<string, List<ChatHistoryEntry>>();

        private readonly HashSet<Guid> _autoIgnoredPersonaIdsThisSession = new HashSet<Guid>();
        public bool Hooked { get; private set; }
        public string HookStatus { get; private set; } = "not hooked yet";

        public ChatController(ManualLogSource log)
        {
            _log = log;
        }

        public void TryHook()
        {
            try
            {
                SocialManager socialManager = Singleton<ClientAPI>.Current?.SocialManager;
                if (socialManager == null)
                    return;
                socialManager.ChatChannelMessage += OnChatMessageReceived;
                Hooked = true;
                HookStatus = "hooked";
            }
            catch (Exception exception)
            {
                HookStatus = "error: " + exception.Message;
                _log.LogWarning("chat hook failed: " + exception.Message);
            }
        }

        public void Unhook()
        {
            if (!Hooked)
                return;
            try
            {
                SocialManager socialManager = Singleton<ClientAPI>.Current?.SocialManager;
                if (socialManager != null)
                    socialManager.ChatChannelMessage -= OnChatMessageReceived;
            }
            catch
            {
                // best effort on shutdown
            }
        }

        private void OnChatMessageReceived(object eventSender, ChatChannelMessageEventArgs messageEventArgs)
        {
            if (string.IsNullOrEmpty(messageEventArgs.Message) || messageEventArgs.Participant == null)
                return;

            if (messageEventArgs.Channel != null && messageEventArgs.Channel.IsPrivate)
                SyncThreadFromChannel(messageEventArgs.Channel);

            string matchedTrigger = FindMatchingTrigger(messageEventArgs.Message);
            if (matchedTrigger == null)
                return;

            IgnoreSenderIfNotAlreadyIgnored(messageEventArgs.Participant, matchedTrigger);
        }

        // Closing a private tab and reopening it hands back a NEW
        // ChatChannelInfo instance for the same person, whose own message
        // list starts empty - rebuilding HistoryBySender by fully
        // re-enumerating "the" channel each time silently discarded
        // everything once that happened. Tracking how many entries have
        // already been synced from each specific channel INSTANCE and only
        // appending the new tail means a fresh instance's (short) content
        // gets added onto the existing saved thread instead of replacing it.
        private readonly Dictionary<ChatChannelInfo, int> _syncedCountByChannel = new Dictionary<ChatChannelInfo, int>();

        private void SyncThreadFromChannel(ChatChannelInfo channel)
        {
            string partnerName = channel.ChannelMemberOfWhomITryToCommunicateWith?.Name;
            if (string.IsNullOrEmpty(partnerName))
                return;

            if (!HistoryBySender.TryGetValue(partnerName, out List<ChatHistoryEntry> thread))
            {
                thread = new List<ChatHistoryEntry>();
                HistoryBySender[partnerName] = thread;
            }

            _syncedCountByChannel.TryGetValue(channel, out int alreadySyncedCount);
            int index = 0;
            foreach (IChatMessage message in channel)
            {
                index++;
                if (index <= alreadySyncedCount)
                    continue; // already recorded from this channel instance
                if (string.IsNullOrEmpty(message.Message))
                    continue;
                string senderName = message.User != null ? message.User.Name : partnerName;
                thread.Add(new ChatHistoryEntry(senderName, message.Message));
            }
            _syncedCountByChannel[channel] = index;

            if (thread.Count > MaxMessagesPerSender)
                thread.RemoveRange(0, thread.Count - MaxMessagesPerSender);

            RecentSenders.Remove(partnerName);
            RecentSenders.Insert(0, partnerName);
        }

        private string FindMatchingTrigger(string message)
        {
            foreach (string triggerWord in IgnoreTriggerWords)
            {
                if (string.IsNullOrWhiteSpace(triggerWord))
                    continue;
                if (message.IndexOf(triggerWord, StringComparison.OrdinalIgnoreCase) >= 0)
                    return triggerWord;
            }
            return null;
        }

        private void IgnoreSenderIfNotAlreadyIgnored(ChannelMember sender, string matchedTrigger)
        {
            if (!_autoIgnoredPersonaIdsThisSession.Add(sender.PersonaID))
                return; // already handled this session

            try
            {
                Singleton<ClientAPI>.Current.SocialManager.InitiateAddIgnore(sender.PersonaID);
                _log.LogInfo("Auto-ignored " + sender.Name + " (trigger: " + matchedTrigger + ")");
            }
            catch (Exception exception)
            {
                _log.LogWarning("InitiateAddIgnore failed: " + exception.Message);
            }
        }

        // RecentSenders is newest-first; persisting in that order lets
        // DecodeHistory restore the same ordering on next launch.
        public string EncodeHistory()
        {
            var container = new PersistedChatHistory();
            foreach (string senderName in RecentSenders)
            {
                if (!HistoryBySender.TryGetValue(senderName, out List<ChatHistoryEntry> thread))
                    continue;

                var persistedThread = new PersistedChatThread { PartnerName = senderName };
                foreach (ChatHistoryEntry entry in thread)
                    persistedThread.Messages.Add(new PersistedChatMessage { SenderName = entry.SenderName, Message = entry.Message });
                container.Threads.Add(persistedThread);
            }
            return JsonUtility.ToJson(container);
        }

        public void DecodeHistory(string encoded)
        {
            HistoryBySender.Clear();
            RecentSenders.Clear();
            if (string.IsNullOrEmpty(encoded))
                return;

            PersistedChatHistory container;
            try
            {
                container = JsonUtility.FromJson<PersistedChatHistory>(encoded);
            }
            catch (Exception exception)
            {
                _log.LogWarning("chat history load failed, starting fresh: " + exception.Message);
                return;
            }
            if (container?.Threads == null)
                return;

            foreach (PersistedChatThread persistedThread in container.Threads)
            {
                if (string.IsNullOrEmpty(persistedThread.PartnerName))
                    continue;

                var thread = new List<ChatHistoryEntry>();
                foreach (PersistedChatMessage message in persistedThread.Messages)
                    thread.Add(new ChatHistoryEntry(message.SenderName, message.Message));
                HistoryBySender[persistedThread.PartnerName] = thread;
                RecentSenders.Add(persistedThread.PartnerName);
            }
        }

        public string EncodeTriggers() => string.Join(";", IgnoreTriggerWords);

        public void DecodeTriggers(string encoded)
        {
            IgnoreTriggerWords.Clear();
            if (string.IsNullOrEmpty(encoded))
                return;
            foreach (string triggerWord in encoded.Split(';'))
                if (!string.IsNullOrWhiteSpace(triggerWord))
                    IgnoreTriggerWords.Add(triggerWord);
        }
    }
}
