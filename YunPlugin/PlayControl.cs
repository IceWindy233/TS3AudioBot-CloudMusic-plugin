using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.ResourceFactories;

public class PlayControl
{
    private PlayManager playManager;
    private InvokerData invoker;
    private Ts3Client ts3Client;
    private List<MusicInfo> songList = new List<MusicInfo>();
    private NLog.Logger Log;
    private Mode mode;
    private int currentPlay = 0;
    private MusicInfo currentPlayMusicInfo;
    private PlayListMeta playListMeta;
    private TaskScheduler mainScheduler;

    public PlayControl(PlayManager playManager, Ts3Client ts3Client, NLog.Logger log)
    {
        Log = log;
        this.playManager = playManager;
        this.ts3Client = ts3Client;
        mainScheduler = TaskScheduler.Current;
    }

    private async Task RunOnMainThread(Func<Task> action)
    {
        if (mainScheduler != null && TaskScheduler.Current != mainScheduler)
        {
            await Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.None, mainScheduler).Unwrap();
        }
        else
        {
            await action();
        }
    }

    public MusicInfo GetCurrentPlayMusicInfo()
    {
        return currentPlayMusicInfo;
    }

    public InvokerData Getinvoker()
    {
        return this.invoker;
    }

    public void SetInvoker(InvokerData invoker)
    {
        this.invoker = invoker;
    }

    public Mode GetMode()
    {
        return this.mode;
    }

    public void SetMode(Mode mode)
    {
        this.mode = mode;
    }

    public void SetPlayList(PlayListMeta meta, List<MusicInfo> list=null)
    {
        playListMeta = meta;
        if (list != null)
        {
            songList = new List<MusicInfo>(list);
        } else
        {
            songList = new List<MusicInfo>(meta.MusicList);
        }
        currentPlay = 0;
        if (mode == Mode.RandomPlay || mode == Mode.RandomLoopPlay)
        {
            Utils.ShuffleArrayList(songList);
        }
    }

    public void AddPlayList(PlayListMeta meta, bool append = true)
    {
        if (!append)
        {
            SetPlayList(meta);
            return;
        }

        if (meta.MusicList != null)
        {
            foreach (var music in meta.MusicList)
            {
                // Prevent duplicate by ID if possible, or just append
                if (!songList.Any(m => m.Id == music.Id))
                {
                    songList.Add(music);
                }
            }
        }
    }

    public List<MusicInfo> GetPlayList()
    {
        return songList;
    }

    public void AddMusic(MusicInfo musicInfo, bool insert = true)
    {
        songList.RemoveAll(m => m.Id == musicInfo.Id);
        if (insert)
            songList.Insert(0, musicInfo);
        else
            songList.Add(musicInfo);
    }

    public async Task PlayNextMusic()
    {
        if (songList.Count == 0)
        {
            return;
        }
        var musicInfo = GetNextMusic();
        await PlayMusic(musicInfo);
    }

    public async Task PlayMusic(MusicInfo musicInfo)
    {
        try
        {
            var invoker = Getinvoker();

            currentPlayMusicInfo = musicInfo;

            await musicInfo.InitMusicInfo();
            string musicUrl = await musicInfo.GetMusicUrl();
            Log.Info($"Music name: {musicInfo.Name}, picUrl: {musicInfo.Image}, url: {musicUrl}");

            if (musicUrl.StartsWith("error"))
            {
                await RunOnMainThread(() => ts3Client.SendChannelMessage($"音乐链接获取失败 [{musicInfo.Name}] {musicUrl}"));
                await PlayNextMusic();
                return;
            }

            await playManager.Play(invoker, new MediaPlayResource(musicUrl, musicInfo.GetMusicInfo(), await musicInfo.GetImage(), false));

            try { await RunOnMainThread(() => ts3Client.SendChannelMessage($"► 正在播放：{musicInfo.GetFullNameBBCode()}")); } catch { }

            string desc;
            if (musicInfo.InPlayList)
            {
                desc = $"[{currentPlay}/{songList.Count}] {musicInfo.GetFullName()}";
            }
            else
            {
                desc = musicInfo.GetFullName();
            }
            try { await RunOnMainThread(() => ts3Client.ChangeDescription(desc)); } catch { }
            try
            {
                await RunOnMainThread(() => MainCommands.CommandBotAvatarSet(ts3Client, musicInfo.Image));
            } catch (Exception e)
            {
                Log.Error(e, "Set avatar error");
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "PlayMusic error");
            try { await RunOnMainThread(() => ts3Client.SendChannelMessage($"播放音乐失败 [{musicInfo.Name}]")); } catch { }
            await PlayNextMusic();
        }
    }

    public List<MusicInfo> GetNextPlayList(int limit = 3)
    {
        var list = new List<MusicInfo>();
        limit = Math.Min(limit, songList.Count);
        for (int i = 0; i < limit; i++)
        {
            list.Add(songList[i]);
        }
        return list;
    }

    private MusicInfo GetNextMusic()
    {
        MusicInfo result = songList[0];
        songList.RemoveAt(0);
        if (mode == Mode.SeqLoopPlay || mode == Mode.RandomLoopPlay) // 循环的重新加入列表
        {
            songList.Add(result);
            currentPlay += 1;
        }
        else
        {
            currentPlay = 1; // 不是循环播放就固定当前播放第一首
        }

        if (mode == Mode.RandomLoopPlay) // 如果播放计次达到播放列表最大就重新排序
        {
            if (currentPlay >= songList.Count)
            {
                Utils.ShuffleArrayList(songList);
                currentPlay = 1; // 重排了就从头开始
            }
        }

        return result;
    }

    public async Task<string> GetPlayListString()
    {
        var musicList = GetNextPlayList();
        var musicInfo = GetCurrentPlayMusicInfo();
        var descBuilder = new StringBuilder();
        descBuilder.AppendLine($"\n当前正在播放：{musicInfo.GetFullNameBBCode()}");
        var modeStr = mode switch
        {
            Mode.SeqPlay => "顺序播放",
            Mode.SeqLoopPlay => "当顺序循环",
            Mode.RandomPlay => "随机播放",
            Mode.RandomLoopPlay => "随机循环",
            _ => $"未知模式{mode}",
        };
        descBuilder.AppendLine($"当前播放模式：{modeStr}");
        descBuilder.Append("播放列表 ");
        if (playListMeta != null)
        {
            descBuilder.Append($"[URL={playListMeta.Url}]{playListMeta.Name}[/URL] ");
        }
        descBuilder.AppendLine($"[{currentPlay}/{songList.Count}]");


        for (var i = 0; i < musicList.Count; i++)
        {
            var music = musicList[i];
            await music.InitMusicInfo();
            descBuilder.AppendLine($"{i + 1}: {music.GetFullNameBBCode()}");
        }

        return descBuilder.ToString();
    }

    public void Clear()
    {
        songList.Clear(); ;
    }
}
