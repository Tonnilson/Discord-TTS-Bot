using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.Audio;
using System.IO;
using Google.Cloud.TextToSpeech.V1;
using Discord.WebSocket;
using System.Collections.Concurrent;
using NAudio.Wave;
using System.Threading;

namespace YoutubeTTSBot.Services
{
    public class AudioService
    {
        private static readonly ConcurrentDictionary<ulong, AudioClient> _channels = new ConcurrentDictionary<ulong, AudioClient>();
        private static TextToSpeechClient _google;

        public AudioService()
        {
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", "tts_auth.json");
        }

        public class AudioQueue
        {
            private static readonly ConcurrentQueue<AudioInQueue> _queue = new ConcurrentQueue<AudioInQueue>();
            private static bool _taskIsRunning = false;
            private static CancellationTokenSource _cancel;

            private class AudioInQueue
            {
                public ulong Server { get; set; }
                public string Message { get; set; }
                public AudioType Type { get; set; }
            }

            public enum AudioType
            {
                TTS,
                AUDIO
            }

            public void Enqueue(ulong serverId, string message, AudioType type)
            {
                _queue.Enqueue(new AudioInQueue { Server = serverId, Message = message, Type = type });
                if (!_taskIsRunning)
                    Task.Run(ProcessQueuedItemsAsync);
            }

            public async void SkipQueue(ITextChannel channel)
            {
                if (_cancel != null)
                    _cancel.Cancel();
                else
                    await channel.SendMessageAsync("There is nothing in the queue to skip");
            }

            private static async Task ProcessQueuedItemsAsync()
            {
                _taskIsRunning = true;
                while(true)
                {
                    if (!_queue.Any()) break;
                    AudioInQueue item;
                    if(_queue.TryDequeue(out item))
                    {
                        _cancel = new CancellationTokenSource();
                        try
                        {
                            if (item.Type == AudioType.TTS)
                                await ProcessTextToSpeech(item.Server, item.Message, _cancel.Token);
                        } catch (Exception ex) { Console.WriteLine(ex.Message); }
                        finally { _cancel.Dispose(); }
                    }
                }
                _taskIsRunning = false;
            }
        }

        private class AudioClient
        {
            public ulong ChannelId { get; set; }
            public IAudioClient Client { get; set; }
            public AudioOutStream AudioDevice { get; set; }
            public AudioQueue Queue { get; set; }
        }

        public async Task AddQueue(IGuild server, string message, AudioQueue.AudioType type)
        {
            AudioClient voice;
            if (_channels.TryGetValue(server.Id, out voice))
                voice.Queue.Enqueue(server.Id, message, type);

            await Task.CompletedTask;
        }

        public async Task SkipAudio(IGuild server, ITextChannel textChannel)
        {
            AudioClient voice;
            if (_channels.TryGetValue(server.Id, out voice))
                voice.Queue.SkipQueue(textChannel);

            await Task.CompletedTask;
        }

        public async Task JoinAudio(IGuild server, IVoiceChannel voiceChannel, ITextChannel textChannel)
        {
            if (voiceChannel.Guild.Id != server.Id) return;
            AudioClient voice;

            // Checkk if currently connected vc
            if(_channels.TryGetValue(server.Id, out voice))
            {
                if(voiceChannel.Id == voice.ChannelId && voice.Client.ConnectionState == ConnectionState.Connected)
                {
                    await textChannel.SendMessageAsync("Already connected to the same channel.");
                    return;
                }

                var oldVoice = voice; // Make a temporary copy of the old audioclient
                var client = await voiceChannel.ConnectAsync(); // This should disconnect from any current channel and connect to newly desired
                voice.AudioDevice = null;
                voice.Client = client;
                voice.ChannelId = voiceChannel.Id;
                _channels.TryUpdate(server.Id, voice, oldVoice);
                return;
            }

            voice = new AudioClient
            {
                ChannelId = voiceChannel.Id,
                Queue = new AudioQueue()
            };

            var audioClient = await voiceChannel.ConnectAsync();
            voice.Client = audioClient;
            if (!_channels.TryAdd(server.Id, voice))
                await textChannel.SendMessageAsync("Failed to add to our hashmap, internal error");
        }

        public async Task LeaveAudio(IGuild server)
        {
            AudioClient voice;
            if (_channels.TryRemove(server.Id, out voice))
                voice.Client.Dispose();

            await Task.CompletedTask;
        }

        private static string MD5(string input)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hashBytes = md5.ComputeHash(Encoding.ASCII.GetBytes(input));
                return Convert.ToHexString(hashBytes);
            }
        }

        private static async Task ProcessTextToSpeech(ulong server, string message, CancellationToken token)
        {
            if (!Directory.Exists("tts"))
                Directory.CreateDirectory("tts");

            string file_Name = Path.Combine("tts", string.Format("{0}.mp3", MD5(message.ToLower())));

            if(!File.Exists(file_Name))
            {
                if (_google == null)
                    _google = await TextToSpeechClient.CreateAsync();

                var response = await _google.SynthesizeSpeechAsync(new SynthesizeSpeechRequest
                {
                    Input = new SynthesisInput { Text = message },
                    Voice = new VoiceSelectionParams { LanguageCode = "en-AU", Name = "en-AU-Neural2-A" },
                    AudioConfig = new AudioConfig { AudioEncoding = AudioEncoding.Mp3 }
                });

                using (var stream = File.Create(file_Name))
                    response.AudioContent.WriteTo(stream);
            }

            await SendMp3AudioAsync(server, file_Name, token);
        }

        private static async Task SendMp3AudioAsync(ulong server, string audio_file, CancellationToken token)
        {
            AudioClient voice;
            if (!File.Exists(audio_file)) return;

            if(_channels.TryGetValue(server, out voice))
            {
                using (var audio = new Mp3FileReader(audio_file))
                {
                    var stream = new WaveFormatConversionStream(new WaveFormat(48000, 16, 2), audio);

                    if (voice.AudioDevice == null)
                        voice.AudioDevice = voice.Client.CreatePCMStream(AudioApplication.Mixed, 98304, 200);

                    try { await stream.CopyToAsync(voice.AudioDevice, 1920, token); } finally { await voice.AudioDevice.FlushAsync(); }
                }
            }
        }
    }
}
