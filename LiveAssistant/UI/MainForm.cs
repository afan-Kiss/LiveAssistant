using System.Diagnostics;
using LiveAssistant.Models;
using LiveAssistant.Services;

namespace LiveAssistant.UI;

public sealed class MainForm : Form
{
    private readonly LiveAppHost _host;

    private readonly Label _lblAlertBar = new();
    private readonly Label _lblAccount = new();
    private readonly Label _lblRoom = new();
    private readonly Label _lblConnection = new();
    private readonly Label _lblMode = new();
    private readonly Label _lblDouyinConn = new();
    private readonly Label _lblKugouConn = new();
    private readonly Label _lblHealthSong = new();
    private readonly Label _lblHealthArtist = new();
    private readonly Label _lblHealthProgress = new();
    private readonly Label _lblQueueWaiting = new();
    private readonly Label _lblStartedAt = new();
    private readonly Label _lblUptime = new();
    private readonly Label _lblTodaySongs = new();
    private readonly Label _lblTodayDanmaku = new();
    private readonly Label _lblTodayGifts = new();
    private readonly Label _lblTodayRequests = new();
    private readonly Label _lblRecentErrors = new();

    private readonly ListBox _lstDanmaku = new();
    private readonly ListView _lvQueue = new();
    private readonly ColumnHeader _colQueuePos = new();
    private readonly ColumnHeader _colQueueUser = new();
    private readonly ColumnHeader _colQueueSong = new();

    private readonly Label _lblNowSong = new();
    private readonly Label _lblNowArtist = new();
    private readonly Label _lblNowState = new();
    private readonly Label _lblNowSource = new();
    private readonly Label _lblNowRemaining = new();
    private readonly TrackBar _volumeBar = new();
    private readonly Button _btnPause = new();
    private readonly Button _btnResume = new();
    private readonly Button _btnNext = new();
    private readonly Button _btnKugouRelogin = new();
    private readonly Button _btnToggleRequest = new();
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
        MinimumSize = new Size(1100, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.FromArgb(245, 247, 250);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(245, 247, 250)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // GroupBox 标题 + 内边距 + 34px 按钮行，64px 会在高 DPI 下裁切顶部控件
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        Controls.Add(root);

        root.Controls.Add(BuildAlertBar(), 0, 0);
        root.Controls.Add(BuildMainPanel(), 0, 1);
        root.Controls.Add(BuildConnectPanel(), 0, 2);
        root.Controls.Add(BuildBottomPanel(), 0, 3);
    }

    private Control BuildAlertBar()
    {
        _lblAlertBar.Dock = DockStyle.Fill;
        _lblAlertBar.TextAlign = ContentAlignment.MiddleLeft;
        _lblAlertBar.Padding = new Padding(12, 0, 12, 0);
        _lblAlertBar.AutoEllipsis = true;
        _lblAlertBar.BackColor = Color.FromArgb(255, 248, 230);
        _lblAlertBar.ForeColor = Color.FromArgb(120, 80, 0);
        _lblAlertBar.Text = "状态加载中…";
        _lblAlertBar.Margin = new Padding(0, 0, 0, 4);

        var wrap = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(255, 248, 230),
            Padding = new Padding(0)
        };
        wrap.Controls.Add(_lblAlertBar);
        return wrap;
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
        BindSplitRatio(split, vertical: true, ratio: 0.60, min1: 400, min2: 380);

