// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.IO;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.UI;
using RevitMcpAddin.Ai;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace RevitMcpAddin.UI
{
    /// <summary>
    /// Revit Dockable Pane 的 Claude 聊天面板（純程式碼 WPF）。
    /// 面板內直接跟 Claude 對話，Claude 透過工具操作目前開啟的 Revit 模型。
    /// </summary>
    public class ChatPanel : UserControl, IDockablePaneProvider
    {
        public static readonly DockablePaneId PaneId =
            new(new Guid("7E2C1A44-9B3D-4E71-9C2A-1F5B6D0A9E10"));

        private static readonly string NameFile =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "revit-mcp-username.txt");

        private string _userName = "你";

        private readonly StackPanel _messages;
        private readonly ScrollViewer _scroll;
        private readonly TextBox _input;
        private readonly Border _keyRow;
        private readonly PasswordBox _keyBox;
        private readonly TextBlock _keyStatus;
        private readonly TextBox _nameBox;

        private string _apiKey = "";
        private IChatClient? _client;
        private bool _busy;
        private AiProvider _provider = AiProvider.Claude;
        private readonly TextBlock _title;
        private readonly ComboBox _providerBox;
        private readonly TextBox _modelBox;

        // 由 App.OnIdling 注入
        public RevitApiExecutor? Executor { get; set; }
        public UIApplication? UiApp { get; set; }

        public ChatPanel()
        {
            var root = new Grid { Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            LoadName();
            PanelSettings.Load();
            _provider = PanelSettings.Provider;
            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x5B, 0x4A, 0x8A)),
                Padding = new Thickness(12, 6, 8, 6)
            };
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _title = new TextBlock { Text = TitleText(), Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_title, 0);
            headerGrid.Children.Add(_title);
            _nameBox = new TextBox { Text = _userName, MinWidth = 70, Padding = new Thickness(5, 2, 5, 2), FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "你的顯示名稱" };
            Grid.SetColumn(_nameBox, 1);
            headerGrid.Children.Add(_nameBox);
            var nameSave = new Button { Content = "改名", MinWidth = 40, Margin = new Thickness(6, 0, 0, 0), FontSize = 12 };
            nameSave.Click += (s, e) => SaveName();
            Grid.SetColumn(nameSave, 2);
            headerGrid.Children.Add(nameSave);

            // 金鑰按鈕：隨時可叫出金鑰列修改（不再只有第一次能設定）
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var keyToggle = new Button
            {
                Content = "金鑰",
                MinWidth = 44,
                Margin = new Thickness(6, 0, 0, 0),
                FontSize = 12,
                ToolTip = "檢視或更換目前 AI 的 API 金鑰"
            };
            keyToggle.Click += (s, e) => ToggleKeyRow();
            Grid.SetColumn(keyToggle, 3);
            headerGrid.Children.Add(keyToggle);

            // 第二列：AI 供應商與模型
            var provGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            provGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            provGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            provGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            provGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var provLabel = new TextBlock { Text = "AI", Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(provLabel, 0);
            provGrid.Children.Add(provLabel);
            _providerBox = new ComboBox { MinWidth = 80, FontSize = 12, ToolTip = "切換 AI 供應商（切換後重新開始對話）" };
            foreach (var p in AiProviders.All) _providerBox.Items.Add(AiProviders.DisplayName(p));
            _providerBox.SelectedIndex = Array.IndexOf(AiProviders.All, _provider);
            _providerBox.SelectionChanged += (s, e) => SwitchProvider(AiProviders.All[Math.Max(0, _providerBox.SelectedIndex)]);
            Grid.SetColumn(_providerBox, 1);
            provGrid.Children.Add(_providerBox);
            var modelLabel = new TextBlock { Text = "模型", Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
            Grid.SetColumn(modelLabel, 2);
            provGrid.Children.Add(modelLabel);
            _modelBox = new TextBox { Text = PanelSettings.GetModel(_provider), FontSize = 12, Padding = new Thickness(5, 2, 5, 2), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "模型 ID，按 Enter 套用；清空後按 Enter 恢復預設" };
            _modelBox.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { ApplyModel(); e.Handled = true; } };
            _modelBox.LostFocus += (s, e) => ApplyModel();
            Grid.SetColumn(_modelBox, 3);
            provGrid.Children.Add(_modelBox);

            var headerStack = new StackPanel();
            headerStack.Children.Add(headerGrid);
            headerStack.Children.Add(provGrid);
            header.Child = headerStack;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // API 金鑰列（可隨時由標題列「金鑰」按鈕叫出修改）
            _keyRow = new Border { Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xEC, 0xF7)), Padding = new Thickness(10, 8, 10, 8) };
            var keyPanel = new StackPanel();

            _keyStatus = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x4A, 0x8A)),
                Margin = new Thickness(2, 0, 2, 5),
                TextWrapping = TextWrapping.Wrap
            };
            keyPanel.Children.Add(_keyStatus);

            var keyGrid = new Grid();
            keyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            keyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            keyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            keyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _keyBox = new PasswordBox { MinHeight = 28, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
            _keyBox.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { SaveKey(); e.Handled = true; } };
            Grid.SetColumn(_keyBox, 0);
            keyGrid.Children.Add(_keyBox);

            var saveKey = new Button { Content = "存金鑰", MinWidth = 56, Margin = new Thickness(8, 0, 0, 0) };
            saveKey.Click += (s, e) => SaveKey();
            Grid.SetColumn(saveKey, 1);
            keyGrid.Children.Add(saveKey);

            var clearKey = new Button { Content = "清除", MinWidth = 44, Margin = new Thickness(6, 0, 0, 0), ToolTip = "刪除已儲存的金鑰" };
            clearKey.Click += (s, e) => ClearKey();
            Grid.SetColumn(clearKey, 2);
            keyGrid.Children.Add(clearKey);

            var cancelKey = new Button { Content = "關閉", MinWidth = 44, Margin = new Thickness(6, 0, 0, 0) };
            cancelKey.Click += (s, e) => { _keyBox.Clear(); if (_apiKey != "") _keyRow.Visibility = Visibility.Collapsed; };
            Grid.SetColumn(cancelKey, 3);
            keyGrid.Children.Add(cancelKey);

            keyPanel.Children.Add(keyGrid);
            _keyRow.Child = keyPanel;
            Grid.SetRow(_keyRow, 1);
            root.Children.Add(_keyRow);

            _messages = new StackPanel { Margin = new Thickness(10) };
            _scroll = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(_scroll, 2);
            root.Children.Add(_scroll);

            var inputBar = new Grid { Margin = new Thickness(10, 4, 10, 10) };
            inputBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _input = new TextBox { MinHeight = 34, Padding = new Thickness(8, 6, 8, 6), TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, VerticalContentAlignment = VerticalAlignment.Center };
            _input.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { OnSend(); e.Handled = true; } };
            Grid.SetColumn(_input, 0);
            inputBar.Children.Add(_input);
            var sendBtn = new Button { Content = "送出", MinWidth = 56, Margin = new Thickness(8, 0, 0, 0), Background = new SolidColorBrush(Color.FromRgb(0x24, 0x1C, 0x33)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            sendBtn.Click += (s, e) => OnSend();
            Grid.SetColumn(sendBtn, 1);
            inputBar.Children.Add(sendBtn);
            Grid.SetRow(inputBar, 3);
            root.Children.Add(inputBar);

            Content = root;

            LoadKey();
            AddMessage(_apiKey == "" ? $"請先在上方貼上{AiProviders.KeyLabel(_provider)}並按「存金鑰」，即可開始對話。"
                                    : "面板就緒。直接輸入指令，例如「列出所有樓層」。", false);
        }

        private string TitleText() => AiProviders.DisplayName(_provider) + " · Revit   © 2026 Rita (Rirychh)";

        /// <summary>切換 AI 供應商：載入該家金鑰與模型，重新開始對話。</summary>
        private void SwitchProvider(AiProvider p)
        {
            if (p == _provider) return;
            if (_busy)
            {
                _providerBox.SelectedIndex = Array.IndexOf(AiProviders.All, _provider);
                AddNote("回覆進行中，等這輪結束再切換。");
                return;
            }
            _provider = p;
            PanelSettings.Provider = p;
            PanelSettings.Save();
            _client = null;
            _title.Text = TitleText();
            _modelBox.Text = PanelSettings.GetModel(p);
            LoadKey();
            AddMessage(_apiKey == ""
                ? $"已切換到 {AiProviders.DisplayName(p)}。請先貼上{AiProviders.KeyLabel(p)}並按「存金鑰」。"
                : $"已切換到 {AiProviders.DisplayName(p)}（{PanelSettings.GetModel(p)}），對話重新開始。", false);
        }

        /// <summary>套用模型欄位：空白＝恢復預設。改動後下一則訊息起生效。</summary>
        private void ApplyModel()
        {
            var before = PanelSettings.GetModel(_provider);
            PanelSettings.SetModel(_provider, _modelBox.Text);
            var after = PanelSettings.GetModel(_provider);
            _modelBox.Text = after;
            if (after == before) return;
            PanelSettings.Save();
            if (_client != null) _client.Model = after;
            AddNote($"模型改為 {after}");
        }

        private void LoadName()
        {
            try { if (File.Exists(NameFile)) { var n = File.ReadAllText(NameFile).Trim(); if (n != "") _userName = n; } } catch { }
        }

        private void SaveName()
        {
            var n = _nameBox.Text.Trim();
            if (n == "") { _nameBox.Text = _userName; return; }
            _userName = n;
            try { File.WriteAllText(NameFile, n); } catch { }
        }

        private void LoadKey()
        {
            try { _apiKey = KeyStore.Load(_provider); } catch { _apiKey = ""; }
            UpdateKeyStatus();
            _keyRow.Visibility = _apiKey == "" ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>標題列「金鑰」按鈕：顯示／隱藏金鑰列，隨時可更換金鑰。</summary>
        private void ToggleKeyRow()
        {
            if (_keyRow.Visibility == Visibility.Visible && _apiKey != "")
            {
                _keyBox.Clear();
                _keyRow.Visibility = Visibility.Collapsed;
                return;
            }

            UpdateKeyStatus();
            _keyBox.Clear();
            _keyRow.Visibility = Visibility.Visible;
            _keyBox.Focus();
        }

        /// <summary>顯示目前金鑰狀態（遮罩，只露頭尾）。</summary>
        private void UpdateKeyStatus()
        {
            _keyStatus.Text = _apiKey == ""
                ? $"尚未設定金鑰。貼上{AiProviders.KeyLabel(_provider)}後按「存金鑰」。"
                : $"{AiProviders.DisplayName(_provider)} 金鑰：{Mask(_apiKey)}　存放於：{KeyStore.StorageLabel(_provider)}　（貼上新金鑰可直接覆蓋）";
        }

        private static string Mask(string key)
        {
            if (key.Length <= 12) return new string('•', key.Length);
            return key.Substring(0, 8) + new string('•', 6) + key.Substring(key.Length - 4);
        }

        private void SaveKey()
        {
            var k = _keyBox.Password.Trim();
            if (k == "")
            {
                _keyStatus.Text = "沒有輸入內容，未變更。";
                return;
            }

            try { KeyStore.Save(_provider, k); }
            catch (Exception ex)
            {
                _keyStatus.Text = "寫入失敗：" + ex.Message;
                return;
            }

            var changed = _apiKey != "" && _apiKey != k;
            _apiKey = k;
            _client = null;              // 重建客戶端，新金鑰立即生效
            _keyBox.Clear();
            UpdateKeyStatus();
            _keyRow.Visibility = Visibility.Collapsed;
            AddMessage(changed ? "金鑰已更換，對話已重新開始。" : "金鑰已儲存。可以開始對話了。", false);
        }

        /// <summary>清除已儲存的金鑰。</summary>
        private void ClearKey()
        {
            var ok = System.Windows.MessageBox.Show(
                $"確定要刪除已儲存的{AiProviders.KeyLabel(_provider)}嗎？", TitleText(),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK) return;

            try { KeyStore.Delete(_provider); } catch { }
            _apiKey = "";
            _client = null;
            _keyBox.Clear();
            UpdateKeyStatus();
            _keyRow.Visibility = Visibility.Visible;
            AddMessage("金鑰已清除。請輸入新的金鑰才能繼續對話。", false);
        }

        private async void OnSend()
        {
            var text = _input.Text.Trim();
            if (string.IsNullOrEmpty(text) || _busy) return;
            if (_apiKey == "") { AddMessage("尚未設定 API 金鑰。", false); UpdateKeyStatus(); _keyRow.Visibility = Visibility.Visible; return; }
            if (UiApp == null || Executor == null) { AddMessage("Revit 尚未就緒，請開啟一份文件後再試。", false); return; }

            _input.Clear();
            AddMessage(text, true);
            _busy = true;

            if (_client == null)
            {
                _client = AiProviders.Create(_provider, _apiKey, UiApp, Executor);
                _client.Model = PanelSettings.GetModel(_provider);
            }

            var client = _client;
            var speaker = AiProviders.DisplayName(_provider);
            Action<string> onText = t => Dispatcher.Invoke(() => AddMessage(t, false, speaker));
            Action<string> onNote = t => Dispatcher.Invoke(() => AddNote(t));

            try
            {
                // 背景緒跑對話：工具執行會阻塞等 Revit 主緒，不能在 UI 緒跑（會死鎖）
                await Task.Run(() => client.SendAsync(text, onText, onNote));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AddMessage("[錯誤] " + ex.Message, false));
            }
            finally { _busy = false; }
        }

        private void AddNote(string text)
        {
            _messages.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x76, 0x90)),
                FontSize = 11,
                Margin = new Thickness(4, 2, 4, 2)
            });
            _scroll.ScrollToEnd();
        }

        public void AddMessage(string text, bool isUser, string? speaker = null)
        {
            var bubble = new Border
            {
                Background = isUser ? new SolidColorBrush(Color.FromRgb(0xEC, 0xE6, 0xF5)) : new SolidColorBrush(Colors.White),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = 320
            };
            bubble.Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28)), FontSize = 13 };

            var group = new StackPanel { HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left };
            group.Children.Add(new TextBlock
            {
                Text = isUser ? _userName : (speaker ?? AiProviders.DisplayName(_provider)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x76, 0x90)),
                FontSize = 10,
                Margin = new Thickness(4, 2, 4, 0),
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
            });
            group.Children.Add(bubble);
            _messages.Children.Add(group);
            _scroll.ScrollToEnd();
        }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
        }
    }
}
