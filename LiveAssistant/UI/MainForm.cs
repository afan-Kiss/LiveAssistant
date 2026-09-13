using LiveAssistant.Models;
using LiveAssistant.Services;

namespace LiveAssistant.UI;

public sealed class MainForm : Form
{
    private readonly LiveAppHost _host;

    private readonly Label _lblAccount = new();
    private readonly Label _lblRoom = new();
    private readonly Label _lblConnection = new();
    private readonly Label _lblMode = new();
    private readonly Label _lblDouyinSidecar = new();
    private readonly Label _lblKugouSidecar = new();
    private readonly Label _lblRuntimeSong = new();
    private readonly Label _lblRuntimeQueue = new();
    private readonly Label _lblUptime = new();

    private readonly ListBox _lstDanmaku = new();
    private readonly ListBox _lstSystem = new();
    private readonly ListView _lvQueue = new();
    private readonly ColumnHeader _colQueuePos = new();
    private readonly ColumnHeader _colQueueUser = new();
    private readonly ColumnHeader _colQueueSong = new();

    private readonly Label _lblNowSong = new();
    private readonly Label _lblNowArtist = new();
    private readonly Label _lblNowRequester = new();
    private readonly Label _lblProgress = new();
    private readonly TrackBar _volumeBar = new();
    private readonly Button _btnPause = new();
    private readonly Button _btnNext = new();
    private readonly Button _btnConnect = new();
    private readonly TextBox _txtWebRid = new();
    private readonly ComboBox _cmbMode = new();
    private readonly Button _btnSkipQueue = new();
    private readonly Button _btnRemoveQueue = new();
    private readonly Button _btnClearQueue = new();

    private readonly System.Windows.Forms.Timer _uiTimer = new();
    private readonly HashSet<string> _seenDanmaku = new();

    public MainForm(LiveAppHost host)
    {
        _host = host;
        InitializeUi();
        WireEvents();
        RefreshAll();
    }

    private void InitializeUi()
    {
        Text = "LiveAssistant - 直播互动助手";
        Width = 1180;
        Height = 760;
        MinimumSize = new Size(980, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(8)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(root);

        root.Controls.Add(BuildStatusPanel(), 0, 0);
        root.Controls.Add(BuildRuntimeStatusPanel(), 0, 1);
        root.Controls.Add(BuildMainPanel(), 0, 2);
        root.Controls.Add(BuildPlaybackPanel(), 0, 3);
        root.Controls.Add(BuildBottomPanel(), 0, 4);
    }

    private Control BuildRuntimeStatusPanel()
    {
        var panel = new GroupBox
        {
            Text = "运行状态",
            Dock = DockStyle.Fill
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            Padding = new Padding(8, 4, 8, 4)
        };
        for (var i = 0; i < 5; i++)
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        }

        layout.Controls.Add(MakeStatusCell("抖音", _lblDouyinSidecar), 0, 0);
        layout.Controls.Add(MakeStatusCell("酷狗", _lblKugouSidecar), 1, 0);
        layout.Controls.Add(MakeStatusCell("当前歌曲", _lblRuntimeSong), 2, 0);
        layout.Controls.Add(MakeStatusCell("队列数量", _lblRuntimeQueue), 3, 0);
        layout.Controls.Add(MakeStatusCell("运行时间", _lblUptime), 4, 0);
        panel.Controls.Add(layout);
        return panel;
    }

    private Control BuildStatusPanel()
    {
        var panel = new GroupBox
        {
            Text = "直播状态",
            Dock = DockStyle.Fill
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            Padding = new Padding(8, 4, 8, 4)
        };
        for (var i = 0; i < 4; i++)
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }

