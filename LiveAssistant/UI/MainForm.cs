using System.Diagnostics;
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
    private readonly Label _lblAdminAccount = new();
    private readonly Label _lblCurrentTask = new();
    private readonly Label _lblLastError = new();

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
    private readonly Button _btnKugouLogin = new();
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
        Text = AppBranding.DisplayName;
        Width = 1280;
        Height = 820;
        MinimumSize = new Size(1100, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(245, 247, 250);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(245, 247, 250)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        Controls.Add(root);

        root.Controls.Add(BuildHeaderPanel(), 0, 0);
        root.Controls.Add(BuildMainPanel(), 0, 1);
        root.Controls.Add(BuildConnectPanel(), 0, 2);
        root.Controls.Add(BuildBottomPanel(), 0, 3);
    }

    private Control BuildHeaderPanel()
    {
        var wrap = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0)
        };
        wrap.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        wrap.RowStyles.Add(new RowStyle(SizeType.Percent, 60));

        wrap.Controls.Add(BuildLiveStatusBox(), 0, 0);
        wrap.Controls.Add(BuildRuntimeStatusBox(), 0, 1);
        return wrap;
    }

    private Control BuildLiveStatusBox()
    {
        var box = MakeGroup("直播状态");
        var grid = MakeGrid(4, 1, 6);
        grid.Controls.Add(MakeStatusCell("主播", _lblAccount), 0, 0);
        grid.Controls.Add(MakeStatusCell("直播间", _lblRoom), 1, 0);
        grid.Controls.Add(MakeStatusCell("连接状态", _lblConnection), 2, 0);
        grid.Controls.Add(MakeStatusCell("播放模式", _lblMode), 3, 0);
        box.Controls.Add(grid);
        return box;
    }

    private Control BuildRuntimeStatusBox()
    {
        var box = MakeGroup("运行状态");
        var grid = MakeGrid(4, 2, 4);
        grid.RowStyles.Clear();
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        grid.Controls.Add(MakeStatusCell("抖音", _lblDouyinSidecar), 0, 0);
        grid.Controls.Add(MakeStatusCell("酷狗", _lblKugouSidecar), 1, 0);
        grid.Controls.Add(MakeStatusCell("抖音登录", _lblAdminAccount), 2, 0);
        grid.Controls.Add(MakeStatusCell("运行时间", _lblUptime), 3, 0);
        grid.Controls.Add(MakeStatusCell("当前歌曲", _lblRuntimeSong), 0, 1);
        grid.Controls.Add(MakeStatusCell("队列数量", _lblRuntimeQueue), 1, 1);
        grid.Controls.Add(MakeStatusCell("当前任务", _lblCurrentTask), 2, 1);
        grid.Controls.Add(MakeStatusCell("最后错误", _lblLastError), 3, 1);
        box.Controls.Add(grid);
        return box;
    }

    private static GroupBox MakeGroup(string title)
    {
        return new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 8),
            Margin = new Padding(0, 0, 0, 6),
            BackColor = Color.White
        };
    }

    private static TableLayoutPanel MakeGrid(int cols, int rows, int pad)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = cols,
            RowCount = rows,
            Padding = new Padding(pad, 2, pad, 2),
            Margin = new Padding(0)
        };
        for (var c = 0; c < cols; c++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
        }

        for (var r = 0; r < rows; r++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
        }

        return grid;
    }

    private static Control MakeStatusCell(string title, Label valueLabel)
    {
        valueLabel.Dock = DockStyle.Fill;
        valueLabel.AutoEllipsis = true;
        valueLabel.Text = "-";
        valueLabel.TextAlign = ContentAlignment.MiddleLeft;
        valueLabel.Margin = new Padding(0);

        var wrap = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(4, 2, 4, 2),
            Padding = new Padding(0)
        };
        wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
        wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        wrap.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.BottomLeft,
            Margin = new Padding(0)
        }, 0, 0);
        wrap.Controls.Add(valueLabel, 0, 1);
        return wrap;
    }

    private Control BuildMainPanel()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            BackColor = Color.FromArgb(245, 247, 250)
        };
        BindSplitRatio(split, vertical: true, ratio: 0.72, min1: 420, min2: 240);

        var leftSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6
        };
        BindSplitRatio(leftSplit, vertical: false, ratio: 0.38, min1: 100, min2: 180);

        var leftBottom = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6
        };
        BindSplitRatio(leftBottom, vertical: false, ratio: 0.45, min1: 80, min2: 120);

        leftSplit.Panel1.Controls.Add(BuildListGroup("实时弹幕", _lstDanmaku));
        leftBottom.Panel1.Controls.Add(BuildListGroup("系统消息", _lstSystem));
        leftBottom.Panel2.Controls.Add(BuildQueueGroup());
        leftSplit.Panel2.Controls.Add(leftBottom);

        split.Panel1.Controls.Add(leftSplit);
        split.Panel2.Controls.Add(BuildSidePanel());
        return split;
    }

    /// <summary>
    /// 延后设置 MinSize / SplitterDistance，避免构造阶段宽高为 0 时抛异常导致“打不开”。
    /// </summary>
    private static void BindSplitRatio(SplitContainer split, bool vertical, double ratio, int min1, int min2)
    {
        void Apply()
        {
            try
            {
                var total = vertical ? split.Width : split.Height;
                if (total <= split.SplitterWidth + 20)
                {
                    return;
                }

                var maxMin = Math.Max(25, (total - split.SplitterWidth) / 3);
                var safeMin1 = Math.Min(min1, maxMin);
                var safeMin2 = Math.Min(min2, maxMin);
                split.Panel1MinSize = Math.Max(25, safeMin1);
                split.Panel2MinSize = Math.Max(25, safeMin2);

                var desired = (int)(total * ratio);
                var min = split.Panel1MinSize;
                var max = total - split.SplitterWidth - split.Panel2MinSize;
                if (max < min)
                {
                    return;
                }

                split.SplitterDistance = Math.Clamp(desired, min, max);
            }
            catch
            {
                // ignore layout race
            }
        }

        split.HandleCreated += (_, _) => Apply();
        split.SizeChanged += (_, _) => Apply();
    }

    private static Control BuildListGroup(string title, ListBox list)
    {
        var group = MakeGroup(title);
        list.Dock = DockStyle.Fill;
        list.HorizontalScrollbar = true;
        list.IntegralHeight = false;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.Margin = new Padding(0);
        group.Controls.Add(list);
        return group;
    }

    private Control BuildQueueGroup()
    {
        var group = MakeGroup("点歌队列");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        _lvQueue.Dock = DockStyle.Fill;
        _lvQueue.View = View.Details;
        _lvQueue.FullRowSelect = true;
        _lvQueue.GridLines = true;
        _lvQueue.BorderStyle = BorderStyle.FixedSingle;
        _lvQueue.Margin = new Padding(0);
        _colQueuePos.Text = "#";
        _colQueuePos.Width = 40;
        _colQueueUser.Text = "用户";
        _colQueueUser.Width = 120;
        _colQueueSong.Text = "歌曲";
        _colQueueSong.Width = 280;
        if (_lvQueue.Columns.Count == 0)
        {
            _lvQueue.Columns.AddRange(new[] { _colQueuePos, _colQueueUser, _colQueueSong });
        }

        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0, 6, 0, 0),
            Padding = new Padding(0)
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        StyleButton(_btnSkipQueue, "跳过", 80);
        StyleButton(_btnRemoveQueue, "删除", 80);
        StyleButton(_btnClearQueue, "清空", 80);
        buttons.Controls.Add(_btnSkipQueue, 0, 0);
        buttons.Controls.Add(_btnRemoveQueue, 1, 0);
        buttons.Controls.Add(_btnClearQueue, 2, 0);

        layout.Controls.Add(_lvQueue, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildSidePanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

        var info = MakeGroup("当前播放");
        var infoGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = new Padding(0),
            Padding = new Padding(4)
        };
        for (var i = 0; i < 4; i++)
        {
            infoGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        }

        foreach (var lbl in new[] { _lblNowSong, _lblNowArtist, _lblNowRequester, _lblProgress })
        {
            lbl.Text = lbl == _lblNowSong ? "歌曲: -"
                : lbl == _lblNowArtist ? "歌手: -"
                : lbl == _lblNowRequester ? "点歌: -"
                : "进度: -";
            lbl.Dock = DockStyle.Fill;
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            lbl.AutoEllipsis = true;
            lbl.Margin = new Padding(0);
        }

        infoGrid.Controls.Add(_lblNowSong, 0, 0);
        infoGrid.Controls.Add(_lblNowArtist, 0, 1);
        infoGrid.Controls.Add(_lblNowRequester, 0, 2);
        infoGrid.Controls.Add(_lblProgress, 0, 3);
        info.Controls.Add(infoGrid);

        var controls = MakeGroup("播放控制");
        var ctrlGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0),
            Padding = new Padding(4)
        };
        ctrlGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        ctrlGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        ctrlGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var btnRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        StyleButton(_btnPause, "暂停", 0);
        StyleButton(_btnNext, "下一首", 0);
        _btnPause.Dock = DockStyle.Fill;
        _btnNext.Dock = DockStyle.Fill;
        _btnPause.Margin = new Padding(0, 0, 4, 0);
        _btnNext.Margin = new Padding(4, 0, 0, 0);
        btnRow.Controls.Add(_btnPause, 0, 0);
        btnRow.Controls.Add(_btnNext, 1, 0);

        var volLabel = new Label
        {
            Text = "音量",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            ForeColor = Color.Gray,
            Margin = new Padding(0)
        };
        _volumeBar.Dock = DockStyle.Fill;
        _volumeBar.Minimum = 0;
        _volumeBar.Maximum = 100;
        _volumeBar.Value = Math.Clamp(_host.Config.Settings.Playback.Volume, 0, 100);
        _volumeBar.TickFrequency = 10;
        _volumeBar.TickStyle = TickStyle.None;
        _volumeBar.Margin = new Padding(0, 0, 0, 4);

        ctrlGrid.Controls.Add(btnRow, 0, 0);
        ctrlGrid.Controls.Add(volLabel, 0, 1);
        ctrlGrid.Controls.Add(_volumeBar, 0, 2);
        controls.Controls.Add(ctrlGrid);

        layout.Controls.Add(info, 0, 0);
        layout.Controls.Add(controls, 0, 1);
        return layout;
    }

    private Control BuildConnectPanel()
    {
        var box = MakeGroup("连接与模式");
        box.Margin = new Padding(0, 6, 0, 0);

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(4, 2, 4, 2)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var lblRid = new Label
        {
            Text = "直播间短号",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        _txtWebRid.Dock = DockStyle.Fill;
        _txtWebRid.Margin = new Padding(0, 6, 8, 6);
        _txtWebRid.Text = _host.Config.Settings.Douyin.WebRid;

        StyleButton(_btnConnect, "连接", 0);
        _btnConnect.Dock = DockStyle.Fill;
        _btnConnect.Margin = new Padding(0, 4, 8, 4);

        StyleButton(_btnKugouLogin, "酷狗登录", 0);
        _btnKugouLogin.Dock = DockStyle.Fill;
        _btnKugouLogin.Margin = new Padding(0, 4, 12, 4);

        var lblMode = new Label
        {
            Text = "模式",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        _cmbMode.Dock = DockStyle.Fill;
        _cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbMode.Margin = new Padding(0, 6, 0, 6);
        if (_cmbMode.Items.Count == 0)
        {
            _cmbMode.Items.AddRange(new object[] { "点歌+随机补位", "点歌优先", "随机播放" });
        }
        _cmbMode.SelectedIndex = ModeToIndex(_host.Config.Settings.Playback.Mode);

        row.Controls.Add(lblRid, 0, 0);
        row.Controls.Add(_txtWebRid, 1, 0);
        row.Controls.Add(_btnConnect, 2, 0);
        row.Controls.Add(_btnKugouLogin, 3, 0);
        row.Controls.Add(lblMode, 4, 0);
        row.Controls.Add(_cmbMode, 5, 0);
        box.Controls.Add(row);
        return box;
    }

    private static void StyleButton(Button btn, string text, int width)
    {
        btn.Text = text;
        btn.Height = 32;
        if (width > 0)
        {
            btn.Width = width;
            btn.Anchor = AnchorStyles.Left;
        }

        btn.FlatStyle = FlatStyle.System;
        btn.Margin = new Padding(0, 2, 8, 2);
        btn.UseVisualStyleBackColor = true;
    }

    private Control BuildBottomPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));

        var admin = _host.Config.Settings.Admin;
        var adminUrl = $"http://127.0.0.1:{admin.Port}{admin.Path}";

        var lbl = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.Gray,
            Margin = new Padding(2, 0, 0, 0),
            Text = $"Sidecar: 抖音 http://127.0.0.1:4723 | 酷狗 http://127.0.0.1:17888 | 管理后台 {adminUrl}"
        };

        var btnAdmin = new Button
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 2, 0, 2)
        };
        StyleButton(btnAdmin, "管理后台", 0);
        btnAdmin.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(adminUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开管理后台: {ex.Message}", AppBranding.DisplayName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        panel.Controls.Add(lbl, 0, 0);
        panel.Controls.Add(btnAdmin, 1, 0);
        return panel;
    }

    private void WireEvents()
    {
        _host.DanmakuReceived += OnDanmaku;
        _host.SystemMessages.MessageAdded += line => BeginInvoke(() => AppendSystem(line));
        foreach (var msg in _host.SystemMessages.Messages)
        {
            AppendSystem(msg);
        }

        _host.Queue.QueueChanged += () => BeginInvoke(RefreshQueue);
        _host.PlaybackCommands.Playback.StateChanged += () => BeginInvoke(RefreshPlayback);
        _host.StateChanged += () => BeginInvoke(RefreshStatusPanels);

        _btnConnect.Click += async (_, _) => await ConnectAsync();
        _btnKugouLogin.Click += (_, _) => OpenKugouLoginPage();
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

        FormClosing += (_, _) =>
        {
            // 窗口立即关闭；资源释放放到 Application.Run 返回后，避免 UI 线程死锁卡死
            try { _uiTimer.Stop(); } catch { /* ignore */ }
        };
        FormClosed += (_, _) =>
        {
            try
            {
                _host.DanmakuReceived -= OnDanmaku;
            }
            catch
            {
                // ignore
            }
        };
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

    private void OpenKugouLoginPage()
    {
        var url = _host.Kugou.LoginPageUrl;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
            AppendSystem("已打开酷狗扫码登录页，登录后将自动领取每日试用会员");
        }
        catch (Exception ex)
        {
            AppendSystem($"打开酷狗登录页失败: {ex.Message}（请手动访问 {url}）");
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
        if (_lstSystem.Items.Contains(line))
        {
            return;
        }

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
        RefreshStatusPanels();
    }

    private void RefreshStatusPanels()
    {
        RefreshStatus();
        RefreshQueue();
        RefreshPlayback();
    }

    private void RefreshStatus()
    {
        _lblAccount.Text = string.IsNullOrWhiteSpace(_host.Danmaku.RoomOwnerNickname)
            || _host.Danmaku.RoomOwnerNickname == "-"
            ? "-"
            : _host.Danmaku.RoomOwnerNickname;
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
        if (status.KugouOnline)
        {
            _lblKugouSidecar.Text = status.KugouLoginStatus;
            _lblKugouSidecar.ForeColor = status.KugouLoginStatus.Contains("未登录", StringComparison.Ordinal)
                ? Color.DarkOrange
                : Color.DarkGreen;
        }
        else
        {
            _lblKugouSidecar.Text = "离线";
            _lblKugouSidecar.ForeColor = Color.DarkRed;
        }
        _lblAdminAccount.Text = status.DouyinLoginStatus.Contains("已登录", StringComparison.Ordinal)
            ? status.DouyinLoginNickname
            : status.DouyinLoginStatus;
        _lblRuntimeSong.Text = status.CurrentSong;
        _lblRuntimeQueue.Text = status.QueueCount.ToString();
        _lblUptime.Text = FormatUptime(status.Uptime);
        _lblCurrentTask.Text = status.CurrentTask;
        _lblLastError.Text = string.IsNullOrWhiteSpace(status.LastError) ? "无" : status.LastError;
        _lblLastError.ForeColor = string.IsNullOrWhiteSpace(status.LastError) ? Color.Black : Color.DarkRed;
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
            _btnPause.Text = "暂停";
            RefreshStatus();
            return;
        }

        _lblNowSong.Text = $"歌曲: {track.SongName}";
        _lblNowArtist.Text = $"歌手: {track.Artist}";
        _lblNowRequester.Text = track.IsRandom ? "点歌: 随机补位" : $"点歌: {track.Requester}";
        var duration = Math.Max(1, _host.PlaybackCommands.Playback.DurationSec);
        var progress = _host.PlaybackCommands.Playback.ProgressSec;
        _lblProgress.Text = $"进度: {FormatTime(progress)} / {FormatTime(duration)}";
        _btnPause.Text = _host.PlaybackCommands.Playback.State == PlaybackState.Paused ? "恢复" : "暂停";
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
