using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using System.Text.RegularExpressions;
using YoutubeTTSBot.Services;
using Microsoft.Extensions.DependencyInjection;

namespace YoutubeTTSBot.Modules
{
    [Group("voice")]
    public class AudioModule : ModuleBase<SocketCommandContext>
    {
        private readonly AudioService _audio;
        public AudioModule(IServiceProvider services)
        {
            _audio = services.GetRequiredService<AudioService>();
        }

        [Command("join", RunMode = RunMode.Async)]
        public async Task JoinAsync(IVoiceChannel channel = null)
        {
            channel = channel ?? (Context.User as IGuildUser)?.VoiceChannel;
            if (channel == null)
                await ReplyAsync("You are not connected to a voice channel for me to join");
            else
                await _audio.JoinAudio(Context.Guild, channel, Context.Channel as ITextChannel);
        }

        [Command("leave", RunMode = RunMode.Async)]
        public async Task LeaveAsync() => await _audio.LeaveAudio(Context.Guild);
        [Command("skip", RunMode = RunMode.Async)]
        public async Task SkipAsync() => await _audio.SkipAudio(Context.Guild, Context.Channel as ITextChannel);

        [Command("tts", RunMode = RunMode.Async)]
        public async Task TTSAsync([Remainder]string textInput)
        {
            string message = Regex.Replace(textInput, @"@\\w[a-zA-Z0-9()]{0,75}#[0-9]{0,4}", "").Trim().ToLower();
            if(message.Length < 1)
            {
                await ReplyAsync("The message you entered is invalid");
                return;
            }

            await _audio.AddQueue(Context.Guild, message, AudioService.AudioQueue.AudioType.TTS);
        }
    }
}