        var leftSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6
        };
        BindSplitRatio(leftSplit, vertical: false, ratio: 0.50, min1: 120, min2: 160);

        leftSplit.Panel1.Controls.Add(BuildListGroup("实时弹幕", _lstDanmaku));
        leftSplit.Panel2.Controls.Add(BuildQueueGroup());
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

        group.Controls.Add(_lvQueue);
        return group;
    }

    private Control BuildSidePanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 46));

        var info = MakeGroup("当前播放");
        var infoGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = new Padding(0),
            Padding = new Padding(4)
        };
        for (var i = 0; i < 5; i++)
        {
            infoGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        }

        _lblNowSong.Text = "歌曲: -";
        _lblNowArtist.Text = "歌手: -";
        _lblNowState.Text = "状态: -";
        _lblNowSource.Text = "来源: -";
        _lblNowRemaining.Text = "剩余: -";
        foreach (var lbl in new[] { _lblNowSong, _lblNowArtist, _lblNowState, _lblNowSource, _lblNowRemaining })
        {
            lbl.Dock = DockStyle.Fill;
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            lbl.AutoEllipsis = true;
            lbl.Margin = new Padding(0);
        }

        infoGrid.Controls.Add(_lblNowSong, 0, 0);
        infoGrid.Controls.Add(_lblNowArtist, 0, 1);
        infoGrid.Controls.Add(_lblNowState, 0, 2);
        infoGrid.Controls.Add(_lblNowSource, 0, 3);
        infoGrid.Controls.Add(_lblNowRemaining, 0, 4);
        info.Controls.Add(infoGrid);

        var controls = MakeGroup("播放控制");
        var ctrlGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(4)
        };
        ctrlGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        ctrlGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        var btnRow1 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
        btnRow1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        btnRow1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        btnRow1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        StyleButton(_btnNext, "下一首", 0);
        StyleButton(_btnPause, "暂停", 0);
        StyleButton(_btnResume, "恢复", 0);
        foreach (var btn in new[] { _btnNext, _btnPause, _btnResume })
        {
            btn.Dock = DockStyle.Fill;
            btn.Margin = new Padding(2);
        }
        btnRow1.Controls.Add(_btnNext, 0, 0);
        btnRow1.Controls.Add(_btnPause, 1, 0);
        btnRow1.Controls.Add(_btnResume, 2, 0);

        var btnRow2 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        btnRow2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        btnRow2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        StyleButton(_btnKugouRelogin, "酷狗重登", 0);
        StyleButton(_btnToggleRequest, "关闭点歌", 0);
        _btnKugouRelogin.Dock = DockStyle.Fill;
        _btnToggleRequest.Dock = DockStyle.Fill;
        _btnKugouRelogin.Margin = new Padding(2);
        _btnToggleRequest.Margin = new Padding(2);
        btnRow2.Controls.Add(_btnKugouRelogin, 0, 0);
        btnRow2.Controls.Add(_btnToggleRequest, 1, 0);

        ctrlGrid.Controls.Add(btnRow1, 0, 0);
        ctrlGrid.Controls.Add(btnRow2, 0, 1);
        controls.Controls.Add(ctrlGrid);

        var volumeGroup = MakeGroup("音量");
        var volLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(8, 4, 8, 4)
        };
        _volumeBar.Dock = DockStyle.Fill;
        _volumeBar.Minimum = 0;
        _volumeBar.Maximum = 100;
        _volumeBar.Value = Math.Clamp(_host.Config.Settings.Playback.Volume, 0, 100);
        _volumeBar.TickFrequency = 10;
        _volumeBar.TickStyle = TickStyle.None;
        _volumeBar.Margin = new Padding(0);
        volLayout.Controls.Add(_volumeBar, 0, 0);
        volumeGroup.Controls.Add(volLayout);

        var quick = MakeGroup("快捷操作");
        var quickGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(4)
        };
        for (var c = 0; c < 3; c++)
        {
            quickGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        }
        quickGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        quickGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        StyleButton(_btnSkipQueue, "跳过", 0);
        StyleButton(_btnRemoveQueue, "删除", 0);
        StyleButton(_btnClearQueue, "清空", 0);
        foreach (var btn in new[] { _btnSkipQueue, _btnRemoveQueue, _btnClearQueue })
        {
            btn.Dock = DockStyle.Fill;
            btn.Margin = new Padding(2);
        }
        quickGrid.Controls.Add(_btnSkipQueue, 0, 0);
        quickGrid.Controls.Add(_btnRemoveQueue, 1, 0);
        quickGrid.Controls.Add(_btnClearQueue, 2, 0);

        var modePanel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(2) };
        _cmbMode.Dock = DockStyle.Fill;
        _cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
        if (_cmbMode.Items.Count == 0)
        {
            _cmbMode.Items.AddRange(new object[] { "点歌+随机补位", "点歌优先", "随机播放" });
        }
        _cmbMode.SelectedIndex = ModeToIndex(_host.Config.Settings.Playback.Mode);
        modePanel.Controls.Add(_cmbMode);
        quickGrid.SetColumnSpan(modePanel, 2);
        quickGrid.Controls.Add(modePanel, 0, 1);

        var btnAdminQuick = new Button { Dock = DockStyle.Fill, Margin = new Padding(2) };
        StyleButton(btnAdminQuick, "管理后台", 0);
        btnAdminQuick.Click += (_, _) => OpenAdminPage();
        quickGrid.Controls.Add(btnAdminQuick, 2, 1);
        quick.Controls.Add(quickGrid);

        layout.Controls.Add(info, 0, 0);
        layout.Controls.Add(controls, 0, 1);
        layout.Controls.Add(volumeGroup, 0, 2);
        layout.Controls.Add(quick, 0, 3);
        return layout;
    }

    private Control BuildConnectPanel()
    {
        var box = MakeGroup("连接");
        box.Margin = new Padding(0, 4, 0, 0);
        box.Padding = new Padding(10, 4, 10, 6);

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(4, 0, 4, 0)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        var lblRid = new Label
        {
            Text = "直播间短号",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        _txtWebRid.Dock = DockStyle.Fill;
        _txtWebRid.Margin = new Padding(0, 2, 8, 2);
        _txtWebRid.Text = _host.Config.Settings.Douyin.WebRid;

        StyleButton(_btnConnect, "连接", 0);
        _btnConnect.Dock = DockStyle.Fill;
        _btnConnect.Margin = new Padding(0, 2, 8, 2);

        StyleButton(_btnKugouLogin, "酷狗登录", 0);
        _btnKugouLogin.Dock = DockStyle.Fill;
        _btnKugouLogin.Margin = new Padding(0, 2, 0, 2);

        row.Controls.Add(lblRid, 0, 0);
        row.Controls.Add(_txtWebRid, 1, 0);
        row.Controls.Add(_btnConnect, 2, 0);
        row.Controls.Add(_btnKugouLogin, 3, 0);
        box.Controls.Add(row);
        return box;
    }

    private static void StyleButton(Button btn, string text, int width)
    {
        btn.Text = text;
        btn.Height = 34;
        btn.MinimumSize = new Size(72, 34);
        if (width > 0)
        {
            btn.Width = width;
            btn.Anchor = AnchorStyles.Left;
        }

        btn.FlatStyle = FlatStyle.System;
        btn.Margin = new Padding(0, 2, 6, 2);
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
        _btnPause.Click += (_, _) => _host.Engine.Pause();
        _btnResume.Click += (_, _) => _host.Engine.Resume();
        _btnNext.Click += async (_, _) => await _host.Engine.SkipAsync();
        _btnKugouRelogin.Click += (_, _) => OpenKugouLoginPage();
        _btnToggleRequest.Click += (_, _) => ToggleSongRequest();
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

        _uiTimer.Interval = 2000;
        _uiTimer.Tick += (_, _) =>
        {
            RefreshPlayback();
            RefreshRuntimeStatus();
        };
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

    private void ToggleSongRequest()
    {
        var enabled = !_host.SongRequestControl.IsRequestEnabled;
        _host.SetSongRequestEnabled(enabled);
        _btnToggleRequest.Text = enabled ? "关闭点歌" : "开启点歌";
        AppendSystem(enabled ? "点歌已开启" : "点歌已关闭");
        RefreshRuntimeStatus();
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

    private string _lastSystemMessage = "";

    private void AppendSystem(string line)
    {
        _lastSystemMessage = line;
        RefreshAlertBar();
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
        var track = _host.PlaybackCommands.Playback.CurrentTrack;

        _lblDouyinConn.Text = status.DouyinOnline ? "在线" : "断开";
        _lblKugouConn.Text = !status.KugouOnline ? "离线"
            : status.KugouLoginStatus.Contains("未登录", StringComparison.Ordinal)
              || status.KugouLoginStatus.Contains("失效", StringComparison.Ordinal) ? "失效"
            : status.KugouFullPlaybackAvailable ? "完整版可用" : "完整版不可用";

        _lblHealthSong.Text = track?.SongName ?? "-";
        _lblHealthArtist.Text = track?.Artist ?? "-";
        var duration = Math.Max(1, status.DurationSec);
        _lblHealthProgress.Text = track == null
            ? "-"
            : $"{FormatTime(status.ProgressSec)}/{FormatTime(duration)}";
        _lblQueueWaiting.Text = status.WaitingQueueCount.ToString();
        _lblStartedAt.Text = status.StartedAt.ToString("MM-dd HH:mm");
        _lblUptime.Text = FormatUptime(status.Uptime);
        _lblTodaySongs.Text = status.TodaySongsPlayed.ToString();
        _lblTodayDanmaku.Text = status.TodayDanmakuCount.ToString();
        _lblTodayGifts.Text = status.TodayGiftCount.ToString();
        _lblTodayRequests.Text = status.TodaySongRequestCount.ToString();
        _lblRecentErrors.Text = status.RecentErrorCount.ToString();

        _btnToggleRequest.Text = status.SongRequestEnabled ? "关闭点歌" : "开启点歌";
        RefreshAlertBar(status);
    }

    private void RefreshAlertBar(RuntimeStatus? status = null)
    {
        status ??= _host.GetRuntimeStatus();
        var track = _host.PlaybackCommands.Playback.CurrentTrack;
        var song = track?.SongName ?? status.CurrentSong ?? "-";
        var dy = status.DouyinOnline ? "抖音:在线" : "抖音:断开";
        var kg = !status.KugouOnline ? "酷狗:离线"
            : status.KugouLoginStatus.Contains("未登录", StringComparison.Ordinal)
              || status.KugouLoginStatus.Contains("失效", StringComparison.Ordinal) ? "酷狗:失效"
            : status.KugouFullPlaybackAvailable ? "酷狗:完整版可用" : "酷狗:完整版不可用";
        var req = status.SongRequestEnabled ? "点歌:开" : "点歌:关";
        var err = status.RecentErrorCount > 0 ? $" | 异常:{status.RecentErrorCount}" : "";
        var msg = string.IsNullOrWhiteSpace(_lastSystemMessage) ? "" : $" | {_lastSystemMessage}";
        _lblAlertBar.Text = $"{dy} | {kg} | {req} | 播放:{song} | 队列:{status.WaitingQueueCount}{err}{msg}";

        if (status.RecentErrorCount > 0 || !status.DouyinOnline || !status.KugouOnline)
        {
            _lblAlertBar.BackColor = Color.FromArgb(255, 235, 235);
            _lblAlertBar.ForeColor = Color.DarkRed;
        }
        else if (_host.Config.Settings.Emergency.PauseInteraction)
        {
            _lblAlertBar.BackColor = Color.FromArgb(255, 230, 200);
            _lblAlertBar.ForeColor = Color.FromArgb(160, 70, 0);
        }
        else
        {
            _lblAlertBar.BackColor = Color.FromArgb(255, 248, 230);
            _lblAlertBar.ForeColor = Color.FromArgb(120, 80, 0);
        }
    }

    private void OpenAdminPage()
    {
        var admin = _host.Config.Settings.Admin;
        var adminUrl = $"http://127.0.0.1:{admin.Port}{admin.Path}";
        try
        {
            Process.Start(new ProcessStartInfo(adminUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开管理后台: {ex.Message}", AppBranding.DisplayName,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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
        var playback = _host.PlaybackCommands.Playback;
        if (track == null)
        {
            _lblNowSong.Text = "歌曲: -";
            _lblNowArtist.Text = "歌手: -";
            _lblNowState.Text = "状态: -";
            _lblNowSource.Text = "来源: -";
            _lblNowRemaining.Text = "剩余: -";
            RefreshStatus();
            return;
        }

        _lblNowSong.Text = $"歌曲: {track.SongName}";
        _lblNowArtist.Text = $"歌手: {track.Artist}";
        _lblNowState.Text = $"状态: {FormatPlaybackState(playback.State)}";
        _lblNowSource.Text = track.IsRandom ? "来源: 随机补位" : "来源: 点歌";
        var duration = Math.Max(1, playback.DurationSec);
        var remaining = Math.Max(0, duration - playback.ProgressSec);
        _lblNowRemaining.Text = $"剩余: {FormatTime(remaining)}";
        RefreshStatus();
    }

    private static string FormatPlaybackState(PlaybackState state) => state switch
    {
        PlaybackState.Playing => "播放中",
        PlaybackState.Paused => "已暂停",
        PlaybackState.RandomFill => "随机补位",
        PlaybackState.Idle => "空闲",
        _ => state.ToString()
    };

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
