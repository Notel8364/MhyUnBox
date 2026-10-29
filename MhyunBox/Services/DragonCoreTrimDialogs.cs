using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MhyunBox.Pages;
using MhyunBox.Settings;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace MhyunBox.Services;

// DragonCore 增量裁剪/合并的完整弹窗流程（ToolsPage 与 ClientUpdatePage 共用）
public static class DragonCoreTrimDialogs
{
	public static bool Busy { get; private set; }

	public static async Task<string?> PickFolderAsync()
	{
		FolderPicker folderPicker = new FolderPicker
		{
			FileTypeFilter = { "*" }
		};
		WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance));
		StorageFolder storageFolder = await folderPicker.PickSingleFolderAsync();
		return storageFolder?.Path;
	}

	public static async Task<string?> PickZipAsync()
	{
		FileOpenPicker fileOpenPicker = new FileOpenPicker
		{
			ViewMode = PickerViewMode.List
		};
		fileOpenPicker.FileTypeFilter.Add(".zip");
		WinRT.Interop.InitializeWithWindow.Initialize(fileOpenPicker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance));
		StorageFile storageFile = await fileOpenPicker.PickSingleFileAsync();
		return storageFile?.Path;
	}

	// 裁剪流程：选基准包（文件夹/压缩包，版本目录自动拼 DragonCore 路径）→ 确认/选当前包 → 进度弹窗 → 结果弹窗 → 可自动合并进元素客户端 → 可选删除新包
	public static async Task ShowTrimFlowAsync(XamlRoot xamlRoot)
	{
		string? baseline = await PickPackSourceAsync(xamlRoot, "基准包（旧的大体积完整包）");
		if (baseline == null)
		{
			return;
		}
		string? baselineDir = DragonCoreTrimService.ResolvePackDir(baseline);
		if (baselineDir == null)
		{
			await DialogGate.ShowAsync(new ContentDialog
			{
				Title = "无法识别资源包",
				Content = "在所选位置没有找到 DragonCore 资源包（需要含 gui/models/sound 子目录或 .data 文件，或版本客户端目录）。\n所选：\n" + baseline,
				CloseButtonText = "知道了",
				XamlRoot = xamlRoot
			});
			return;
		}
		string? current = DragonCoreTrimService.DefaultCandidateDirs().FirstOrDefault();
		string? currentRaw = null;
		if (current != null)
		{
			ContentDialogResult contentDialogResult = await DialogGate.ShowAsync(new ContentDialog
			{
				Title = "当前包目录",
				Content = "检测到本地 DragonCore 资源包目录：\n" + current + "\n\n使用该目录作为「当前包（最新）」？",
				PrimaryButtonText = "使用该目录",
				SecondaryButtonText = "手动选择…",
				CloseButtonText = "取消",
				DefaultButton = ContentDialogButton.Primary,
				XamlRoot = xamlRoot
			});
			if (contentDialogResult == ContentDialogResult.None)
			{
				return;
			}
			if (contentDialogResult == ContentDialogResult.Secondary)
			{
				current = await PickPackSourceAsync(xamlRoot, "当前包（元素之诗服务器最新大包，文件夹或压缩包）");
				currentRaw = current;
				if (current == null)
				{
					return;
				}
			}
		}
		else
		{
			current = await PickPackSourceAsync(xamlRoot, "当前包（元素之诗服务器最新大包，文件夹或压缩包）");
			currentRaw = current;
			if (current == null)
			{
				return;
			}
		}
		string? currentDir = DragonCoreTrimService.ResolvePackDir(current);
		if (currentDir == null)
		{
			await DialogGate.ShowAsync(new ContentDialog
			{
				Title = "无法识别资源包",
				Content = "在所选位置没有找到 DragonCore 资源包（需要含 gui/models/sound 子目录或 .data 文件，或版本客户端目录）。\n所选：\n" + current,
				CloseButtonText = "知道了",
				XamlRoot = xamlRoot
			});
			return;
		}
		await RunTrimAsync(xamlRoot, baselineDir, currentDir, currentRaw);
	}

	// 选资源包来源：文件夹或压缩包（选压缩包=增量源/新包 zip，选文件夹=DragonCore 目录/版本客户端目录自动拼路径）
	private static async Task<string?> PickPackSourceAsync(XamlRoot xamlRoot, string title)
	{
		ContentDialogResult result = await DialogGate.ShowAsync(new ContentDialog
		{
			Title = title,
			Content = "选择来源类型：\n・文件夹：DragonCore 资源包目录、版本客户端目录（自动定位其中的 DragonCore）\n・压缩包：官方新包 zip（自动解压后对比）",
			PrimaryButtonText = "选文件夹",
			SecondaryButtonText = "选压缩包",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = xamlRoot
		});
		if (result == ContentDialogResult.Primary)
		{
			return await PickFolderAsync();
		}
		if (result == ContentDialogResult.Secondary)
		{
			return await PickZipAsync();
		}
		return null;
	}

	// 手动选择合并目标（DragonCore 目录/版本客户端目录自动拼路径），失败返回 null
	private static async Task<string?> PickMergeTargetManuallyAsync(XamlRoot xamlRoot)
	{
		string? picked = await PickFolderAsync();
		if (picked == null)
		{
			return null;
		}
		string? resolved = DragonCoreTrimService.ResolvePackDir(picked);
		if (resolved == null)
		{
			await DialogGate.ShowAsync(new ContentDialog
			{
				Title = "无法识别资源包",
				Content = "在所选位置没有找到 DragonCore 资源包。\n所选：\n" + picked,
				CloseButtonText = "知道了",
				XamlRoot = xamlRoot
			});
			return null;
		}
		return resolved;
	}

	private static void SaveMergeTarget(string targetDir)
	{
		try
		{
			AppSettings instance = SettingsManager.Instance;
			if (instance.DragonCoreTargetDir != targetDir)
			{
				instance.DragonCoreTargetDir = targetDir;
				SettingsManager.Save(instance);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "保存元素客户端 DragonCore 路径失败");
		}
	}

	private static async Task RunTrimAsync(XamlRoot xamlRoot, string baseline, string current, string? currentRaw)
	{
		DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();
		CancellationTokenSource cts = new CancellationTokenSource();
		TextBlock statusText = new TextBlock
		{
			Text = "准备中…",
			TextWrapping = TextWrapping.Wrap,
			MaxWidth = 420,
			VerticalAlignment = VerticalAlignment.Center
		};
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 12
		};
		stackPanel.Children.Add(new ProgressRing
		{
			IsActive = true,
			Width = 28,
			Height = 28
		});
		stackPanel.Children.Add(statusText);
		ContentDialog progressDialog = new ContentDialog
		{
			Title = "正在裁剪增量包",
			Content = stackPanel,
			CloseButtonText = "取消",
			XamlRoot = xamlRoot
		};
		progressDialog.Closed += delegate
		{
			try
			{
				cts.Cancel();
			}
			catch
			{
			}
		};
		Busy = true;
		Task<ContentDialogResult> dialogTask = DialogGate.ShowAsync(progressDialog);
		try
		{
			DragonCoreTrimService.TrimResult trimResult = await DragonCoreTrimService.TrimAsync(baseline, current, null, delegate(DragonCoreTrimService.TrimProgress p)
			{
				queue.TryEnqueue(delegate
				{
					statusText.Text = ((p.Total > 0) ? $"{p.Phase} {p.Done}/{p.Total}" : p.Phase) + ((p.CurrentFile.Length > 60) ? "\n" + p.CurrentFile.Substring(p.CurrentFile.Length - 60) : ((p.CurrentFile.Length > 0) ? ("\n" + p.CurrentFile) : ""));
				});
			}, cts.Token);
			progressDialog.Hide();
			await dialogTask;
			if (!trimResult.Success)
			{
				await DialogGate.ShowAsync(new ContentDialog
				{
					Title = "裁剪未完成",
					Content = trimResult.Error,
					CloseButtonText = "知道了",
					XamlRoot = xamlRoot
				});
				return;
			}
			try
			{
				// 裁剪完成自动弹出发布文件夹并选中产物
				Process.Start("explorer.exe", "/select,\"" + trimResult.OutputZip + "\"");
			}
			catch (Exception ex4)
			{
				LogHelper.LogError(ex4, "自动打开增量包目录失败");
			}
			StackPanel stackPanel2 = new StackPanel
			{
				Spacing = 10
			};
			stackPanel2.Children.Add(new TextBlock
			{
				Text = $"基准包 {trimResult.BaselineFiles} 个文件\n当前包 {trimResult.CurrentFiles} 个文件\n新增 {trimResult.Added} 个，变更 {trimResult.Changed} 个\n增量原始体积 {trimResult.AddedBytes / 1048576.0:0.0} MB\n耗时 {trimResult.Elapsed.TotalSeconds:0.0} 秒",
				TextWrapping = TextWrapping.Wrap
			});
			if (trimResult.Samples.Count > 0)
			{
				stackPanel2.Children.Add(new TextBlock
				{
					Text = "示例：\n" + string.Join("\n", trimResult.Samples.Take(8)),
					FontSize = 11,
					Opacity = 0.7,
					FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
					TextWrapping = TextWrapping.Wrap
				});
			}
			TextBlock textBlock = new TextBlock
			{
				Text = trimResult.OutputZip,
				FontSize = 12,
				FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
				TextWrapping = TextWrapping.Wrap,
				IsTextSelectionEnabled = true
			};
			stackPanel2.Children.Add(textBlock);
			StackPanel resultButtons = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				Spacing = 8
			};
			Button button = new Button
			{
				Content = "移到桌面",
				HorizontalAlignment = HorizontalAlignment.Left
			};
			button.Click += delegate
			{
				try
				{
					string dest = MoveZipToDesktop(trimResult.OutputZip);
					textBlock.Text = dest;
					button.Content = "已移到桌面";
					button.IsEnabled = false;
				}
				catch (Exception ex3)
				{
					LogHelper.LogError(ex3, "移动增量包到桌面失败");
				}
			};
			resultButtons.Children.Add(button);
			Button button2 = new Button
			{
				Content = "打开所在文件夹",
				HorizontalAlignment = HorizontalAlignment.Left
			};
			button2.Click += delegate
			{
				try
				{
					Process.Start("explorer.exe", "/select,\"" + textBlock.Text + "\"");
				}
				catch (Exception ex2)
				{
					LogHelper.LogError(ex2, "打开增量包目录失败");
				}
			};
			resultButtons.Children.Add(button2);
			stackPanel2.Children.Add(resultButtons);
			await DialogGate.ShowAsync(new ContentDialog
			{
				Title = "增量包已生成",
				Content = stackPanel2,
				CloseButtonText = "完成",
				XamlRoot = xamlRoot
			});
			// —— 裁剪后自动合并进元素客户端 ——
			string? target = GetKnownMergeTarget();
			ContentDialogResult mergeChoice = await DialogGate.ShowAsync(new ContentDialog
			{
				Title = "自动合并",
				Content = (target != null)
					? ("检测到元素客户端 DragonCore 资源包目录：\n" + target + "\n\n把刚生成的增量包合并进去？")
					: "是否把刚生成的增量包合并进元素客户端的 DragonCore 资源包？（未检测到本地目录，需要手动选择）",
				PrimaryButtonText = (target != null) ? "合并到该目录" : "选择目录合并…",
				SecondaryButtonText = (target != null) ? "选择其他目录…" : "",
				CloseButtonText = "跳过",
				DefaultButton = ContentDialogButton.Primary,
				XamlRoot = xamlRoot
			});
			if (mergeChoice != ContentDialogResult.None)
			{
				if (mergeChoice == ContentDialogResult.Secondary || target == null)
				{
					target = await PickMergeTargetManuallyAsync(xamlRoot);
				}
				if (target != null)
				{
					await RunMergeCoreAsync(xamlRoot, trimResult.OutputZip, target);
					SaveMergeTarget(target);
				}
			}
			// —— 可选删除新包（大文件夹/压缩包）释放空间 ——
			if (currentRaw != null)
			{
				await OfferDeleteNewPackAsync(xamlRoot, currentRaw, current, target);
			}
		}
		finally
		{
			DragonCoreTrimService.CleanupTempDir(baseline);
			DragonCoreTrimService.CleanupTempDir(current);
			Busy = false;
		}
	}

	// 已知的合并目标：程序记住的路径 > 自动候选
	private static string? GetKnownMergeTarget()
	{
		string? remembered = SettingsManager.Instance.DragonCoreTargetDir;
		if (!string.IsNullOrEmpty(remembered) && DragonCoreTrimService.LooksLikeDragonCoreDir(remembered))
		{
			return remembered;
		}
		return DragonCoreTrimService.DefaultCandidateDirs().FirstOrDefault();
	}

	// 问用户是否删除刚用完的新包（压缩包或大文件夹），自动候选目录绝不删除
	private static async Task OfferDeleteNewPackAsync(XamlRoot xamlRoot, string currentRaw, string currentDir, string? target)
	{
		try
		{
			LogHelper.Force($"[DragonCore裁剪] 删除询问：raw={currentRaw} dir={currentDir} target={target}");
			// 缓冲：上一个弹窗可能仍在关闭动画中，ContentDialog 同一 XamlRoot 只允许一个，稍等避免竞态静默失败
			await Task.Delay(300);
			bool isZip = System.IO.File.Exists(currentRaw) && string.Equals(System.IO.Path.GetExtension(currentRaw), ".zip", StringComparison.OrdinalIgnoreCase);
			if (!isZip && !System.IO.Directory.Exists(currentRaw))
			{
				LogHelper.Force("[DragonCore裁剪] 删除询问跳过：来源不存在");
				return;
			}
			if (currentRaw.Contains("MhyDragonTrim_"))
			{
				LogHelper.Force("[DragonCore裁剪] 删除询问跳过：临时目录");
				return;
			}
			if (DragonCoreTrimService.DefaultCandidateDirs().Any((string d) => string.Equals(d.TrimEnd('\\'), currentDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
			{
				LogHelper.Force("[DragonCore裁剪] 删除询问跳过：当前包是自动候选目录");
				return;
			}
			if (target != null && string.Equals(target.TrimEnd('\\'), currentDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
			{
				LogHelper.Force("[DragonCore裁剪] 删除询问跳过：新包与合并目标相同");
				return;
			}
			ContentDialogResult choice = await DialogGate.ShowAsync(new ContentDialog
			{
				Title = "释放空间",
				Content = "是否删除新包" + (isZip ? "压缩包" : "大文件夹") + "？\n" + currentRaw + "\n\n删除后不可恢复（增量包已生成，合并不受影响；正式服务器资源请自行确认不再需要）。",
				PrimaryButtonText = "删除",
				CloseButtonText = "保留",
				DefaultButton = ContentDialogButton.Close,
				XamlRoot = xamlRoot
			});
			if (choice != ContentDialogResult.Primary)
			{
				return;
			}
			try
			{
				if (isZip)
				{
					System.IO.File.Delete(currentRaw);
				}
				else
				{
					System.IO.Directory.Delete(currentRaw, recursive: true);
				}
			}
			catch (Exception ex5)
			{
				LogHelper.LogError(ex5, "删除新包失败");
				await DialogGate.ShowAsync(new ContentDialog
				{
					Title = "删除失败",
					Content = ex5.Message + "\n\n" + currentRaw,
					CloseButtonText = "知道了",
					XamlRoot = xamlRoot
				});
			}
		}
		catch (Exception ex6)
		{
			LogHelper.LogError(ex6, "删除新包流程异常");
		}
	}

	// 合并流程：选增量包 → 目标=程序记住的路径/自动候选（都没有才手动选）→ 合并 → 结果弹窗（O+P 提示）→ 记住路径
	public static async Task ShowMergeFlowAsync(XamlRoot xamlRoot)
	{
		string? text = await PickZipAsync();
		if (text == null)
		{
			return;
		}
		string? text2 = GetKnownMergeTarget();
		if (text2 == null)
		{
			text2 = await PickMergeTargetManuallyAsync(xamlRoot);
			if (text2 == null)
			{
				return;
			}
		}
		await RunMergeCoreAsync(xamlRoot, text, text2);
		SaveMergeTarget(text2);
	}

	// 合并核心：进度弹窗 → MergeAsync → 结果弹窗（成功含 O+P 重载提示）
	private static async Task RunMergeCoreAsync(XamlRoot xamlRoot, string zipPath, string targetDir)
	{
		CancellationTokenSource cts = new CancellationTokenSource();
		TextBlock statusText = new TextBlock
		{
			Text = "正在解压合并…",
			TextWrapping = TextWrapping.Wrap,
			VerticalAlignment = VerticalAlignment.Center
		};
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 12
		};
		stackPanel.Children.Add(new ProgressRing
		{
			IsActive = true,
			Width = 28,
			Height = 28
		});
		stackPanel.Children.Add(statusText);
		ContentDialog progressDialog = new ContentDialog
		{
			Title = "正在合并增量包",
			Content = stackPanel,
			CloseButtonText = "取消",
			XamlRoot = xamlRoot
		};
		progressDialog.Closed += delegate
		{
			try
			{
				cts.Cancel();
			}
			catch
			{
			}
		};
		bool wasBusy = Busy;
		Busy = true;
		Task<ContentDialogResult> dialogTask = DialogGate.ShowAsync(progressDialog);
		try
		{
			DragonCoreTrimService.MergeResult mergeResult = await DragonCoreTrimService.MergeAsync(zipPath, targetDir, cts.Token);
			progressDialog.Hide();
			await dialogTask;
			await DialogGate.ShowAsync(new ContentDialog
			{
				Title = (mergeResult.Success ? "合并完成" : "合并失败"),
				Content = mergeResult.Success ? $"已解压覆盖 {mergeResult.Extracted} 个文件到：\n{mergeResult.TargetDir}\n\n进元素之诗服务器后按 O+P 重新加载模型即可生效（无需重启游戏）。" : mergeResult.Error,
				CloseButtonText = "知道了",
				XamlRoot = xamlRoot
			});
		}
		finally
		{
			Busy = wasBusy;
		}
	}

	// 把增量包 zip 移到桌面（同名覆盖），返回新路径
	public static string MoveZipToDesktop(string zipPath)
	{
		string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		string dest = System.IO.Path.Combine(desktop, System.IO.Path.GetFileName(zipPath));
		if (string.Equals(System.IO.Path.GetFullPath(zipPath), System.IO.Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
		{
			return dest;
		}
		if (System.IO.File.Exists(dest))
		{
			System.IO.File.Delete(dest);
		}
		System.IO.File.Move(zipPath, dest);
		return dest;
	}
}
