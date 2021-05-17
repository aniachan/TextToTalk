﻿using Dalamud.Game.Internal;
using Dalamud.Game.Internal.Gui.Addon;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using System.Linq;
using System.Speech.Synthesis;
using TextToTalk.Attributes;
using TextToTalk.Talk;

namespace TextToTalk
{
    public class TextToTalk : IDalamudPlugin
    {
        private DalamudPluginInterface pluginInterface;
        private PluginCommandManager<TextToTalk> commandManager;
        private PluginConfiguration config;
        private PluginUI ui;

        private Addon talkAddonInterface;

        private SpeechSynthesizer speechSynthesizer;
        private WsServer wsServer;

        private string lastQuestText;

        public string Name => "TextToTalk";

        public void Initialize(DalamudPluginInterface pi)
        {
            this.pluginInterface = pi;

            this.config = (PluginConfiguration)this.pluginInterface.GetPluginConfig() ?? new PluginConfiguration();
            this.config.Initialize(this.pluginInterface);

            this.wsServer = new WsServer();

            this.ui = new PluginUI(this.config, this.wsServer);
            this.pluginInterface.UiBuilder.OnBuildUi += this.ui.DrawConfig;
            this.pluginInterface.UiBuilder.OnOpenConfigUi += (_, _) => this.ui.ConfigVisible = true;

            this.speechSynthesizer = new SpeechSynthesizer();
            this.pluginInterface.Framework.Gui.Chat.OnChatMessage += OnChatMessage;

            this.pluginInterface.Framework.OnUpdateEvent += OnFrameworkUpdate;

            this.commandManager = new PluginCommandManager<TextToTalk>(this, this.pluginInterface);
        }

        private unsafe void OnFrameworkUpdate(Framework framework)
        {
            if (this.talkAddonInterface == null || this.talkAddonInterface.Address == IntPtr.Zero)
            {
                this.talkAddonInterface = this.pluginInterface.Framework.Gui.GetAddonByName("Talk", 1);
                return;
            }

            var talkAddon = (AddonTalk*)this.talkAddonInterface.Address.ToPointer();
            if (talkAddon == null) return;

            var talkAddonText = TalkUtils.ReadTalkAddon(talkAddon);
            var text = talkAddonText.Text;

            if (this.lastQuestText == talkAddonText.Text) return;
            this.lastQuestText = text;

#if DEBUG
            PluginLog.Log($"NPC text found: {text}");
#endif

            if (ShouldSaySender())
            {
                text = $"{talkAddonText.Speaker} says {text}";
            }

            Say(text);
        }

        private void OnChatMessage(XivChatType type, uint id, ref SeString sender, ref SeString message, ref bool handled)
        {
            var textValue = message.TextValue;
            if (this.lastQuestText == textValue) return;

            if (sender != null && sender.TextValue != string.Empty)
            {
                if (ShouldSaySender(type))
                {
                    textValue = $"{sender.TextValue} says {textValue}";
                }
            }

#if DEBUG
            PluginLog.Log("Chat message from type {0}: {1}", type, textValue);
#endif

            if (!this.config.Enabled) return;
            if (this.config.Bad.Where(t => t.Text != "").Any(t => t.Match(textValue))) return;

            var typeAccepted = this.config.EnabledChatTypes.Contains((int)type);
            var goodMatch = this.config.Good
                .Where(t => t.Text != "")
                .Any(t => t.Match(textValue));
            if (!(this.config.EnableAllChatTypes || typeAccepted) || this.config.Good.Count > 0 && !goodMatch) return;

            Say(textValue);
        }

        private void Say(string textValue)
        {
            if (this.config.UseWebsocket)
            {
                this.wsServer.Broadcast(textValue);
#if DEBUG
                PluginLog.Log("Sent message {0} on WebSocket server.", textValue);
#endif
            }
            else
            {
                this.speechSynthesizer.Rate = this.config.Rate;
                this.speechSynthesizer.Volume = this.config.Volume;

                if (this.speechSynthesizer.Voice.Name != this.config.VoiceName)
                {
                    this.speechSynthesizer.SelectVoice(this.config.VoiceName);
                }

                this.speechSynthesizer.SpeakAsync(textValue);
            }
        }

        [Command("/canceltts")]
        [HelpMessage("Cancel all queued TTS messages.")]
        public void CancelTts(string command, string args)
        {
            if (this.config.UseWebsocket)
            {
                this.wsServer.Cancel();
                PluginLog.Log("Canceled TTS over WebSocket server.");
            }
            else
            {
                this.speechSynthesizer.SpeakAsyncCancelAll();
                PluginLog.Log("Canceled SpeechSynthesizer TTS.");
            }
        }

        [Command("/toggletts")]
        [HelpMessage("Toggle TextToTalk's text-to-speech.")]
        public void ToggleTts(string command, string args)
        {
            if (this.config.Enabled)
                DisableTts();
            else
                EnableTts();
        }

        [Command("/disabletts")]
        [HelpMessage("Disable TextToTalk's text-to-speech.")]
        public void DisableTts(string command = "", string args = "")
        {
            this.config.Enabled = false;
            var chat = this.pluginInterface.Framework.Gui.Chat;
            chat.Print("TTS disabled.");
            PluginLog.Log("TTS disabled.");
        }

        [Command("/enabletts")]
        [HelpMessage("Enable TextToTalk's text-to-speech.")]
        public void EnableTts(string command = "", string args = "")
        {
            this.config.Enabled = true;
            var chat = this.pluginInterface.Framework.Gui.Chat;
            chat.Print("TTS enabled.");
            PluginLog.Log("TTS enabled.");
        }

        [Command("/tttconfig")]
        [HelpMessage("Toggle TextToTalk's configuration window.")]
        public void ToggleConfig(string command, string args)
        {
            this.ui.ConfigVisible = !this.ui.ConfigVisible;
        }

        private bool ShouldSaySender()
        {
            return this.config.NameNpcWithSay;
        }

        private bool ShouldSaySender(XivChatType type)
        {
            return this.config.NameNpcWithSay || (int)type != (int)AdditionalChatTypes.Enum.NPCDialogue;
        }

        #region IDisposable Support
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            this.commandManager.Dispose();

            this.pluginInterface.Framework.Gui.Chat.OnChatMessage -= OnChatMessage;
            this.speechSynthesizer.Dispose();

            this.wsServer.Stop();

            this.pluginInterface.SavePluginConfig(this.config);

            this.pluginInterface.UiBuilder.OnOpenConfigUi -= (sender, args) => this.ui.ConfigVisible = true;
            this.pluginInterface.UiBuilder.OnBuildUi -= this.ui.DrawConfig;

            this.pluginInterface.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
