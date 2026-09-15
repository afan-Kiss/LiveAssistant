using System.Diagnostics;
using LiveAssistant.Models;
using LiveAssistant.Services;
using LiveAssistant.Services.AiSpeech;

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

    // AI 语音互动
    private readonly CheckBox _chkAiEnabled = new();
    private readonly CheckBox _chkAiTestMode = new();
    private readonly ComboBox _cmbAiModel = new();
    private readonly ComboBox _cmbAiDevice = new();
    private readonly NumericUpDown _numAiInterval = new();
    private readonly NumericUpDown _numAiQueue = new();
    private readonly NumericUpDown _numAiMaxChars = new();
    private readonly Label _lblAiVoice = new();
    private readonly Label _lblAiTtsUrl = new();
    private readonly Label _lblAiOllamaStatus = new();
    private readonly Label _lblAiTtsStatus = new();
    private readonly Label _lblAiVoiceStatus = new();
    private readonly Label _lblAiPhase = new();
    private readonly Label _lblAiQueue = new();
    private readonly Label _lblAiLatestUser = new();
    private readonly Label _lblAiLatestContent = new();
    private readonly Label _lblAiReply = new();
    private readonly Label _lblAiHint = new();
    private readonly Label _lblAiTestStats = new();
    private readonly Button _btnAiRefreshModels = new();
    private readonly Button _btnAiTestVoice = new();
    private readonly Button _btnAiTestAi = new();
    private readonly Button _btnAiStop = new();
    private bool _aiUiLoading;

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
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Point(4, 4)
        };

        var tabPlay = new TabPage("播放") { BackColor = Color.White, Padding = new Padding(4) };
        var tabAi = new TabPage("AI语音") { BackColor = Color.White, Padding = new Padding(4) };
        tabPlay.Controls.Add(BuildPlaybackSidePanel());
        tabAi.Controls.Add(BuildAiSpeechPanel());
        tabs.TabPages.Add(tabPlay);
        tabs.TabPages.Add(tabAi);
        return tabs;
    }

    private Control BuildPlaybackSidePanel()
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

    private Control BuildAiSpeechPanel()
    {
        var outer = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.White
        };

        var group = MakeGroup("AI语音互动");
        group.Dock = DockStyle.Top;
        group.AutoSize = true;
        group.AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(4),
            Margin = new Padding(0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(string title, Control control, int height = 30)
        {
            var r = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            grid.Controls.Add(new Label
            {
                Text = title,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            }, 0, r);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 2, 0, 2);
            grid.Controls.Add(control, 1, r);
        }

        var s = _host.Config.Settings.AiSpeech;
        _chkAiEnabled.Text = "启用AI语音";
        _chkAiEnabled.Checked = s.Enabled;
        _chkAiTestMode.Text = "AI语音测试模式（真实弹幕，按间隔挑一条）";
        _chkAiTestMode.Checked = s.TestMode;

        _cmbAiModel.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbAiDevice.DropDownStyle = ComboBoxStyle.DropDownList;

        _lblAiVoice.Text = string.IsNullOrWhiteSpace(s.Voice) ? "my_voice" : s.Voice;
        _lblAiVoice.TextAlign = ContentAlignment.MiddleLeft;
        _lblAiTtsUrl.Text = string.IsNullOrWhiteSpace(s.TtsUrl) ? "127.0.0.1:9880" : s.TtsUrl.Replace("http://", "");
        _lblAiTtsUrl.TextAlign = ContentAlignment.MiddleLeft;

        _numAiInterval.Minimum = 3;
        _numAiInterval.Maximum = 60;
        _numAiInterval.Value = Math.Clamp(s.MinIntervalSeconds, 3, 60);
        _numAiQueue.Minimum = 1;
        _numAiQueue.Maximum = 20;
        _numAiQueue.Value = Math.Clamp(s.MaxQueueSize, 1, 20);
        _numAiMaxChars.Minimum = 10;
        _numAiMaxChars.Maximum = 120;
        _numAiMaxChars.Value = Math.Clamp(s.MaxReplyLength, 10, 120);

        foreach (var lbl in new[]
                 {
                     _lblAiOllamaStatus, _lblAiTtsStatus, _lblAiVoiceStatus, _lblAiPhase, _lblAiQueue,
                     _lblAiLatestUser, _lblAiLatestContent, _lblAiReply, _lblAiHint, _lblAiTestStats
                 })
        {
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            lbl.AutoEllipsis = true;
        }

        _lblAiOllamaStatus.Text = "● Ollama检测中";
        _lblAiTtsStatus.Text = "● GPT-SoVITS检测中";
        _lblAiVoiceStatus.Text = "● 声音检测中";
        _lblAiPhase.Text = "空闲";
        _lblAiQueue.Text = "0 / 5";
        _lblAiLatestUser.Text = "-";
        _lblAiLatestContent.Text = "-";
        _lblAiReply.Text = "-";
        _lblAiHint.Text = "";
        _lblAiTestStats.Text = "";
        _lblAiHint.ForeColor = Color.FromArgb(160, 80, 0);
        _lblAiTestStats.ForeColor = Color.DimGray;

        var modelRow = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, Margin = new Padding(0) };
        modelRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modelRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        _cmbAiModel.Dock = DockStyle.Fill;
        StyleButton(_btnAiRefreshModels, "刷新", 0);
        _btnAiRefreshModels.Dock = DockStyle.Fill;
        _btnAiRefreshModels.Margin = new Padding(4, 0, 0, 0);
        modelRow.Controls.Add(_cmbAiModel, 0, 0);
        modelRow.Controls.Add(_btnAiRefreshModels, 1, 0);

        var btnRow = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Dock = DockStyle.Fill, Margin = new Padding(0) };
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        StyleButton(_btnAiTestAi, "测试AI", 0);
        StyleButton(_btnAiTestVoice, "测试声音", 0);
        StyleButton(_btnAiStop, "停止播放", 0);
        foreach (var b in new[] { _btnAiTestAi, _btnAiTestVoice, _btnAiStop })
        {
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(2, 0, 2, 0);
        }
        btnRow.Controls.Add(_btnAiTestAi, 0, 0);
        btnRow.Controls.Add(_btnAiTestVoice, 1, 0);
        btnRow.Controls.Add(_btnAiStop, 2, 0);

        AddRow("", _chkAiEnabled, 28);
        AddRow("", _chkAiTestMode, 28);
        AddRow("AI模型", modelRow, 32);
        var modelHint = new Label
        {
            Text = "推荐 qwen3:8b；可选 qwen2.5:7b / qwen3.5:27b。人格：Config/ai_personality.txt",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            AutoEllipsis = true
        };
        AddRow("", modelHint, 22);
        AddRow("声音", _lblAiVoice, 24);
        AddRow("GPT-SoVITS", _lblAiTtsUrl, 24);
        AddRow("状态", _lblAiOllamaStatus, 22);
        AddRow("", _lblAiTtsStatus, 22);
        AddRow("", _lblAiVoiceStatus, 22);
        AddRow("输出设备", _cmbAiDevice, 32);
        AddRow("回复间隔", _numAiInterval, 30);
        AddRow("最大排队", _numAiQueue, 30);
        AddRow("最大字数", _numAiMaxChars, 30);
        AddRow("操作", btnRow, 36);
        AddRow("当前状态", _lblAiPhase, 24);
        AddRow("队列", _lblAiQueue, 24);
        AddRow("最新弹幕", _lblAiLatestUser, 24);
        AddRow("", _lblAiLatestContent, 24);
        AddRow("AI回复", _lblAiReply, 40);
        AddRow("提示", _lblAiHint, 24);
        AddRow("测试", _lblAiTestStats, 48);

        group.Controls.Add(grid);
        outer.Controls.Add(group);
        return outer;
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

        WireAiSpeechEvents();

        _uiTimer.Interval = 2000;
        _uiTimer.Tick += (_, _) =>
        {
            RefreshPlayback();
            RefreshRuntimeStatus();
            RefreshAiSpeechStatus();
        };
        _uiTimer.Start();

        Load += async (_, _) =>
        {
            try
            {
                await InitAiSpeechUiAsync();
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

    private async void OpenKugouLoginPage()
    {
        var url = _host.Kugou.LoginPageUrl;
        try
        {
            var wasLoggedIn = _host.Kugou.LoginSnapshot.LoggedIn;
            if (wasLoggedIn)
            {
                await _host.Kugou.LogoutAsync();
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
            AppendSystem(wasLoggedIn
                ? "已清除旧登录并打开酷狗扫码页；请用手机酷狗 App 扫码，登录后会自动领取试用会员"
                : "已打开酷狗扫码页；请用手机酷狗 App 扫码，登录后会自动领取试用会员");
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
        RefreshAiSpeechStatus();
    }

    private void WireAiSpeechEvents()
    {
        _host.AiSpeech.StatusChanged += () =>
        {
            if (IsDisposed)
            {
                return;
            }

            BeginInvoke(RefreshAiSpeechStatus);
        };

        _chkAiEnabled.CheckedChanged += (_, _) =>
        {
            if (_aiUiLoading)
            {
                return;
            }

            _host.AiSpeech.SaveSettingsFromUi(s => s.Enabled = _chkAiEnabled.Checked);
            AppendSystem(_chkAiEnabled.Checked ? "AI语音已启用" : "AI语音已关闭");
        };

        _chkAiTestMode.CheckedChanged += (_, _) =>
        {
            if (_aiUiLoading)
            {
                return;
            }

            _host.AiSpeech.SaveSettingsFromUi(s => s.TestMode = _chkAiTestMode.Checked);
        };

        _cmbAiModel.SelectedIndexChanged += (_, _) =>
        {
            if (_aiUiLoading)
            {
                return;
            }

            var model = ResolveSelectedAiModel();
            if (string.IsNullOrWhiteSpace(model))
            {
                return;
            }

            _host.AiSpeech.SaveSettingsFromUi(s => s.Model = model);
        };

        _cmbAiDevice.SelectedIndexChanged += (_, _) =>
        {
            if (_aiUiLoading || _cmbAiDevice.SelectedItem is not AudioOutputDeviceInfo device)
            {
                return;
            }

            _host.AiSpeech.SaveSettingsFromUi(s =>
            {
                s.OutputDeviceNumber = device.DeviceNumber;
                s.OutputDeviceName = device.DeviceNumber < 0 ? "" : device.Name;
            });
        };

        void SaveNumeric()
        {
            if (_aiUiLoading)
            {
                return;
            }

            _host.AiSpeech.SaveSettingsFromUi(s =>
            {
                s.MinIntervalSeconds = (int)_numAiInterval.Value;
                s.MaxQueueSize = (int)_numAiQueue.Value;
                s.MaxReplyLength = (int)_numAiMaxChars.Value;
            });
        }

        _numAiInterval.ValueChanged += (_, _) => SaveNumeric();
        _numAiQueue.ValueChanged += (_, _) => SaveNumeric();
        _numAiMaxChars.ValueChanged += (_, _) => SaveNumeric();

        _btnAiRefreshModels.Click += async (_, _) =>
        {
            _btnAiRefreshModels.Enabled = false;
            try
            {
                await RefreshAiModelsAsync();
                await _host.AiSpeech.RefreshHealthAsync();
                RefreshAiSpeechStatus();
            }
            finally
            {
                _btnAiRefreshModels.Enabled = true;
            }
        };

        _btnAiStop.Click += (_, _) =>
        {
            _host.AiSpeech.StopCurrentPlayback();
            AppendSystem("已停止当前 AI 语音播放");
        };

        _btnAiTestVoice.Click += async (_, _) =>
        {
            _btnAiTestVoice.Enabled = false;
            try
            {
                PersistAiSettingsFromControls();
                var result = await _host.AiSpeech.TestVoiceAsync();
                if (result.Success)
                {
                    _lblAiTestStats.Text = $"测试声音成功 | TTS {result.TtsMs} ms | 总 {result.TotalMs} ms";
                    AppendSystem("AI 测试声音播放完成");
                }
                else
                {
                    _lblAiTestStats.Text = $"测试声音失败：{result.Error}";
                    AppendSystem($"语音服务不可用：{result.Error}");
                }
            }
            catch (Exception ex)
            {
                _lblAiTestStats.Text = $"测试声音异常：{ex.Message}";
                AppendSystem($"语音服务不可用：{ex.Message}");
            }
            finally
            {
                _btnAiTestVoice.Enabled = true;
                RefreshAiSpeechStatus();
            }
        };

        _btnAiTestAi.Click += async (_, _) =>
        {
            _btnAiTestAi.Enabled = false;
            try
            {
                PersistAiSettingsFromControls();
                if (string.IsNullOrWhiteSpace(_host.Config.Settings.AiSpeech.Model))
                {
                    _lblAiTestStats.Text = "请先选择 Ollama 模型";
                    AppendSystem("AI模型不可用：未选择模型");
                    return;
                }

                var result = await _host.AiSpeech.TestAiAsync();
                _lblAiLatestUser.Text = result.Nickname;
                _lblAiLatestContent.Text = result.Danmaku;
                _lblAiReply.Text = string.IsNullOrWhiteSpace(result.Reply) ? "-" : result.Reply;
                if (result.Success)
                {
                    _lblAiTestStats.Text =
                        $"收到弹幕：{result.Nickname}：{result.Danmaku}\n" +
                        $"AI回复：{result.Reply}\n" +
                        $"生成耗时：{result.OllamaMs} ms | TTS：{result.TtsMs} ms | 总：{result.TotalMs} ms";
                    AppendSystem("AI 完整链路测试成功");
                }
                else
                {
                    _lblAiTestStats.Text =
                        $"收到弹幕：{result.Nickname}：{result.Danmaku}\n" +
                        $"失败：{result.Error}\n" +
                        $"生成：{result.OllamaMs} ms | TTS：{result.TtsMs} ms | 总：{result.TotalMs} ms";
                    AppendSystem(result.Error?.Contains("语音", StringComparison.Ordinal) == true
                        ? $"语音服务不可用：{result.Error}"
                        : $"AI模型不可用：{result.Error}");
                }
            }
            catch (Exception ex)
            {
                _lblAiTestStats.Text = $"测试AI异常：{ex.Message}";
                AppendSystem($"AI测试失败：{ex.Message}");
            }
            finally
            {
                _btnAiTestAi.Enabled = true;
                RefreshAiSpeechStatus();
            }
        };
    }

    private async Task InitAiSpeechUiAsync()
    {
        _aiUiLoading = true;
        try
        {
            LoadAiDevices();
            await RefreshAiModelsAsync();
            await _host.AiSpeech.RefreshHealthAsync();
            RefreshAiSpeechStatus();
        }
        finally
        {
            _aiUiLoading = false;
        }
    }

    private void LoadAiDevices()
    {
        var devices = AudioOutputDevices.ListDevices();
        _cmbAiDevice.Items.Clear();
        foreach (var d in devices)
        {
            _cmbAiDevice.Items.Add(d);
        }

        var s = _host.Config.Settings.AiSpeech;
        var selectedNumber = AudioOutputDevices.ResolveDeviceNumber(s.OutputDeviceName, s.OutputDeviceNumber);
        for (var i = 0; i < _cmbAiDevice.Items.Count; i++)
        {
            if (_cmbAiDevice.Items[i] is AudioOutputDeviceInfo info && info.DeviceNumber == selectedNumber)
            {
                _cmbAiDevice.SelectedIndex = i;
                return;
            }
        }

        if (_cmbAiDevice.Items.Count > 0)
        {
            _cmbAiDevice.SelectedIndex = 0;
        }
    }

    private async Task RefreshAiModelsAsync()
    {
        var installed = await _host.AiSpeech.ListOllamaModelsAsync();
        var merged = AiSpeechModelsCatalog.MergeWithInstalled(installed);
        var previous = AiSpeechModelsCatalog.ResolveDefault(
            _host.Config.Settings.AiSpeech.Model,
            merged);
        _aiUiLoading = true;
        try
        {
            _cmbAiModel.Items.Clear();
            foreach (var m in merged)
            {
                _cmbAiModel.Items.Add(new AiModelComboItem(m));
            }

            if (_cmbAiModel.Items.Count == 0)
            {
                return;
            }

            var selectedIdx = -1;
            for (var i = 0; i < _cmbAiModel.Items.Count; i++)
            {
                if (_cmbAiModel.Items[i] is AiModelComboItem item
                    && item.ModelId.Equals(previous, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIdx = i;
                    break;
                }
            }

            if (selectedIdx < 0)
            {
                selectedIdx = 0;
            }

            _cmbAiModel.SelectedIndex = selectedIdx;
            var selected = ResolveSelectedAiModel();
            if (!string.IsNullOrWhiteSpace(selected)
                && !selected.Equals(_host.Config.Settings.AiSpeech.Model, StringComparison.OrdinalIgnoreCase))
            {
                _host.AiSpeech.SaveSettingsFromUi(s => s.Model = selected);
            }
        }
        finally
        {
            _aiUiLoading = false;
        }
    }

    private string ResolveSelectedAiModel()
    {
        return _cmbAiModel.SelectedItem switch
        {
            AiModelComboItem item => item.ModelId,
            string s => s.Trim(),
            _ => ""
        };
    }

    private void PersistAiSettingsFromControls()
    {
        _host.AiSpeech.SaveSettingsFromUi(s =>
        {
            s.Enabled = _chkAiEnabled.Checked;
            s.TestMode = _chkAiTestMode.Checked;
            s.MinIntervalSeconds = (int)_numAiInterval.Value;
            s.MaxQueueSize = (int)_numAiQueue.Value;
            s.MaxReplyLength = (int)_numAiMaxChars.Value;
            var model = ResolveSelectedAiModel();
            if (!string.IsNullOrWhiteSpace(model))
            {
                s.Model = model;
            }

            if (_cmbAiDevice.SelectedItem is AudioOutputDeviceInfo device)
            {
                s.OutputDeviceNumber = device.DeviceNumber;
                s.OutputDeviceName = device.DeviceNumber < 0 ? "" : device.Name;
            }
        });
    }

    private void RefreshAiSpeechStatus()
    {
        if (IsDisposed || !_lblAiPhase.IsHandleCreated)
        {
            return;
        }

        var status = _host.AiSpeech.GetStatus();
        _lblAiPhase.Text = status.PhaseText;
        _lblAiQueue.Text = $"{status.QueueCount} / {status.MaxQueueSize}";
        _lblAiOllamaStatus.Text = status.OllamaOk ? "● Ollama正常" : "● Ollama不可用";
        _lblAiOllamaStatus.ForeColor = status.OllamaOk ? Color.ForestGreen : Color.Firebrick;
        _lblAiTtsStatus.Text = status.TtsOk ? "● GPT-SoVITS正常" : "● GPT-SoVITS不可用";
        _lblAiTtsStatus.ForeColor = status.TtsOk ? Color.ForestGreen : Color.Firebrick;
        _lblAiVoiceStatus.Text = status.VoiceReady
            ? $"● 我的声音已加载（{status.VoiceName}）"
            : "● 声音未就绪";
        _lblAiVoiceStatus.ForeColor = status.VoiceReady ? Color.ForestGreen : Color.Firebrick;
        _lblAiVoice.Text = status.VoiceName;
        if (!string.IsNullOrWhiteSpace(status.LatestNickname))
        {
            _lblAiLatestUser.Text = status.LatestNickname;
        }

        if (!string.IsNullOrWhiteSpace(status.LatestContent))
        {
            _lblAiLatestContent.Text = status.LatestContent;
        }

        if (!string.IsNullOrWhiteSpace(status.LatestReply))
        {
            _lblAiReply.Text = status.LatestReply;
        }

        _lblAiHint.Text = status.ServiceHint;
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
        var track = _host.PlaybackCommands.Playback.CurrentTrack;
        if (now != null)
        {
            var songName = !string.IsNullOrWhiteSpace(track?.SongName) ? track.SongName : now.SongName;
            var artist = !string.IsNullOrWhiteSpace(track?.Artist) ? track.Artist : now.Artist;
            var playing = new ListViewItem("▶")
            {
                Tag = now.Id
            };
            playing.SubItems.Add(now.IsRandom ? "随机" : now.Nickname);
            playing.SubItems.Add($"{songName} - {artist}");
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
