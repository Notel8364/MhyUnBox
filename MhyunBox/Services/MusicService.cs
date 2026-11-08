using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using MhyunBox.Settings;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace MhyunBox.Services;

public static class MusicService
{
	private static readonly string[] AudioExtensions = new string[7] { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma" };

	private static readonly string[] VideoExtensions = new string[5] { ".mp4", ".mkv", ".mov", ".wmv", ".webm" };

	private static MediaPlayer? _player;

	private static MediaSource? _source;

	private static readonly Random Random = new Random();

	private static DispatcherQueue? _dispatcher;

	private static long _lastSwitchTicks;

	private static MediaSource? _pendingSource;

	private static string? _pendingPath;

	private static string? _scanKey;

	private static List<string>? _mp4Cache;

	private static string? _mp4CacheKey;

	private static FileSystemWatcher? _musicWatcher;

	private static FileSystemWatcher? _videoWatcher;

	private static Timer? _reloadDebounce;

	private static readonly object ReloadLock = new object();

	public const string NoMusicMarker = "<no-music>";

	private static volatile bool _switchingTrack;

	public static long LastFlyoutClickTicks;

	public static bool RestartVideoRequested { get; set; }

	public static string DefaultMusicFolder => Path.Combine(AppContext.BaseDirectory, "Musics");

	public static bool IsVideoSource => SettingsManager.Instance.MusicSource == "video";

	public static string? VideoFolder
	{
		get
		{
			string videoFolder = SettingsManager.Instance.VideoFolder;
			if (!string.IsNullOrWhiteSpace(videoFolder))
			{
				return videoFolder;
			}
			return null;
		}
	}

	public static string MusicFolder
	{
		get
		{
			string musicFolder = SettingsManager.Instance.MusicFolder;
			if (!string.IsNullOrWhiteSpace(musicFolder))
			{
				return musicFolder;
			}
			return DefaultMusicFolder;
		}
	}

	public static List<string> KnownMusic { get; } = new List<string>();

	public static string? CurrentPath { get; private set; }

	public static bool NoMusicMode { get; private set; }

	public static bool IsPlaying
	{
		get
		{
			MediaPlayer player = _player;
			if ((object)player != null)
			{
				return player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
			}
			return false;
		}
	}

	public static MediaPlayer Player
	{
		get
		{
			EnsurePlayer();
			return _player;
		}
	}

	public static event Action? StateChanged;

	public static void ApplySettings()
	{
		if ((object)_dispatcher == null)
		{
			_dispatcher = DispatcherQueue.GetForCurrentThread();
		}
		EnsureLibraryWatcher();
		RefreshKnownMusic();
		AppSettings instance = SettingsManager.Instance;
		if (_player != null)
		{
			_player.Volume = Math.Clamp(instance.MusicVolume, 0.0, 1.0);
		}
		if (KnownMusic.Count == 0)
		{
			if (CurrentPath != null)
			{
				Stop();
			}
		}
		else
		{
			if (NoMusicMode)
			{
				return;
			}
			string text = ResolveStartTarget();
			if (text == null)
			{
				if (CurrentPath != null)
				{
					Stop();
				}
			}
			else if (SettingsManager.Instance.AutoPlayOnStartup && !SettingsManager.Instance.MusicPaused && (CurrentPath == null || !string.Equals(CurrentPath, text, StringComparison.OrdinalIgnoreCase)))
			{
				Play(text);
			}
		}
	}

	private static string? ResolveStartTarget()
	{
		if (CurrentPath != null && File.Exists(CurrentPath))
		{
			return CurrentPath;
		}
		if (IsVideoSource)
		{
			string text = ResolveBackgroundVideoPath();
			if (text != null && KnownMusic.Contains(text))
			{
				return text;
			}
		}
		string musicPath = SettingsManager.Instance.MusicPath;
		if (!string.IsNullOrWhiteSpace(musicPath))
		{
			foreach (string item in KnownMusic)
			{
				if (string.Equals(item, musicPath, StringComparison.OrdinalIgnoreCase) && (IsVideoSource || !IsVideoFile(item)))
				{
					return item;
				}
			}
		}
		if (!IsVideoSource)
		{
			foreach (string item2 in KnownMusic)
			{
				if (!IsVideoFile(item2))
				{
					return item2;
				}
			}
		}
		if (KnownMusic.Count <= 0)
		{
			return null;
		}
		return KnownMusic[0];
	}

	private static string BuildScanKey()
	{
		AppSettings instance = SettingsManager.Instance;
		return $"{MusicFolder}|{instance.VideoFolder ?? ""}|{instance.BackgroundPath ?? ""}";
	}

	public static void InvalidateScanCache()
	{
		_scanKey = null;
		_mp4Cache = null;
		_mp4CacheKey = null;
	}

	public static void ReloadLibrary()
	{
		InvalidateScanCache();
		RefreshKnownMusic();
		NotifyStateChanged();
	}

	private static void EnsureLibraryWatcher()
	{
		try
		{
			_musicWatcher?.Dispose();
			_musicWatcher = null;
			_videoWatcher?.Dispose();
			_videoWatcher = null;
			string musicFolder = MusicFolder;
			if (Directory.Exists(musicFolder))
			{
				_musicWatcher = CreateLibraryWatcher(musicFolder);
			}
			string videoFolder = VideoFolder;
			if (!string.IsNullOrWhiteSpace(videoFolder) && !string.Equals(videoFolder, musicFolder, StringComparison.OrdinalIgnoreCase) && Directory.Exists(videoFolder))
			{
				_videoWatcher = CreateLibraryWatcher(videoFolder);
			}
		}
		catch
		{
		}
	}

	private static FileSystemWatcher CreateLibraryWatcher(string folder)
	{
		FileSystemWatcher fileSystemWatcher = new FileSystemWatcher(folder);
		fileSystemWatcher.IncludeSubdirectories = true;
		fileSystemWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite;
		fileSystemWatcher.Created += OnLibraryFileChanged;
		fileSystemWatcher.Changed += OnLibraryFileChanged;
		fileSystemWatcher.Renamed += OnLibraryFileChanged;
		fileSystemWatcher.Deleted += OnLibraryFileChanged;
		fileSystemWatcher.Error += OnLibraryWatcherError;
		fileSystemWatcher.EnableRaisingEvents = true;
		return fileSystemWatcher;
	}

	private static void OnLibraryWatcherError(object sender, ErrorEventArgs e)
	{
		LogHelper.Log("音乐库监视器错误: " + (e.GetException()?.Message ?? "未知"));
	}

	private static void OnLibraryFileChanged(object sender, FileSystemEventArgs e)
	{
		try
		{
			string value = Path.GetExtension(e.FullPath).ToLowerInvariant();
			if (AudioExtensions.Contains(value) || VideoExtensions.Contains(value))
			{
				ScheduleLibraryReload();
			}
		}
		catch
		{
		}
	}

	private static void ScheduleLibraryReload()
	{
		lock (ReloadLock)
		{
			if (_reloadDebounce == null)
			{
				_reloadDebounce = new Timer(delegate
				{
					FireLibraryReload();
				}, null, 1500, -1);
			}
			else
			{
				_reloadDebounce.Change(1500, -1);
			}
		}
	}

	private static void FireLibraryReload()
	{
		bool flag = false;
		lock (ReloadLock)
		{
			if (_reloadDebounce != null)
			{
				_reloadDebounce.Dispose();
				_reloadDebounce = null;
				flag = true;
			}
		}
		if (flag)
		{
			DispatcherQueue dispatcher = _dispatcher;
			if (dispatcher != null)
			{
				dispatcher.TryEnqueue(ReloadLibrary);
			}
			else
			{
				ReloadLibrary();
			}
		}
	}

	public static void RefreshKnownMusic()
	{
		string text = BuildScanKey();
		if (string.Equals(text, _scanKey, StringComparison.Ordinal))
		{
			return;
		}
		_scanKey = text;
		KnownMusic.Clear();
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			Directory.CreateDirectory(MusicFolder);
			foreach (string item in Directory.EnumerateFiles(MusicFolder, "*", SearchOption.AllDirectories))
			{
				if (AudioExtensions.Contains(Path.GetExtension(item).ToLowerInvariant()))
				{
					Add(item);
				}
			}
		}
		catch
		{
		}
		try
		{
			string videoFolder = SettingsManager.Instance.VideoFolder;
			if (!string.IsNullOrWhiteSpace(videoFolder))
			{
				Directory.CreateDirectory(videoFolder);
				foreach (string item2 in Directory.EnumerateFiles(videoFolder, "*", SearchOption.AllDirectories))
				{
					if (VideoExtensions.Contains(Path.GetExtension(item2).ToLowerInvariant()))
					{
						Add(item2);
					}
				}
			}
		}
		catch
		{
		}
		string text2 = ResolveBackgroundVideoPath();
		if (text2 != null)
		{
			Add(text2);
		}
		ApplySavedOrder();
		void Add(string path)
		{
			try
			{
				string fullPath = Path.GetFullPath(path);
				if (seen.Add(fullPath))
				{
					KnownMusic.Add(fullPath);
				}
			}
			catch
			{
			}
		}
	}

