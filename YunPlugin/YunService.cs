using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using YunPlugin.api;

namespace YunPlugin
{
    public class YunService
    {
        private PlayControl _playControl;
        private Config _config;
        private Dictionary<MusicApiType, IMusicApiInterface> _musicApis;
        private PlayManager _playManager;
        private Ts3Client _ts3Client;
        private Player _player;
        private NLog.Logger Log = YunPlugin.GetLogger("Service");

        // Dependencies injection
        public YunService(PlayControl playControl, Config config, Dictionary<MusicApiType, IMusicApiInterface> musicApis, 
                          PlayManager playManager, Ts3Client ts3Client, Player player)
        {
            _playControl = playControl;
            _config = config;
            _musicApis = musicApis;
            _playManager = playManager;
            _ts3Client = ts3Client;
            _player = player;
        }

        private IMusicApiInterface GetApiInterface(MusicApiType type = MusicApiType.None)
        {
            if (type == MusicApiType.None)
            {
                type = _config.DefaultApi;
            }
            if (!_musicApis.ContainsKey(type))
            {
                throw new CommandException("未找到对应的API", CommandExceptionReason.CommandError);
            }
            return _musicApis[type];
        }

        public IMusicApiInterface GetApiByArgs(CommandArgs args)
        {
             return args.Api ?? GetApiInterface();
        }

        public CommandArgs ProcessArgs(string args, int argsLen = 3)
        {
            var result = new CommandArgs();
            var sp = Utils.ProcessArgs(Utils.RemoveBBCode(args));
            if (sp.Length == 0)
            {
                throw new CommandException("Invalid Arguments", CommandExceptionReason.CommandError);
            }
            if (sp.Length > argsLen)
            {
                string[] newSP = new string[argsLen];
                Array.Copy(sp, newSP, argsLen - 1);
                newSP[argsLen - 1] = string.Join(" ", sp.Skip(argsLen - 1));
                sp = newSP;
            }

            if (sp.Length >= 2 && (sp.Last() == "max" || (Utils.IsNumber(sp.Last()) && sp.Length <= 4)))
            {
                if (sp.Last() == "max")
                {
                    result.Limit = 0;
                }
                else
                {
                    result.Limit = int.Parse(sp.Last());
                }
                sp = sp.Take(sp.Length - 1).ToArray();
            }

            switch (sp.Length)
            {
                case 2:
                    var api = _config.GetApiConfig(sp[0]);
                    if (api == null || !api.Enable)
                    {
                        throw new CommandException($"未找到对应的API [{sp[0]}]", CommandExceptionReason.CommandError);
                    }
                    result.Api = GetApiInterface(api.Type);
                    sp = sp.Skip(1).ToArray();
                    goto case 1;
                case 1:
                    result.Data = sp[0];
                    if (result.Api == null)
                    {
                        foreach (var item in _musicApis)
                        {
                            foreach (var a in item.Value.KeyInUrl)
                            {
                                if (result.Data.Contains(a))
                                {
                                    result.Api = item.Value;
                                    break;
                                }
                            }
                            if (result.Api != null)
                            {
                                break;
                            }
                        }
                        if (result.Api == null)
                        {
                            foreach (var item in _musicApis)
                            {
                                var input = item.Value.GetInputData(result.Data);
                                if (input.Type != MusicUrlType.None)
                                {
                                    result.Api = item.Value;
                                    result.InputData = input;
                                    break;
                                }
                            }
                        }
                    }
                    if (result.Api == null)
                    {
                        result.Api = GetApiInterface();
                    }
                    if (result.InputData == null)
                    {
                        result.InputData = result.Api.GetInputData(result.Data);
                    }

                    break;
            }

            return result;
        }

        // --- Core Business Logic ---

        public async Task<string> Play(CommandArgs input)
        {
            var api = GetApiByArgs(input);
            var raw = input.Data;
            var inputData = input.InputData;
            
            if (inputData.Type == MusicUrlType.PlayList)
            {
                return await PlayPlaylist(input);
            }
            else if (inputData.Type == MusicUrlType.Album)
            {
                return await PlayAlbum(input);
            }

            MusicInfo music;
            if (inputData.Type == MusicUrlType.None)
            {
                var song = await api.SearchMusic(raw, 1);
                if (song.Count == 0) return "未找到歌曲";
                music = song[0];
            }
            else
            {
                music = await api.GetMusicInfo(inputData.Id);
            }

            if (_config.PlayMode != Mode.SeqPlay && _config.PlayMode != Mode.RandomPlay)
            {
                _playControl.AddMusic(music, false);
            }
            
            await _playControl.PlayMusic(music);
            return null;
        }

        public async Task<string> Add(CommandArgs input)
        {
            var api = GetApiByArgs(input);
            var raw = input.Data;
            var inputData = input.InputData;

            MusicInfo music;
            if (inputData.Type == MusicUrlType.None)
            {
                var song = await api.SearchMusic(raw, 1);
                if (song.Count == 0) return "未找到歌曲";
                music = song[0];
            }
            else
            {
                music = await api.GetMusicInfo(inputData.Id);
            }
            
            _playControl.AddMusic(music);
            return "已添加到下一首播放";
        }

