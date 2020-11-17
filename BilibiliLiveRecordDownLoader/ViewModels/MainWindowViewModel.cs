using BilibiliApi.Clients;
using BilibiliApi.Model.LiveRecordList;
using BilibiliLiveRecordDownLoader.Interfaces;
using BilibiliLiveRecordDownLoader.Models;
using BilibiliLiveRecordDownLoader.Shared;
using BilibiliLiveRecordDownLoader.Utils;
using BilibiliLiveRecordDownLoader.ViewModels.TaskViewModels;
using DynamicData;
using Microsoft.Extensions.Logging;
using ModernWpf.Controls;
using Punchclock;
using ReactiveUI;
using Splat;
using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace BilibiliLiveRecordDownLoader.ViewModels
{
	public sealed class MainWindowViewModel : ReactiveObject, IDisposable
	{
		#region Monitor

		private readonly IDisposable _diskMonitor;
		private readonly IDisposable _roomIdMonitor;

		#endregion

		#region Command

		public ReactiveCommand<object?, Unit> CopyLiveRecordDownloadUrlCommand { get; }
		public ReactiveCommand<object?, Unit> OpenLiveRecordUrlCommand { get; }
		public ReactiveCommand<object?, Unit> DownLoadCommand { get; }
		public ReactiveCommand<object?, Unit> OpenDirCommand { get; }
		public ReactiveCommand<Unit, Unit> ShowWindowCommand { get; }
		public ReactiveCommand<Unit, Unit> ExitCommand { get; }
		public ReactiveCommand<object?, Unit> StopTaskCommand { get; }
		public ReactiveCommand<Unit, Unit> ClearAllTasksCommand { get; }

		#endregion

		private readonly ILogger _logger;
		private readonly IConfigService _configService;
		private readonly SourceList<LiveRecordList> _liveRecordSourceList;
		private readonly SourceList<TaskViewModel> _taskSourceList;
		private readonly OperationQueue _liveRecordDownloadTaskQueue;
		public readonly GlobalViewModel Global;

		public readonly ReadOnlyObservableCollection<LiveRecordListViewModel> LiveRecordList;
		public readonly ReadOnlyObservableCollection<TaskViewModel> TaskList;
		public Config Config => _configService.Config;
		private const long PageSize = 200;

		public MainWindowViewModel(
			ILogger<MainWindowViewModel> logger,
			IConfigService configService,
			SourceList<LiveRecordList> liveRecordSourceList,
			SourceList<TaskViewModel> taskSourceList,
			OperationQueue taskQueue,
			GlobalViewModel global)
		{
			_logger = logger;
			_configService = configService;
			_liveRecordSourceList = liveRecordSourceList;
			_taskSourceList = taskSourceList;
			_liveRecordDownloadTaskQueue = taskQueue;
			Global = global;

			_roomIdMonitor = this.WhenAnyValue(x => x._configService.Config.RoomId, x => x.Global.TriggerLiveRecordListQuery)
					.Throttle(TimeSpan.FromMilliseconds(800), RxApp.MainThreadScheduler)
					.DistinctUntilChanged()
					.Where(i => i.Item1 > 0)
					.Select(i => i.Item1)
					.Subscribe(i =>
					{
						GetAnchorInfoAsync(i).NoWarning();
						GetRecordListAsync(i).NoWarning();
					});

			_diskMonitor = Observable.Interval(TimeSpan.FromSeconds(1), RxApp.MainThreadScheduler)
					.Subscribe(GetDiskUsage);

			_liveRecordSourceList.Connect()
					.Transform(x => new LiveRecordListViewModel(x))
					.ObserveOnDispatcher()
					.Bind(out LiveRecordList)
					.DisposeMany()
					.Subscribe();

			_taskSourceList.Connect()
					.ObserveOnDispatcher()
					.Bind(out TaskList)
					.DisposeMany()
					.Subscribe();

			CopyLiveRecordDownloadUrlCommand = ReactiveCommand.CreateFromTask<object?>(CopyLiveRecordDownloadUrlAsync);
			OpenLiveRecordUrlCommand = ReactiveCommand.CreateFromObservable<object?, Unit>(OpenLiveRecordUrl);
			OpenDirCommand = ReactiveCommand.CreateFromObservable<object?, Unit>(OpenDir);
			DownLoadCommand = ReactiveCommand.CreateFromObservable<object?, Unit>(Download);
			ShowWindowCommand = ReactiveCommand.Create(ShowWindow);
			ExitCommand = ReactiveCommand.Create(Exit);
			StopTaskCommand = ReactiveCommand.CreateFromObservable<object?, Unit>(StopTask);
			ClearAllTasksCommand = ReactiveCommand.CreateFromTask(ClearAllTasksAsync);
		}

		private void GetDiskUsage(long _)
		{
			var (availableFreeSpace, totalSize) = Utils.Utils.GetDiskUsage(_configService.Config.MainDir);
			if (totalSize != 0)
			{
				Global.DiskUsageProgressBarText = $@"已使用 {Utils.Utils.CountSize(totalSize - availableFreeSpace)}/{Utils.Utils.CountSize(totalSize)} 剩余 {Utils.Utils.CountSize(availableFreeSpace)}";
				var percentage = (totalSize - availableFreeSpace) / (double)totalSize;
				Global.DiskUsageProgressBarValue = percentage * 100;
			}
			else
			{
				Global.DiskUsageProgressBarText = string.Empty;
				Global.DiskUsageProgressBarValue = 0;
			}
		}

		private async Task GetAnchorInfoAsync(long roomId)
		{
			try
			{
				using var client = new BililiveApiClient();
				var msg = await client.GetAnchorInfoAsync(roomId);

				if (msg?.data?.info == null || msg.code != 0)
				{
					throw new ArgumentException($@"[{roomId}]获取主播信息出错，可能该房间号的主播不存在");
				}

				var info = msg.data.info;
				Global.ImageUri = info.face;
				Global.Name = info.uname;
				Global.Uid = info.uid;
				Global.Level = info.platform_user_level;
			}
			catch (Exception ex)
			{
				Global.ImageUri = null;
				Global.Name = string.Empty;
				Global.Uid = 0;
				Global.Level = 0;

				if (ex is ArgumentException)
				{
					_logger.LogWarning(ex.Message);
				}
				else
				{
					_logger.LogError(ex, @"[{0}]获取主播信息出错", roomId);
				}
			}
		}

		private async Task GetRecordListAsync(long roomId)
		{
			try
			{
				Global.IsLiveRecordBusy = true;
				Global.RoomId = 0;
				Global.ShortRoomId = 0;
				Global.RecordCount = 0;
				_liveRecordSourceList.Clear();

				using var client = new BililiveApiClient();
				var roomInitMessage = await client.GetRoomInitAsync(roomId);
				if (roomInitMessage != null
					&& roomInitMessage.code == 0
					&& roomInitMessage.data != null
					&& roomInitMessage.data.room_id > 0)
				{
					Global.RoomId = roomInitMessage.data.room_id;
					Global.ShortRoomId = roomInitMessage.data.short_id;
					Global.RecordCount = long.MaxValue;
					var currentPage = 0;
					while (currentPage < Math.Ceiling((double)Global.RecordCount / PageSize))
					{
						var listMessage = await client.GetLiveRecordListAsync(roomInitMessage.data.room_id, ++currentPage, PageSize);
						if (listMessage?.data != null && listMessage.data.count > 0)
						{
							Global.RecordCount = listMessage.data.count;
							var list = listMessage.data?.list;
							if (list != null)
							{
								_liveRecordSourceList.AddRange(list);
							}
						}
						else
						{
							_logger.LogWarning(@"[{0}]加载列表出错，可能该直播间无直播回放", roomId);
							Global.RecordCount = 0;
							break;
						}
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, @"[{0}]加载直播回放列表出错", roomId);
				Global.RecordCount = 0;
			}
			finally
			{
				Global.IsLiveRecordBusy = false;
			}
		}

		private static async Task CopyLiveRecordDownloadUrlAsync(object? info)
		{
			try
			{
				if (info is LiveRecordListViewModel liveRecord && !string.IsNullOrEmpty(liveRecord.Rid))
				{
					using var client = new BililiveApiClient();
					var message = await client.GetLiveRecordUrlAsync(liveRecord.Rid);
					var list = message?.data?.list;
					if (list is not null
						&& list.Length > 0
						&& list.All(x => x.url is not null or @"" || x.backup_url is not null or @"")
						)
					{
						Utils.Utils.CopyToClipboard(string.Join(Environment.NewLine,
								list.Select(x => x.url is not null or @"" ? x.backup_url : x.url)
						));
					}
				}
			}
			catch
			{
				//ignored
			}
		}

		private static IObservable<Unit> OpenLiveRecordUrl(object? info)
		{
			return Observable.Start(() =>
			{
				try
				{
					if (info is LiveRecordListViewModel { Rid: not @"" or null } liveRecord)
					{
						Utils.Utils.OpenUrl($@"https://live.bilibili.com/record/{liveRecord.Rid}");
					}
				}
				catch
				{
					//ignored
				}
			});
		}

		private IObservable<Unit> OpenDir(object? info)
		{
			return Observable.Start(() =>
			{
				try
				{
					if (info is LiveRecordListViewModel liveRecord && !string.IsNullOrEmpty(liveRecord.Rid))
					{
						var root = Path.Combine(_configService.Config.MainDir, $@"{Global.RoomId}", Constants.LiveRecordPath);
						var path = Path.Combine(root, liveRecord.Rid);
						if (!Utils.Utils.OpenDir(path))
						{
							Directory.CreateDirectory(root);
							Utils.Utils.OpenDir(root);
						}
					}
				}
				catch
				{
					//ignored
				}
			});
		}

		private IObservable<Unit> Download(object? info)
		{
			return Observable.Start(() =>
			{
				try
				{
					if (info is IList { Count: > 0 } list)
					{
						foreach (var item in list)
						{
							if (item is LiveRecordListViewModel { Rid: not @"" or null } liveRecord)
							{
								var root = Path.Combine(_configService.Config.MainDir, $@"{Global.RoomId}", Constants.LiveRecordPath);
								var task = new LiveRecordDownloadTaskViewModel(_logger, liveRecord, root, _configService.Config.DownloadThreads);
								if (AddTask(task))
								{
									_liveRecordDownloadTaskQueue.Enqueue(1, Constants.LiveRecordKey, () => task.StartAsync().AsTask()).NoWarning();
								}
							}
						}
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, @"下载回放出错");
				}
			});
		}

		private bool AddTask(TaskViewModel task)
		{
			if (_taskSourceList.Items.Any(x => x.Description == task.Description))
			{
				_logger.LogWarning($@"添加重复任务：{task.Description}");
				return false;
			}
			_taskSourceList.Add(task);
			return true;
		}

		private IObservable<Unit> StopTask(object? info)
		{
			return Observable.Start(() =>
			{
				try
				{
					if (info is IList { Count: > 0 } list)
					{
						foreach (var item in list)
						{
							if (item is TaskViewModel task)
							{
								task.Stop();
								_taskSourceList.Remove(task);
							}
						}
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, @"停止任务出错");
				}
			});
		}

		private async Task ClearAllTasksAsync()
		{
			try
			{
				if (_taskSourceList.Count == 0)
				{
					return;
				}
				using var dialog = new DisposableContentDialog
				{
					Title = @"确定清空所有任务？",
					Content = @"将会停止所有任务并清空列表",
					PrimaryButtonText = @"确定",
					CloseButtonText = @"取消",
					DefaultButton = ContentDialogButton.Primary
				};
				if (await dialog.ShowAsync() == ContentDialogResult.Primary)
				{
					_taskSourceList.Items.ToList().ForEach(task =>
					{
						task.Stop();
						_taskSourceList.Remove(task);
					});
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, @"停止任务出错");
			}
		}

		private void StopAllTask()
		{
			_taskSourceList.Items.ToList().ForEach(t => t.Stop());
		}

		private static void ShowWindow()
		{
			Locator.Current.GetService<MainWindow>().ShowWindow();
		}

		private void Exit()
		{
			StopAllTask();
			var window = Locator.Current.GetService<MainWindow>();
			window.CloseReason = CloseReason.ApplicationExitCall;
			window.Close();
		}

		public void Dispose()
		{
			_diskMonitor.Dispose();
			_roomIdMonitor.Dispose();
			_configService.Dispose();
		}
	}
}