	public static string? ResolveBackgroundVideoPath()
	{
		string backgroundPath = SettingsManager.Instance.BackgroundPath;
		if (string.IsNullOrWhiteSpace(backgroundPath))
		{
			return null;
		}
		string text = MainWindow.ResolveBackgroundPath(backgroundPath);
		if (text == null || !File.Exists(text))
		{
			return null;
		}
		if (!VideoExtensions.Contains(Path.GetExtension(text).ToLowerInvariant()))
		{
			return null;
		}
		return text;
	}

	public static List<string> GetMp4Library()
	{
		string text = BuildScanKey();
		if (_mp4Cache != null && string.Equals(_mp4CacheKey, text, StringComparison.Ordinal))
		{
			return _mp4Cache;
		}
		List<string> list = new List<string>();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			string videoFolder = SettingsManager.Instance.VideoFolder;
			if (!string.IsNullOrWhiteSpace(videoFolder))
			{
				Directory.CreateDirectory(videoFolder);
				foreach (string item in Directory.EnumerateFiles(videoFolder, "*", SearchOption.AllDirectories))
				{
					if (!VideoExtensions.Contains(Path.GetExtension(item).ToLowerInvariant()))
					{
						continue;
					}
					try
					{
						if (hashSet.Add(Path.GetFullPath(item)))
						{
							list.Add(Path.GetFullPath(item));
						}
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}
		string text2 = ResolveBackgroundVideoPath();
		if (!string.IsNullOrWhiteSpace(text2) && hashSet.Add(text2))
		{
			list.Add(text2);
		}
		list.Sort(StringComparer.OrdinalIgnoreCase);
		_mp4Cache = list;
		_mp4CacheKey = text;
		return list;
	}

	private static void ApplySavedOrder()
	{
		// 2026-11-07 修复：旧实现用 list.AddRange(HashSet) 追加「未在保存顺序里」的文件，
		// HashSet 枚举顺序不稳定，且新增文件按文件系统枚举顺序插入，导致每次扫描后
		// 设置页列表与播放顺序看起来都在乱跳。改为：保存顺序优先 + 新文件按文件名稳定排序追加。
		List<string> scanned = new List<string>(KnownMusic);
		List<string> musicOrder = SettingsManager.Instance.MusicOrder;
		HashSet<string> remaining = new HashSet<string>(scanned, StringComparer.OrdinalIgnoreCase);
		List<string> ordered = new List<string>(scanned.Count);
		if (musicOrder != null)
		{
			foreach (string item in musicOrder)
			{
				if (remaining.Remove(item))
				{
					ordered.Add(item);
				}
			}
		}
		List<string> rest = scanned.Where(remaining.Contains).ToList();
		rest.Sort((string a, string b) => StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b)));
		ordered.AddRange(rest);
		KnownMusic.Clear();
		KnownMusic.AddRange(ordered);
	}