        public async Task<string> PlayPlaylist(CommandArgs input, bool append = false)
        {
            var api = GetApiByArgs(input);
            var raw = input.Data;
            var inputData = input.InputData;
            
            // If the URL is already a playlist, use its ID. 
            // Otherwise, if it's None (text search) or a different URL type (forced search), do search.
            string listId = (inputData.Type == MusicUrlType.PlayList) ? inputData.Id : null;

            if (listId == null)
            {
                var playlist = await api.SearchPlaylist(raw, 1);
                if (playlist.Count == 0) return "未找到歌单";
                listId = playlist[0].Id;
            }
            
            var playListMeta = await api.GetPlayList(listId, input.Limit);
            if (append)
            {
                _playControl.AddPlayList(playListMeta, true);
                return $"已添加歌单到队列：{playListMeta.Name} [{playListMeta.MusicList.Count}]";
            }
            else
            {
                _playControl.SetPlayList(playListMeta);
                await _playControl.PlayNextMusic();
                return $"开始播放歌单：{playListMeta.Name} [{playListMeta.MusicList.Count}]";
            }
        }

        public async Task<string> PlayAlbum(CommandArgs input, bool append = false)
        {
            var api = GetApiByArgs(input);
            var raw = input.Data;
            var inputData = input.InputData;
            
            string id = (inputData.Type == MusicUrlType.Album) ? inputData.Id : null;

            if (id == null)
            {
                var album = await api.SearchAlbum(raw, 1);
                if (album.Count == 0) return "未找到专辑";
                id = album[0].Id.ToString();
            }

            var albumDetail = await api.GetAlbums(id, input.Limit);
            if (append)
            {
                _playControl.AddPlayList(albumDetail, true);
                return $"已添加专辑到队列：{albumDetail.Name} [{albumDetail.MusicList.Count}]";
            }
            else
            {
                _playControl.SetPlayList(albumDetail);
                await _playControl.PlayNextMusic();
                return $"开始播放专辑：{albumDetail.Name} [{albumDetail.MusicList.Count}]";
            }
        }

        public async Task<string> Login(string apiName, string[] args)
        {
             var apiContainer = _config.GetApiConfig(apiName);
             if (apiContainer == null) return $"未找到对应的API [{apiName}]";
             
             var apiInterface = GetApiInterface(apiContainer.Type);
             return await apiInterface.Login(args);
        }

        public async Task Stop()
        {
            if (_playManager.IsPlaying)
            {
                await _playManager.Stop();
            }
        }

        public void SetPaused(bool paused)
        {
            if (_player != null)
            {
                _player.Paused = paused;
            }
        }
        
        public bool IsPaused()
        {
             return _player?.Paused ?? false;
        }

        public void Clear()
        {
            _playControl.Clear();
            _ = Stop(); // Fire and forget stop
        }

        public async Task MoveToChannel(int channelId, string password = null)
        {
            await _ts3Client.MoveTo(new TSLib.ChannelId((ulong)channelId), password);
        }

        public async Task<string> SetMode(int mode)
        {
            if (Enum.IsDefined(typeof(Mode), mode))
            {
                Mode playMode = (Mode)mode;
                _playControl.SetMode(playMode);
                _config.PlayMode = playMode;
                _config.Save();

                return playMode switch
                {
                    Mode.SeqPlay => "当前播放模式为顺序播放",
                    Mode.SeqLoopPlay => "当前播放模式为顺序循环",
                    Mode.RandomPlay => "当前播放模式为随机播放",
                    Mode.RandomLoopPlay => "当前播放模式为随机循环",
                    _ => "请输入正确的播放模式",
                };
            }
            return "请输入正确的播放模式";
        }

        public async Task PlayNextMusic()
        {
             await _playControl.PlayNextMusic();
        }

        // Status & Info

        public async Task<List<object>> GetPluginStatus()
        {
            var list = new List<object>();
            foreach (var api in _musicApis)
            {
                object userInfoData = null;
                try
                {
                    var userInfo = await api.Value.GetUserInfo();
                    if (userInfo != null)
                    {
                        userInfoData = new { name = userInfo.Name, url = userInfo.Url, extra = userInfo.Extra };
                    }
                }
                catch { }

                list.Add(new
                {
                    type = api.Key.ToString(),
                    name = api.Value.Name,
                    server = api.Value.GetApiServerUrl(),
                    user = userInfoData
                });
            }
            return list;
        }
        
        public object GetPlaybackStatus()
        {
            var current = _playControl.GetCurrentPlayMusicInfo();
            var list = _playControl.GetNextPlayList(50);
            var mode = _playControl.GetMode();
            
            return new
            {
                current = current == null ? null : new { name = current.Name, artist = current.GetAuthor() },
                playlist = list.ConvertAll(x => new { name = x.Name, artist = x.GetAuthor() }),
                mode = mode.ToString(),
                modeVal = (int)mode,
                paused = IsPaused()
            };
        }
        public string ReloadConfig()
        {
             // For now, we rely on YunBot to handle actual reloading logic or move it here later.
             // Given the complexity of reloading (disposing APIs, etc.), it's better kept in YunBot or refactored heavily.
             // As requested, we just return a message prompting chat usage or indicating it's done via bot command.
             return "配置已重新加载 (Web端暂不支持热重载逻辑，请使用 !yun reload)"; 
        }

    }

    public class CommandArgs
    {
        public IMusicApiInterface Api { get; set; }
        public MusicApiInputData InputData { get; set; }
        public string Data { get; set; }
        public int Limit { get; set; }

        public CommandArgs()
        {
            Api = null;
            InputData = null;
            Data = null;
            Limit = 100;
        }

        public override string ToString()
        {
            return $"Api: {Api.Name} Data: {Data} Limit: {Limit}";
        }
    }
}