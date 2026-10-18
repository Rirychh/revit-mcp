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
        private AnthropicClient? _client;
        private bool _busy;

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
            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x5B, 0x4A, 0x8A)),
                Padding = new Thickness(12, 6, 8, 6)
            };
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock { Text = "Claude · Revit   © 2026 Rita (Rirychh)", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 0);
            headerGrid.Children.Add(title);
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
                ToolTip = "檢視或更換 Anthropic API 金鑰"
            };
            keyToggle.Click += (s, e) => ToggleKeyRow();
            Grid.SetColumn(keyToggle, 3);
            headerGrid.Children.Add(keyToggle);

            header.Child = headerGrid;
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
            AddMessage(_apiKey == "" ? "請先在上方貼上 Anthropic API 金鑰並按「存金鑰」，即可開始對話。"
                                    : "面板就緒。直接輸入指令，例如「列出所有樓層」。", false);
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
            try { _apiKey = KeyStore.Load(); } catch { _apiKey = ""; }
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
                ? "尚未設定金鑰。貼上 Anthropic API 金鑰後按「存金鑰」。"
                : $"目前金鑰：{Mask(_apiKey)}　存放於：{KeyStore.StorageLabel}　（貼上新金鑰可直接覆蓋）";
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

            try { KeyStore.Save(k); }
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
                "確定要刪除已儲存的 API 金鑰嗎？", "Claude · Revit",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK) return;

            try { KeyStore.Delete(); } catch { }
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
                _client = new AnthropicClient(_apiKey, UiApp, Executor);
                try
                {
                    var mf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "revit-mcp-model.txt");
                    if (File.Exists(mf)) { var m = File.ReadAllText(mf).Trim(); if (m != "") _client.Model = m; }
                }
                catch { }
            }

            Action<string> onText = t => Dispatcher.Invoke(() => AddMessage(t, false));
            Action<string> onNote = t => Dispatcher.Invoke(() => AddNote(t));

            try
            {
                // 背景緒跑對話：工具執行會阻塞等 Revit 主緒，不能在 UI 緒跑（會死鎖）
                await Task.Run(() => _client.SendAsync(text, onText, onNote));
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

        public void AddMessage(string text, bool isUser)
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
                Text = isUser ? _userName : "Claude",
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
