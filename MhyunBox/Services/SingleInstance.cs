using System;
using System.Threading;

namespace MhyunBox.Services;

public static class SingleInstance
{
	private const string MutexName = "Local\\MhyunBox_SingleInstance";

	private const string ShowEventName = "Local\\MhyunBox_SingleInstance_Show";

	private static Mutex? _mutex;

	private static EventWaitHandle? _showEvent;

	private static Thread? _listener;

	public static bool TryClaim()
	{
		try
		{
			_mutex = new Mutex(initiallyOwned: true, "Local\\MhyunBox_SingleInstance", out var createdNew);
			if (!createdNew)
			{
				EventWaitHandle eventWaitHandle = null;
				for (int i = 0; i < 30; i++)
				{
					if (eventWaitHandle != null)
					{
						break;
					}
					try
					{
						eventWaitHandle = EventWaitHandle.OpenExisting("Local\\MhyunBox_SingleInstance_Show");
					}
					catch (WaitHandleCannotBeOpenedException)
					{
						Thread.Sleep(50);
					}
				}
				using (eventWaitHandle)
				{
					eventWaitHandle?.Set();
					LogHelper.Log("检测到已有实例，通知其置顶后退出");
				}
				return false;
			}
			_showEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, "Local\\MhyunBox_SingleInstance_Show");
			_listener = new Thread(Listen)
			{
				IsBackground = true,
				Name = "MhyunBoxSingleInstance"
			};
			_listener.Start();
			return true;
		}
		catch (Exception ex2)
		{
			LogHelper.LogError(ex2, "单实例检测失败，按唯一实例继续启动");
			return true;
		}
	}

	private static void Listen()
	{
		while (_showEvent != null && _showEvent.WaitOne())
		{
			MainWindow.UIQueue?.TryEnqueue(delegate
			{
				MainWindow.Instance?.WakeToFront();
			});
		}
	}

	public static void Release()
	{
		try
		{
			_showEvent?.Set();
			_listener = null;
			EventWaitHandle showEvent = _showEvent;
			_showEvent = null;
			showEvent?.Dispose();
			_mutex?.ReleaseMutex();
			_mutex?.Dispose();
			_mutex = null;
		}
		catch
		{
		}
	}
}