	public static void ReorderKnownMusic(IReadOnlyList<string> ordered)
	{
		// 拖拽结果优先；未出现在拖拽列表里的项按当前相对顺序 + 文件名排序追加，保证结果确定。
		List<string> original = new List<string>(KnownMusic);
		HashSet<string> remaining = new HashSet<string>(original, StringComparer.OrdinalIgnoreCase);
		List<string> list = new List<string>(original.Count);
		foreach (string item in ordered)
		{
			if (remaining.Remove(item))
			{
				list.Add(item);
			}
		}
		List<string> rest = original.Where(remaining.Contains).ToList();
		rest.Sort((string a, string b) => StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b)));
		list.AddRange(rest);
		KnownMusic.Clear();
		KnownMusic.AddRange(list);
		AppSettings instance = SettingsManager.Instance;
		instance.MusicOrder = new List<string>(KnownMusic);
		SettingsManager.Save(instance);
		// 背景视频轮播列表按 MusicOrder 排序，拖拽后必须重建，否则两处顺序不一致。
		MainWindow.Instance?.OnMusicOrderChanged();
		LogHelper.Log($"音乐顺序已调整: {KnownMusic.Count} 首");
	}

	public static void Play(string path)
	{
		string text = NormalizeAudioPath(path);
		if (text == null)
		{
			return;
		}
		NoMusicMode = false;
		CurrentPath = text;
		AppSettings instance = SettingsManager.Instance;
		instance.MusicPath = text;
		instance.MusicPaused = false;
		SettingsManager.Save(instance);
		EnsurePlayer();
		try
		{
			_lastSwitchTicks = Environment.TickCount64;
			_switchingTrack = true;
			if (IsVideoFile(text))
			{
				RestartVideoRequested = true;
			}
			if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
			{
				_player.Pause();
			}
			_source = MediaSource.CreateFromUri(new Uri(text));
			_pendingSource = _source;
			_pendingPath = text;
			_player.Source = _source;
			_player.Play();
			UpdateTransportInfo();
			StateChanged?.Invoke();
			LogHelper.Log("播放音乐: " + text);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "播放音乐失败: " + text);
		}
	}

	public static void Pause()
	{
		_player?.Pause();
		if (!SettingsManager.Instance.MusicPaused)
		{
			AppSettings instance = SettingsManager.Instance;
			instance.MusicPaused = true;
			SettingsManager.Save(instance);
		}
		UpdateTransportInfo();
		StateChanged?.Invoke();
	}

	private static void ClearPausedFlag()
	{
		if (SettingsManager.Instance.MusicPaused)
		{
			AppSettings instance = SettingsManager.Instance;
			instance.MusicPaused = false;
			SettingsManager.Save(instance);
		}
	}

	public static void Resume()
	{
		ClearPausedFlag();
		if (NoMusicMode)
		{
			NoMusicMode = false;
			string musicPath = SettingsManager.Instance.MusicPath;
			string text = null;
			if (!string.IsNullOrWhiteSpace(musicPath) && File.Exists(musicPath) && (IsVideoSource || !IsVideoFile(musicPath)) && !IsSkipped(musicPath))
			{
				text = musicPath;
			}
			if (text == null)
			{
				text = KnownMusic.FirstOrDefault((string f) => !IsSkipped(f) && (IsVideoSource || !IsVideoFile(f)));
			}
			if (text != null)
			{
				Play(text);
			}
			else
			{
				StateChanged?.Invoke();
			}
			return;
		}
		if (CurrentPath != null && IsVideoFile(CurrentPath))
		{
			RestartVideoRequested = true;
			Play(CurrentPath);
			return;
		}
		if (_player != null && _player.Source != null)
		{
			LogHelper.Log($"Resume前: state={_player.PlaybackSession.PlaybackState}, pos={_player.PlaybackSession.Position}");
			_player.Play();
			LogHelper.Log($"Resume后: state={_player.PlaybackSession.PlaybackState}");
		}
		else if (CurrentPath != null)
		{
			Play(CurrentPath);
		}
		UpdateTransportInfo();
		StateChanged?.Invoke();
	}

	public static void Stop()
	{
		if (!(_player == null))
		{
			_switchingTrack = false;
			_player.Pause();
			_player.Source = null;
			_source = null;
			CurrentPath = null;
			NoMusicMode = false;
			UpdateTransportInfo();
			StateChanged?.Invoke();
		}
	}

	public static void EnterNoMusicMode()
	{
		NoMusicMode = true;
		_switchingTrack = false;
		if (_player != null)
		{
			_player.Pause();
			_player.Source = null;
		}
		_source = null;
		_pendingSource = null;
		_pendingPath = null;
		CurrentPath = null;
		LogHelper.Log("不播放任何音乐（背景视频照常轮播）");
		UpdateTransportInfo();
		StateChanged?.Invoke();
	}

	private static string? NormalizeAudioPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}
		try
		{
			string fullPath = Path.GetFullPath(path);
			if (!File.Exists(fullPath) || !IsPlayableExtension(Path.GetExtension(fullPath)))
			{
				return null;
			}
			return fullPath;
		}
		catch
		{
			return null;
		}
	}

	private static bool IsPlayableExtension(string extension)
	{
		string value = extension.ToLowerInvariant();
		if (!AudioExtensions.Contains(value))
		{
			return VideoExtensions.Contains(value);
		}
		return true;
	}

	private static void EnsurePlayer()
	{
		if (!(_player != null))
		{
			MediaPlayer mediaPlayer = new MediaPlayer();
			mediaPlayer.CommandManager.IsEnabled = false;
			mediaPlayer.Volume = Math.Clamp(SettingsManager.Instance.MusicVolume, 0.0, 1.0);
			_player = mediaPlayer;
			_player.MediaEnded += OnMediaEnded;
			_player.MediaFailed += OnMediaFailed;
			_player.MediaOpened += OnMediaOpened;
			_player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
			SetupTransportControls(mediaPlayer);
		}
	}

	private static void SetupTransportControls(MediaPlayer player)
	{
		try
		{
			SystemMediaTransportControls systemMediaTransportControls = player.SystemMediaTransportControls;
			systemMediaTransportControls.IsEnabled = true;
			systemMediaTransportControls.IsPlayEnabled = true;
			systemMediaTransportControls.IsPauseEnabled = true;
			systemMediaTransportControls.IsNextEnabled = true;
			systemMediaTransportControls.IsPreviousEnabled = true;
			systemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Closed;
			systemMediaTransportControls.ButtonPressed += OnTransportButtonPressed;
			UpdateTransportInfo();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "SMTC 初始化失败");
		}
	}

	private static void OnTransportButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
	{
		_dispatcher?.TryEnqueue(delegate
		{
			try
			{
				switch (args.Button)
				{
				case SystemMediaTransportControlsButton.Play:
					Resume();
					break;
				case SystemMediaTransportControlsButton.Pause:
					Pause();
					break;
				case SystemMediaTransportControlsButton.Next:
					PlayNext();
					break;
				case SystemMediaTransportControlsButton.Previous:
					PlayPrevious();
					break;
				}
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "SMTC 按钮处理失败");
			}
		});
	}

	private static void UpdateTransportInfo()
	{
		MediaPlayer player = _player;
		if (player == null)
		{
			return;
		}
		try
		{
			SystemMediaTransportControls systemMediaTransportControls = player.SystemMediaTransportControls;
			string currentPath = CurrentPath;
			if (string.IsNullOrWhiteSpace(currentPath))
			{
				systemMediaTransportControls.IsEnabled = false;
				LogHelper.Log("[SMTC诊断] 音乐空路径 -> IsEnabled=false");
				return;
			}
			systemMediaTransportControls.IsEnabled = true;
			// 上行：文件名去后缀 + 去画质/帧率标记（4K/30fps/60帧之类）；
			// 下行：改回完整路径显示（曾固定成程序名「MhyUnBox」，用户要求改回来）。
			string rawName = Path.GetFileNameWithoutExtension(currentPath);
			string fileName = CleanQualityTags(rawName);
			if (string.IsNullOrWhiteSpace(fileName))
			{
				fileName = rawName;
			}
			systemMediaTransportControls.DisplayUpdater.Type = MediaPlaybackType.Music;
			systemMediaTransportControls.DisplayUpdater.MusicProperties.Title = fileName;
			systemMediaTransportControls.DisplayUpdater.MusicProperties.Artist = currentPath;
			systemMediaTransportControls.DisplayUpdater.Thumbnail = CreateAppThumbnail();
			systemMediaTransportControls.DisplayUpdater.Update();
			systemMediaTransportControls.PlaybackStatus = GetPlaybackStatus();
			LogHelper.Log($"[SMTC诊断] 音乐 path={currentPath} title={fileName} artist={currentPath} IsEnabled=true");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "SMTC 信息更新失败");
		}
	}

	// 画质/帧率标记：4K、2K、8K、24/30/60/120fps、60帧 之类，大小写不限。
	// 「4King」这种 K 后面跟着字母的不算；标记与名字之间的 _ - · 空白一起吃掉。
	private static readonly Regex QualityTagRegex = new(
		@"(?i)[\s_\-·]*(?:\d+\s*k|\d+\s*fps|\d+\s*帧)(?![a-z])[\s_\-·]*",
		RegexOptions.Compiled);

	// 剥完标记后可能残留空括号（如「名字（4K）」→「名字（）」），一并清掉。
	private static readonly Regex EmptyBracketRegex = new(
		@"（\s*）|\(\s*\)|\[\s*\]|【\s*】|\{\s*\}",
		RegexOptions.Compiled);

	/// <summary>
	/// 展示用的媒体名：去掉文件后缀之后，再把 4K / 30fps / 60帧 这类画质帧率
	/// 标记剥掉（「xxx 4K 60fps」→「xxx」）。全部被剥光时返回空串，
	/// 由调用方退回原始名兜底。
	/// </summary>
	public static string CleanQualityTags(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return "";
		}
		try
		{
			string cleaned = QualityTagRegex.Replace(text, " ").Trim();
			cleaned = EmptyBracketRegex.Replace(cleaned, "").Trim();
			cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
			return cleaned;
		}
		catch
		{
			return text;
		}
	}

	public static RandomAccessStreamReference? CreateAppThumbnail()
	{
		try
		{
			string text = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
			if (File.Exists(text))
			{
				return RandomAccessStreamReference.CreateFromUri(new Uri("file:///" + text.Replace('\\', '/')));
			}
		}
		catch
		{
		}
		return null;
	}

	private static MediaPlaybackStatus GetPlaybackStatus()
	{
		MediaPlayer player = _player;
		if (player == null || player.Source == null)
		{
			return MediaPlaybackStatus.Closed;
		}
		return ComputePlaybackStatus(player.PlaybackSession);
	}

	private static MediaPlaybackStatus ComputePlaybackStatus(MediaPlaybackSession session)
	{
		if (_switchingTrack && session.PlaybackState != MediaPlaybackState.None)
		{
			return MediaPlaybackStatus.Playing;
		}
		if (session.PlaybackState != MediaPlaybackState.Playing)
		{
			return MediaPlaybackStatus.Paused;
		}
		return MediaPlaybackStatus.Playing;
	}

	private static void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
	{
		LogHelper.Log($"音乐状态: {sender.PlaybackState}");
		if (sender.PlaybackState == MediaPlaybackState.Playing && _pendingSource == null)
		{
			_switchingTrack = false;
		}
		NotifyStateChanged();
		MediaPlaybackStatus status = ComputePlaybackStatus(sender);
		_dispatcher?.TryEnqueue(delegate
		{
			try
			{
				MediaPlayer player = _player;
				if (!(player == null) && player.Source != null)
				{
					player.SystemMediaTransportControls.PlaybackStatus = status;
				}
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "SMTC 播放状态同步失败");
			}
		});
	}

	private static void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
	{
		_switchingTrack = false;
		LogHelper.Log($"音乐加载失败: Error={args.Error}, 描述={args.ErrorMessage}");
	}

	private static void OnMediaOpened(MediaPlayer sender, object args)
	{
		MediaSource pendingSource = _pendingSource;
		if (pendingSource == null || !ReferenceEquals(sender.Source, pendingSource))
		{
			return;
		}
		string pendingPath = _pendingPath;
		_pendingPath = null;
		_pendingSource = null;
		if (pendingPath != null)
		{
			string text = sender.PlaybackSession.PlaybackState.ToString();
			LogHelper.Log("音乐(已打开): " + pendingPath + ", state=" + text);
			if (sender.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
			{
				sender.Play();
				LogHelper.Log("音乐(打开后补播): " + pendingPath);
			}
			if (sender.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
			{
				_switchingTrack = false;
			}
			try
			{
				sender.SystemMediaTransportControls.PlaybackStatus = GetPlaybackStatus();
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "SMTC 打开后状态同步失败");
			}
			UpdateTransportInfo();
		}
	}

	private static void OnMediaEnded(MediaPlayer sender, object args)
	{
		if (Environment.TickCount64 - _lastSwitchTicks < 1500)
		{
			LogHelper.Log("忽略切歌引起的 MediaEnded");
			return;
		}
		_dispatcher?.TryEnqueue(delegate
		{
			AdvanceToNext();
		});
	}

	private static void AdvanceToNext()
	{
		if (NoMusicMode)
		{
			LogHelper.Log("音乐已结束，但处于“不播放任何音乐”模式，不自动切歌");
			return;
		}
		RefreshKnownMusic();
		if (KnownMusic.Count == 0)
		{
			Stop();
			return;
		}
		List<string> list = KnownMusic.Where((string f) => !IsSkipped(f) && (IsVideoSource || !IsVideoFile(f))).ToList();
		if (list.Count == 0)
		{
			Stop();
			return;
		}
		int num = ((CurrentPath == null) ? (-1) : list.FindIndex((string f) => string.Equals(f, CurrentPath, StringComparison.OrdinalIgnoreCase)));
		string playbackMode = SettingsManager.Instance.PlaybackMode;
		int index;
		if (playbackMode == "loop")
		{
			index = ((num >= 0) ? num : 0);
		}
		else if (playbackMode == "sequential")
		{
			// 当前曲目被标记跳过 / 不在可播放列表时，按它在完整列表中的相对位置继续往后，
			// 而不是每次都回到第 0 首（旧行为表现为「列表循环总从头开始、顺序乱跳」）。
			index = ((num >= 0) ? ((num + 1) % list.Count) : NextPlayableAfterCurrent(list));
		}
		else if (list.Count == 1)
		{
			index = 0;
		}
		else if (num < 0)
		{
			index = Random.Next(list.Count);
		}
		else
		{
			int num2;
			do
			{
				num2 = Random.Next(list.Count);
			}
			while (num2 == num);
			index = num2;
		}
		Play(list[index]);
	}

	/// <summary>
	/// 当前曲目不在可播放列表（被标记跳过 / 视频源切换）时，
	/// 按完整 KnownMusic 顺序找到它之后的第一个可播放项；找不到才回到列表开头。
	/// </summary>
	private static int NextPlayableAfterCurrent(List<string> playable)
	{
		if (CurrentPath == null || playable.Count == 0)
		{
			return 0;
		}
		int raw = KnownMusic.FindIndex((string f) => string.Equals(f, CurrentPath, StringComparison.OrdinalIgnoreCase));
		if (raw < 0)
		{
			return 0;
		}
		for (int i = raw + 1; i < KnownMusic.Count; i++)
		{
			int found = playable.FindIndex((string f) => string.Equals(f, KnownMusic[i], StringComparison.OrdinalIgnoreCase));
			if (found >= 0)
			{
				return found;
			}
		}
		return 0;
	}

	public static void PlayNext()
	{
		_dispatcher?.TryEnqueue(delegate
		{
			if (NoMusicMode)
			{
				Resume();
			}
			else
			{
				RefreshKnownMusic();
				List<string> list = KnownMusic.Where((string f) => !IsSkipped(f) && (IsVideoSource || !IsVideoFile(f))).ToList();
				if (list.Count != 0)
				{
					int num = ((CurrentPath == null) ? (-1) : list.FindIndex((string f) => string.Equals(f, CurrentPath, StringComparison.OrdinalIgnoreCase)));
					int index = ((num >= 0) ? ((num + 1) % list.Count) : NextPlayableAfterCurrent(list));
					Play(list[index]);
				}
			}
		});
	}

	public static void PlayPrevious()
	{
		_dispatcher?.TryEnqueue(delegate
		{
			if (NoMusicMode)
			{
				Resume();
			}
			else
			{
				RefreshKnownMusic();
				List<string> list = KnownMusic.Where((string f) => !IsSkipped(f) && (IsVideoSource || !IsVideoFile(f))).ToList();
				if (list.Count != 0)
				{
					int num = ((CurrentPath == null) ? (-1) : list.FindIndex((string f) => string.Equals(f, CurrentPath, StringComparison.OrdinalIgnoreCase)));
					int index = ((num < 0) ? (list.Count - 1) : ((num - 1 + list.Count) % list.Count));
					Play(list[index]);
				}
			}
		});
	}

	public static bool IsSkipped(string path)
	{
		List<string> skippedMusic = SettingsManager.Instance.SkippedMusic;
		if (skippedMusic == null || skippedMusic.Count == 0)
		{
			return false;
		}
		return skippedMusic.Contains<string>(path, StringComparer.OrdinalIgnoreCase);
	}

	public static void ToggleSkip(string path)
	{
		if (path == "<no-music>")
		{
			return;
		}
		string text = NormalizeAudioPath(path);
		if (text != null)
		{
			AppSettings instance = SettingsManager.Instance;
			HashSet<string> hashSet = new HashSet<string>(instance.SkippedMusic ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
			bool flag;
			if (hashSet.Contains(text))
			{
				hashSet.Remove(text);
				flag = false;
			}
			else
			{
				hashSet.Add(text);
				flag = true;
			}
			instance.SkippedMusic = new List<string>(hashSet);
			SettingsManager.Save(instance);
			LogHelper.Log((flag ? "标记跳过" : "取消跳过") + ": " + text);
			if (flag && CurrentPath != null && string.Equals(CurrentPath, text, StringComparison.OrdinalIgnoreCase))
			{
				LogHelper.Log("当前歌曲被标记跳过，自动切换下一首");
				AdvanceToNext();
			}
			else
			{
				NotifyStateChanged();
			}
		}
	}

	public static bool CanSwitchToSource(string source)
	{
		if (!(source == "video"))
		{
			return true;
		}
		string backgroundPath = SettingsManager.Instance.BackgroundPath;
		if (string.IsNullOrWhiteSpace(backgroundPath))
		{
			return false;
		}
		if (File.Exists(backgroundPath))
		{
			return IsVideoFile(backgroundPath);
		}
		string fileName = Path.GetFileName(backgroundPath);
		string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Backgrounds", fileName);
		if (File.Exists(path))
		{
			return IsVideoFile(path);
		}
		path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
		if (File.Exists(path))
		{
			return IsVideoFile(path);
		}
		return false;
	}

	public static bool IsVideoFile(string path)
	{
		return VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
	}

	public static bool SetMusicSource(string source)
	{
		if (CanSwitchToSource(source))
		{
			AppSettings instance = SettingsManager.Instance;
			if (!(instance.MusicSource == source))
			{
				instance.MusicSource = source;
				SettingsManager.Save(instance);
				CurrentPath = null;
				ApplySettings();
				NotifyStateChanged();
			}
			return true;
		}
		return false;
	}

	public static void NotifyStateChanged()
	{
		StateChanged?.Invoke();
	}

	public static void SetVolume(double volume)
	{
		double num = Math.Clamp(volume, 0.0, 1.0);
		AppSettings instance = SettingsManager.Instance;
		if (!(Math.Abs(instance.MusicVolume - num) < 0.0001) || (!(_player == null) && !(Math.Abs(_player.Volume - num) < 0.0001)))
		{
			instance.MusicVolume = num;
			SettingsManager.Save(instance);
			if (_player != null)
			{
				_player.Volume = num;
			}
		}
	}
}