        layout.Controls.Add(MakeStatusCell("账号", _lblAccount), 0, 0);
        layout.Controls.Add(MakeStatusCell("直播间", _lblRoom), 1, 0);
        layout.Controls.Add(MakeStatusCell("连接状态", _lblConnection), 2, 0);
        layout.Controls.Add(MakeStatusCell("播放模式", _lblMode), 3, 0);
        panel.Controls.Add(layout);
        return panel;
    }

    private static Control MakeStatusCell(string title, Label valueLabel)
    {
        valueLabel.Dock = DockStyle.Fill;
        valueLabel.AutoEllipsis = true;
        valueLabel.Text = "-";
        var wrap = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        wrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        wrap.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Color.Gray }, 0, 0);
        wrap.Controls.Add(valueLabel, 0, 1);
        return wrap;
    }

    private Control BuildMainPanel()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 700
        };

        var leftSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 220
        };
        var leftBottom = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 180
        };

        leftSplit.Panel1.Controls.Add(BuildListGroup("实时弹幕", _lstDanmaku));
        leftBottom.Panel1.Controls.Add(BuildListGroup("系统消息", _lstSystem));
        leftBottom.Panel2.Controls.Add(BuildQueueGroup());
        leftSplit.Panel2.Controls.Add(leftBottom);

        split.Panel1.Controls.Add(leftSplit);
        split.Panel2.Controls.Add(BuildSidePanel());
        return split;
    }

    private static Control BuildListGroup(string title, ListBox list)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(8) };
        list.Dock = DockStyle.Fill;
        list.HorizontalScrollbar = true;
        list.IntegralHeight = false;
        group.Controls.Add(list);
        return group;
    }

    private Control BuildQueueGroup()
    {
        var group = new GroupBox { Text = "点歌队列", Dock = DockStyle.Fill, Padding = new Padding(8) };
        _lvQueue.Dock = DockStyle.Fill;
        _lvQueue.View = View.Details;
        _lvQueue.FullRowSelect = true;
        _lvQueue.GridLines = true;
        _colQueuePos.Text = "#";
        _colQueuePos.Width = 40;
        _colQueueUser.Text = "用户";
        _colQueueUser.Width = 120;
        _colQueueSong.Text = "歌曲";
        _colQueueSong.Width = 260;
        _lvQueue.Columns.AddRange(new[] { _colQueuePos, _colQueueUser, _colQueueSong });

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            FlowDirection = FlowDirection.LeftToRight
        };
        _btnSkipQueue.Text = "跳过";
        _btnRemoveQueue.Text = "删除";
        _btnClearQueue.Text = "清空";
        _btnSkipQueue.AutoSize = true;
        _btnRemoveQueue.AutoSize = true;
        _btnClearQueue.AutoSize = true;
        buttons.Controls.AddRange(new Control[] { _btnSkipQueue, _btnRemoveQueue, _btnClearQueue });

        group.Controls.Add(_lvQueue);
        group.Controls.Add(buttons);
        return group;
    }

    private Control BuildSidePanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var info = new GroupBox { Text = "当前播放", Dock = DockStyle.Top, Height = 160, Padding = new Padding(10) };
        _lblNowSong.Text = "歌曲: -";
        _lblNowArtist.Text = "歌手: -";
        _lblNowRequester.Text = "点歌: -";
        _lblProgress.Text = "进度: -";
        foreach (var lbl in new[] { _lblNowSong, _lblNowArtist, _lblNowRequester, _lblProgress })
        {
            lbl.Dock = DockStyle.Top;
            lbl.Height = 24;
        }
        info.Controls.AddRange(new Control[] { _lblProgress, _lblNowRequester, _lblNowArtist, _lblNowSong });

        var controls = new GroupBox { Text = "播放控制", Dock = DockStyle.Top, Height = 120, Padding = new Padding(10) };
        _btnPause.Text = "暂停";
        _btnNext.Text = "下一首";
        _btnPause.Width = 90;
        _btnNext.Width = 90;
        _volumeBar.Minimum = 0;
        _volumeBar.Maximum = 100;
        _volumeBar.Value = _host.Config.Settings.Playback.Volume;
        _volumeBar.TickFrequency = 10;
        _volumeBar.Width = 180;
        var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
        row.Controls.AddRange(new Control[] { _btnPause, _btnNext, new Label { Text = "音量", AutoSize = true, Padding = new Padding(8, 8, 0, 0) }, _volumeBar });
        controls.Controls.Add(row);

        panel.Controls.Add(controls);
        panel.Controls.Add(info);
        return panel;
    }

    private Control BuildPlaybackPanel()
    {
        var panel = new GroupBox { Text = "连接与模式", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        row.Controls.Add(new Label { Text = "直播间短号:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        _txtWebRid.Width = 180;
        _txtWebRid.Text = _host.Config.Settings.Douyin.WebRid;
        row.Controls.Add(_txtWebRid);
        _btnConnect.Text = "连接";
        _btnConnect.Width = 90;
        row.Controls.Add(_btnConnect);
        row.Controls.Add(new Label { Text = "  模式:", AutoSize = true, Padding = new Padding(12, 8, 0, 0) });
        _cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbMode.Width = 180;
        _cmbMode.Items.AddRange(new object[]
        {
            "点歌+随机补位",
            "点歌优先",
            "随机播放"
        });
        _cmbMode.SelectedIndex = ModeToIndex(_host.Config.Settings.Playback.Mode);
        row.Controls.Add(_cmbMode);
        panel.Controls.Add(row);
        return panel;
    }

    private Control BuildBottomPanel()
    {
        var lbl = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.Gray,
            Text = "Sidecar: 抖音 http://127.0.0.1:4723 | 酷狗 http://127.0.0.1:17888 | 日志: logs/*.log"
        };
        return lbl;
    }

    private void WireEvents()
    {
        _host.DanmakuReceived += OnDanmaku;
        _host.SystemMessages.MessageAdded += line => BeginInvoke(() => AppendSystem(line));
        _host.Queue.QueueChanged += () => BeginInvoke(RefreshQueue);
        _host.PlaybackCommands.Playback.StateChanged += () => BeginInvoke(RefreshPlayback);
        _host.StateChanged += () => BeginInvoke(RefreshAll);

        _btnConnect.Click += async (_, _) => await ConnectAsync();
        _btnPause.Click += (_, _) => TogglePause();
        _btnNext.Click += async (_, _) => await _host.Engine.SkipAsync();
        _btnSkipQueue.Click += async (_, _) => await _host.Engine.SkipAsync();
        _btnRemoveQueue.Click += (_, _) => RemoveSelectedQueueItem();
        _btnClearQueue.Click += (_, _) =>
        {
            _host.Queue.ClearWaiting();
            _host.NotifyStateChanged();
        };
        _volumeBar.ValueChanged += (_, _) =>
        {
            _host.PlaybackCommands.EnqueueSetVolume(_volumeBar.Value);
            _host.Config.Settings.Playback.Volume = _volumeBar.Value;
            _host.Config.Save();
        };
        _cmbMode.SelectedIndexChanged += (_, _) =>
        {
            if (_cmbMode.SelectedIndex < 0)
            {
                return;
            }
            _host.Engine.SetMode(IndexToMode(_cmbMode.SelectedIndex));
            RefreshStatus();
        };

        _uiTimer.Interval = 1000;
        _uiTimer.Tick += (_, _) => RefreshPlayback();
        _uiTimer.Start();

        Load += async (_, _) =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_host.Config.Settings.Douyin.WebRid))
                {
                    await ConnectAsync();
                }
            }
            catch (Exception ex)
            {
                AppendSystem($"启动连接失败: {ex.Message}");
            }
        };

        FormClosing += (_, _) => _host.Dispose();
    }

    private async Task ConnectAsync()
    {
        _btnConnect.Enabled = false;
        try
        {
            await _host.ConnectAsync(_txtWebRid.Text.Trim());
            RefreshAll();
        }
        catch (Exception ex)
        {
            AppendSystem($"连接失败: {ex.Message}");
        }
        finally
        {
            _btnConnect.Enabled = true;
        }
    }

    private void TogglePause()
    {
        if (_host.PlaybackCommands.Playback.State == PlaybackState.Paused)
        {
            _host.Engine.Resume();
            _btnPause.Text = "暂停";
        }
        else if (_host.PlaybackCommands.Playback.State is PlaybackState.Playing or PlaybackState.RandomFill)
        {
            _host.Engine.Pause();
            _btnPause.Text = "恢复";
        }
    }

    private void RemoveSelectedQueueItem()
    {
        if (_lvQueue.SelectedItems.Count == 0)
        {
            return;
        }

        if (_lvQueue.SelectedItems[0].Tag is long id)
        {
            _host.Queue.Remove(id);
            _host.NotifyStateChanged();
        }
    }

    private void OnDanmaku(DanmakuItem item)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnDanmaku(item));
            return;
        }

        if (!_seenDanmaku.Add(item.MsgId))
        {
            return;
        }

        _lstDanmaku.Items.Add(item.DisplayLine);
        TrimList(_lstDanmaku, _host.Config.Settings.Ui.MaxDanmakuLines);
        _lstDanmaku.TopIndex = _lstDanmaku.Items.Count - 1;
    }

    private void AppendSystem(string line)
    {
        _lstSystem.Items.Add(line);
        TrimList(_lstSystem, _host.Config.Settings.Ui.MaxSystemMessageLines);
        _lstSystem.TopIndex = _lstSystem.Items.Count - 1;
    }

    private static void TrimList(ListBox list, int max)
    {
        while (list.Items.Count > max)
        {
            list.Items.RemoveAt(0);
        }
    }

    private void RefreshAll()
    {
        RefreshStatus();
        RefreshQueue();
        RefreshPlayback();
        foreach (var msg in _host.SystemMessages.Messages)
        {
            if (!_lstSystem.Items.Contains(msg))
            {
                _lstSystem.Items.Add(msg);
            }
        }
    }

    private void RefreshStatus()
    {
        _lblAccount.Text = _host.Danmaku.AccountNickname;
        _lblRoom.Text = string.IsNullOrWhiteSpace(_host.Config.Settings.Douyin.WebRid)
            ? "-"
            : $"{_host.Config.Settings.Douyin.WebRid} {_host.Danmaku.RoomTitle}";
        _lblConnection.Text = _host.Danmaku.ConnectionStatus;
        _lblMode.Text = _host.Engine.Mode switch
        {
            PlaybackMode.RequestOnly => "点歌优先",
            PlaybackMode.RandomOnly => "随机播放",
            _ => _host.PlaybackCommands.Playback.IsRandomFillActive ? "点歌+随机补位(补位中)" : "点歌+随机补位"
        };
        RefreshRuntimeStatus();
    }

    private void RefreshRuntimeStatus()
    {
        var status = _host.GetRuntimeStatus();
        _lblDouyinSidecar.Text = $"{status.DouyinStatus} ({status.DanmakuConnection})";
        _lblDouyinSidecar.ForeColor = status.DouyinOnline ? Color.DarkGreen : Color.DarkRed;
        _lblKugouSidecar.Text = status.KugouStatus;
        _lblKugouSidecar.ForeColor = status.KugouOnline ? Color.DarkGreen : Color.DarkRed;
        _lblRuntimeSong.Text = status.CurrentSong;
        _lblRuntimeQueue.Text = status.QueueCount.ToString();
        _lblUptime.Text = FormatUptime(status.Uptime);
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalHours >= 1)
        {
            return $"{(int)uptime.TotalHours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";
        }
        return $"{uptime.Minutes:D2}:{uptime.Seconds:D2}";
    }

    private void RefreshQueue()
    {
        _lvQueue.BeginUpdate();
        _lvQueue.Items.Clear();

        var now = _host.Queue.NowPlaying;
        if (now != null)
        {
            var playing = new ListViewItem("▶")
            {
                Tag = now.Id
            };
            playing.SubItems.Add(now.IsRandom ? "随机" : now.Nickname);
            playing.SubItems.Add($"{now.SongName} - {now.Artist}");
            playing.Font = new Font(_lvQueue.Font, FontStyle.Bold);
            _lvQueue.Items.Add(playing);
        }

        var waiting = _host.Queue.Waiting;
        for (var i = 0; i < waiting.Count; i++)
        {
            var item = waiting[i];
            var row = new ListViewItem((i + 1).ToString()) { Tag = item.Id };
            row.SubItems.Add(item.IsRandom ? "随机" : item.Nickname);
            row.SubItems.Add($"{item.SongName} - {item.Artist}");
            _lvQueue.Items.Add(row);
        }

        _lvQueue.EndUpdate();
        RefreshStatus();
    }

    private void RefreshPlayback()
    {
        var track = _host.PlaybackCommands.Playback.CurrentTrack;
        if (track == null)
        {
            _lblNowSong.Text = "歌曲: -";
            _lblNowArtist.Text = "歌手: -";
            _lblNowRequester.Text = "点歌: -";
            _lblProgress.Text = "进度: -";
            return;
        }

        _lblNowSong.Text = $"歌曲: {track.SongName}";
        _lblNowArtist.Text = $"歌手: {track.Artist}";
        _lblNowRequester.Text = track.IsRandom ? "点歌: 随机补位" : $"点歌: {track.Requester}";
        var duration = Math.Max(1, _host.PlaybackCommands.Playback.DurationSec);
        var progress = _host.PlaybackCommands.Playback.ProgressSec;
        _lblProgress.Text = $"进度: {FormatTime(progress)} / {FormatTime(duration)}";
        RefreshStatus();
    }

    private static string FormatTime(int seconds)
    {
        return TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss");
    }

    private static int ModeToIndex(PlaybackMode mode) => mode switch
    {
        PlaybackMode.RequestOnly => 1,
        PlaybackMode.RandomOnly => 2,
        _ => 0
    };

    private static PlaybackMode IndexToMode(int index) => index switch
    {
        1 => PlaybackMode.RequestOnly,
        2 => PlaybackMode.RandomOnly,
        _ => PlaybackMode.RequestWithRandomFill
    };
}
