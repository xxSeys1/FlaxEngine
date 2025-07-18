// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using FlaxEditor.GUI;
using FlaxEditor.GUI.ContextMenu;
using FlaxEditor.GUI.Input;
using FlaxEditor.Options;
using FlaxEngine;
using FlaxEngine.GUI;

namespace FlaxEditor.Windows
{
    /// <summary>
    /// Editor window used to show engine output logs.
    /// </summary>
    /// <seealso cref="FlaxEditor.Windows.EditorWindow" />
    public sealed class OutputLogWindow : EditorWindow
    {
        /// <summary>
        /// The single log message entry.
        /// </summary>
        private struct Entry
        {
            /// <summary>
            /// The log level.
            /// </summary>
            public LogType Level;

            /// <summary>
            /// The log time (in UTC local format).
            /// </summary>
            public DateTime Time;

            /// <summary>
            /// The message contents.
            /// </summary>
            public string Message;
        };

        private struct TextBlockTag
        {
            internal enum Types
            {
                CodeLocation
            };

            public Types Type;
            public string Url;
            public int Line;
        }

        /// <summary>
        /// The output log textbox.
        /// </summary>
        /// <seealso cref="FlaxEngine.GUI.RichTextBoxBase" />
        private sealed class OutputTextBox : RichTextBoxBase
        {
            /// <summary>
            /// The parent window.
            /// </summary>
            public OutputLogWindow Window;

            /// <summary>
            /// The default text style.
            /// </summary>
            public TextBlockStyle DefaultStyle;

            /// <summary>
            /// The warning text style.
            /// </summary>
            public TextBlockStyle WarningStyle;

            /// <summary>
            /// The error text style.
            /// </summary>
            public TextBlockStyle ErrorStyle;

            public OutputTextBox()
            {
                _consumeAllKeyDownEvents = false;
            }

            /// <inheritdoc />
            protected override void OnParseTextBlocks()
            {
                if (ParseTextBlocks != null)
                {
                    ParseTextBlocks(_text, _textBlocks);
                    return;
                }

                // Use cached text blocks
                _textBlocks.Clear();
                _textBlocks.AddRange(Window._textBlocks);
            }

            /// <inheritdoc />
            public override bool OnMouseDoubleClick(Float2 location, MouseButton button)
            {
                // Click on text block
                int textLength = TextLength;
                if (textLength != 0)
                {
                    var hitPos = CharIndexAtPoint(ref location);
                    if (hitPos != -1 && GetTextBlock(hitPos, out var textBlock) && textBlock.Tag is TextBlockTag tag)
                    {
                        switch (tag.Type)
                        {
                        case TextBlockTag.Types.CodeLocation:
                            Window.Editor.CodeEditing.OpenFile(tag.Url, tag.Line);
                            return true;
                        }
                    }
                }

                return base.OnMouseDoubleClick(location, button);
            }
        }

        /// <summary>
        /// Command line input textbox control which can execute debug commands.
        /// </summary>
        private class CommandLineBox : TextBox
        {
            private sealed class Item : ItemsListContextMenu.Item
            {
                public CommandLineBox Owner;

                public Item()
                {
                }

                protected override void GetTextRect(out Rectangle rect)
                {
                    rect = new Rectangle(2, 0, Width - 4, Height);
                }

                public override bool OnCharInput(char c)
                {
                    if (Owner != null && (!Owner._searchPopup?.Visible ?? true))
                    {
                        // Redirect input into search textbox while typing and using command history
                        Owner.Set(Owner.Text + c);
                        return true;
                    }
                    else if (Owner != null && Owner._searchPopup != null && Owner._searchPopup.Visible)
                    {
                        // Redirect input into search textbox while typing and using command history
                        Owner.OnCharInput(c);
                        return true;
                    }
                    return false;
                }

                public override bool OnKeyDown(KeyboardKeys key)
                {
                    switch (key)
                    {
                    case KeyboardKeys.Delete:
                    case KeyboardKeys.Backspace:
                        if (Owner != null && (!Owner._searchPopup?.Visible ?? true))
                        {
                            // Redirect input into search textbox while typing and using command history
                            Owner.OnKeyDown(key);
                            return true;
                        }
                        break;
                    case KeyboardKeys.ArrowLeft:
                        if (Owner != null && (!Owner._searchPopup?.Visible ?? true))
                        {
                            // Focus back the input field as user want to modify command from history
                            Owner._searchPopup?.Hide();
                            Owner.RootWindow.Focus();
                            Owner.Focus();
                            Owner.OnKeyDown(key);
                            return true;
                        }
                        break;
                    case KeyboardKeys.ArrowDown:
                    case KeyboardKeys.ArrowUp:
                        // UI navigation
                        return base.OnKeyDown(key);
                    default:
                        if (Owner != null && (Owner._searchPopup?.Visible ?? false))
                        {
                            // Redirect input into search textbox while typing and using command history
                            Owner.OnKeyDown(key);
                            return true;
                        }
                        break;
                    }

                    return base.OnKeyDown(key);
                }

                public override void OnDestroy()
                {
                    Owner = null;
                    base.OnDestroy();
                }
            }

            private OutputLogWindow _window;
            private ItemsListContextMenu _searchPopup;
            private bool _isSettingText;

            public CommandLineBox(float x, float y, float width, OutputLogWindow window)
            : base(false, x, y, width)
            {
                WatermarkText = ">";
                _window = window;
            }

            private void Set(string command)
            {
                _isSettingText = true;
                SetText(command);
                SetSelection(command.Length);
                _isSettingText = false;
            }

            private void ShowPopup(ref ItemsListContextMenu cm, IEnumerable<string> commands, string searchText = null)
            {
                if (cm == null)
                    cm = new ItemsListContextMenu(180, 220, false);
                else
                    cm.ClearItems();

                // Add items
                ItemsListContextMenu.Item lastItem = null;
                foreach (var command in commands)
                {
                    cm.AddItem(lastItem = new Item
                    {
                        Name = command,
                        Owner = this,
                    });
                    var flags = DebugCommands.GetCommandFlags(command);
                    if (flags.HasFlag(DebugCommands.CommandFlags.Exec))
                        lastItem.TintColor = new Color(0.85f, 0.85f, 1.0f, 1.0f);
                    else if (flags.HasFlag(DebugCommands.CommandFlags.Read) && !flags.HasFlag(DebugCommands.CommandFlags.Write))
                        lastItem.TintColor = new Color(0.85f, 0.85f, 0.85f, 1.0f);
                    lastItem.Focused += item =>
                    {
                        // Set command
                        Set(item.Name);
                    };
                }
                cm.ItemClicked += item =>
                {
                    // Execute command
                    OnKeyDown(KeyboardKeys.Return);
                };

                // Setup popup
                var count = commands.Count();
                var totalHeight = count * lastItem.Height + cm.ItemsPanel.Margin.Height + cm.ItemsPanel.Spacing * (count - 1);
                cm.Height = 220;
                if (cm.Height > totalHeight)
                    cm.Height = totalHeight; // Limit popup height if list is small
                if (searchText != null)
                {
                    cm.SortItems();
                    cm.Search(searchText);
                    cm.UseVisibilityControl = false;
                    cm.UseInput = false;
                }

                // Show popup
                cm.Show(this, Float2.Zero, ContextMenuDirection.RightUp);
                cm.ScrollViewTo(lastItem);
                if (searchText != null)
                {
                    RootWindow.Window.LostFocus += OnRootWindowLostFocus;
                }
                else
                {
                    lastItem.Focus();
                }
            }

            private void OnRootWindowLostFocus()
            {
                // Prevent popup from staying active when editor window looses focus
                _searchPopup?.Hide();
                if (RootWindow?.Window != null)
                    RootWindow.Window.LostFocus -= OnRootWindowLostFocus;
            }

            /// <inheritdoc />
            public override void OnGotFocus()
            {
                // Precache debug commands to reduce time-to-interactive
                DebugCommands.InitAsync();

                base.OnGotFocus();
            }

            /// <inheritdoc />
            protected override void OnTextChanged()
            {
                base.OnTextChanged();

                // Skip when editing text from code
                if (_isSettingText)
                    return;

                // Show commands search popup based on current text input
                var text = Text.Trim();
                if (text.Length != 0)
                {
                    DebugCommands.Search(text, out var matches);
                    if (matches.Length != 0)
                    {
                        ShowPopup(ref _searchPopup, matches, text);
                        return;
                    }
                }
                _searchPopup?.Hide();
            }

            /// <inheritdoc />
            public override bool OnKeyDown(KeyboardKeys key)
            {
                switch (key)
                {
                case KeyboardKeys.Return:
                {
                    // Run command
                    _searchPopup?.Hide();
                    var command = Text.Trim();
                    if (command.Length == 0)
                        return true;
                    DebugCommands.Execute(command);
                    SetText(string.Empty);

                    // Update history buffer
                    if (_window._commandHistory == null)
                        _window._commandHistory = new List<string>();
                    else if (_window._commandHistory.Count != 0 && _window._commandHistory.Last() == command)
                        _window._commandHistory.RemoveAt(_window._commandHistory.Count - 1);
                    _window._commandHistory.Add(command);
                    if (_window._commandHistory.Count > CommandHistoryLimit)
                        _window._commandHistory.RemoveAt(0);
                    _window.SaveHistory();

                    return true;
                }
                case KeyboardKeys.Tab:
                {
                    // Auto-complete
                    DebugCommands.Search(Text, out var matches, true);
                    if (matches.Length == 0)
                    {
                        // Nothing found
                    }
                    else if (matches.Length == 1)
                    {
                        // Exact match
                        Set(matches[0]);
                    }
                    else
                    {
                        // Find the most common part
                        Array.Sort(matches);
                        int minLength = Text.Length;
                        int maxLength = matches[0].Length;
                        int sharedLength = minLength + 1;
                        bool allMatch = true;
                        for (; allMatch && sharedLength < maxLength; sharedLength++)
                        {
                            var shared = matches[0].Substring(0, sharedLength);
                            for (int i = 1; i < matches.Length; i++)
                            {
                                if (!matches[i].StartsWith(shared, StringComparison.OrdinalIgnoreCase))
                                {
                                    sharedLength -= 2;
                                    allMatch = false;
                                    break;
                                }
                            }
                        }
                        if (sharedLength > minLength)
                        {
                            // Use the largest shared part of all matches
                            Set(matches[0].Substring(0, sharedLength));
                        }
                    }
                    return true;
                }
                case KeyboardKeys.ArrowUp:
                {
                    if (_searchPopup != null && _searchPopup.Visible)
                    {
                        // Route navigation to active popup
                        var focusedItem = _searchPopup.RootWindow.FocusedControl as Item;
                        if (focusedItem == null)
                            _searchPopup.SelectItem((Item)_searchPopup.ItemsPanel.Children.Last());
                        else
                            _searchPopup.OnKeyDown(key);
                    }
                    else if (TextLength == 0)
                    {
                        if (_window._commandHistory != null && _window._commandHistory.Count != 0)
                        {
                            // Show command history popup
                            _searchPopup?.Hide();
                            ItemsListContextMenu cm = null;
                            ShowPopup(ref cm, _window._commandHistory);
                        }
                    }
                    return true;
                }
                case KeyboardKeys.ArrowDown:
                {
                    if (_searchPopup != null && _searchPopup.Visible)
                    {
                        // Route navigation to active popup
                        var focusedItem = _searchPopup.RootWindow.FocusedControl as Item;
                        if (focusedItem == null)
                            _searchPopup.SelectItem((Item)_searchPopup.ItemsPanel.Children.First());
                        else
                            _searchPopup.OnKeyDown(key);
                    }
                    return true;
                }
                }

                return base.OnKeyDown(key);
            }

            /// <inheritdoc />
            public override void OnDestroy()
            {
                _searchPopup?.Dispose();
                _searchPopup = null;

                base.OnDestroy();
            }
        }

        private InterfaceOptions.TimestampsFormats _timestampsFormats;
        private bool _showLogType;

        private List<Entry> _entries = new List<Entry>(1024);
        private bool _isDirty;
        private int _logTypeShowMask = (int)LogType.Info | (int)LogType.Warning | (int)LogType.Error | (int)LogType.Fatal;
        private float _scrollSize = 18.0f;
        private const int OutCapacity = 64;
        private string[] _outMessages = new string[OutCapacity];
        private byte[] _outLogTypes = new byte[OutCapacity];
        private long[] _outLogTimes = new long[OutCapacity];
        private int _textBufferCount;
        private StringBuilder _textBuffer = new StringBuilder();
        private List<TextBlock> _textBlocks = new List<TextBlock>();
        private DateTime _startupTime;
        private Regex _compileRegex = new Regex("(?<path>^(?:[a-zA-Z]\\:|\\\\\\\\[ \\-\\.\\w\\.]+\\\\[ \\-\\.\\w.$]+)\\\\(?:[ \\-\\.\\w]+\\\\)*\\w([ \\w.])+)\\((?<line>\\d{1,}),\\d{1,},\\d{1,},\\d{1,}\\): (?<level>error|warning) (?<message>.*)", RegexOptions.Compiled);
        private List<string> _commandHistory;
        private const string CommandHistoryKey = "CommandHistory";
        private const int CommandHistoryLimit = 30;

        private Button _viewDropdown;
        private TextBox _searchBox;
        private HScrollBar _hScroll;
        private VScrollBar _vScroll;
        private OutputTextBox _output;
        private CommandLineBox _commandLineBox;
        private ContextMenu _contextMenu;

        /// <summary>
        /// Initializes a new instance of the <see cref="DebugLogWindow"/> class.
        /// </summary>
        /// <param name="editor">The editor.</param>
        public OutputLogWindow(Editor editor)
        : base(editor, true, ScrollBars.None)
        {
            Title = "Output Log";
            Icon = editor.Icons.Info64;
            ClipChildren = false;
            FlaxEditor.Utilities.Utils.SetupCommonInputActions(this);

            // Setup UI
            _viewDropdown = new Button(2, 2, 40.0f, TextBoxBase.DefaultHeight)
            {
                TooltipText = "Change output log view options",
                Text = "View",
                Parent = this,
            };
            _viewDropdown.Clicked += OnViewButtonClicked;
            _searchBox = new SearchBox(false, _viewDropdown.Right + 2, 2, Width - _viewDropdown.Right - 4)
            {
                Parent = this,
            };
            _searchBox.TextChanged += Refresh;
            _hScroll = new HScrollBar(this, Height - _scrollSize - TextBox.DefaultHeight - 2, Width - _scrollSize, _scrollSize)
            {
                ThumbThickness = 10,
                Maximum = 0,
            };
            _hScroll.ValueChanged += OnHScrollValueChanged;
            _vScroll = new VScrollBar(this, Width - _scrollSize, Height - _viewDropdown.Height - 4 - TextBox.DefaultHeight, _scrollSize)
            {
                ThumbThickness = 10,
                Maximum = 0,
            };
            _vScroll.Y += _viewDropdown.Height + 2;
            _vScroll.ValueChanged += OnVScrollValueChanged;
            _output = new OutputTextBox
            {
                Window = this,
                IsReadOnly = true,
                IsMultiline = true,
                BackgroundSelectedFlashSpeed = 0.0f,
                Location = new Float2(2, _viewDropdown.Bottom + 2),
                Parent = this,
            };
            _output.TargetViewOffsetChanged += OnOutputTargetViewOffsetChanged;
            _output.TextChanged += OnOutputTextChanged;
            _commandLineBox = new CommandLineBox(2, Height - 2 - TextBox.DefaultHeight, Width - 4, this)
            {
                Parent = this,
            };

            // Setup context menu
            _contextMenu = new ContextMenu();
            _contextMenu.AddButton("Clear log", Clear);
            _contextMenu.AddButton("Copy selection", _output.Copy);
            _contextMenu.AddButton("Select All", _output.SelectAll);
            _contextMenu.AddButton(Utilities.Constants.ShowInExplorer, () => FileSystem.ShowFileExplorer(Path.Combine(Globals.ProjectFolder, "Logs")));
            _contextMenu.AddButton("Scroll to bottom", () => { _vScroll.TargetValue = _vScroll.Maximum; }).Icon = Editor.Icons.ArrowDown12;

            // Setup editor options
            Editor.Options.OptionsChanged += OnEditorOptionsChanged;
            OnEditorOptionsChanged(Editor.Options.Options);

            InputActions.Add(options => options.Search, _searchBox.Focus);

            GameCooker.Event += OnGameCookerEvent;
            ScriptsBuilder.CompilationFailed += OnScriptsCompilationFailed;
        }

        private void OnViewButtonClicked()
        {
            var menu = new ContextMenu();

            var infoLogButton = menu.AddButton("Info");
            infoLogButton.AutoCheck = true;
            infoLogButton.Checked = (_logTypeShowMask & (int)LogType.Info) != 0;
            infoLogButton.Clicked += () => ToggleLogTypeShow(LogType.Info);

            var warningLogButton = menu.AddButton("Warning");
            warningLogButton.AutoCheck = true;
            warningLogButton.Checked = (_logTypeShowMask & (int)LogType.Warning) != 0;
            warningLogButton.Clicked += () => ToggleLogTypeShow(LogType.Warning);

            var errorLogButton = menu.AddButton("Error");
            errorLogButton.AutoCheck = true;
            errorLogButton.Checked = (_logTypeShowMask & (int)LogType.Error) != 0;
            errorLogButton.Clicked += () => ToggleLogTypeShow(LogType.Error);

            menu.AddSeparator();

            menu.AddButton("Load log file...", LoadLogFile);

            menu.Show(_viewDropdown.Parent, _viewDropdown.BottomLeft);
        }

        private void ToggleLogTypeShow(LogType type)
        {
            _logTypeShowMask ^= (int)type;
            Refresh();
        }

        private void OnHScrollValueChanged()
        {
            var viewOffset = _output.ViewOffset;
            viewOffset.X = _hScroll.Value;
            _output.TargetViewOffset = viewOffset;
        }

        private void OnVScrollValueChanged()
        {
            var viewOffset = _output.ViewOffset;
            viewOffset.Y = _vScroll.Value;
            _output.TargetViewOffset = viewOffset;
        }

        private void OnOutputTargetViewOffsetChanged()
        {
            if (!_hScroll.IsThumbClicked)
                _hScroll.TargetValue = _output.TargetViewOffset.X;
            if (!_vScroll.IsThumbClicked)
                _vScroll.TargetValue = _output.TargetViewOffset.Y;
        }

        private void OnOutputTextChanged()
        {
            if (IsLayoutLocked || _output == null)
                return;

            _hScroll.Maximum = Mathf.Max(_output.TextSize.X, _hScroll.Minimum);
            _vScroll.Maximum = Mathf.Max(_output.TextSize.Y - _output.Height, _vScroll.Minimum);
        }

        private void OnEditorOptionsChanged(EditorOptions options)
        {
            if (options.Interface.OutputLogTimestampsFormat == _timestampsFormats &&
                options.Interface.OutputLogShowLogType == _showLogType &&
                _output.DefaultStyle.Font == options.Interface.OutputLogTextFont &&
                _output.DefaultStyle.Color == options.Visual.LogInfoColor &&
                _output.DefaultStyle.ShadowColor == options.Interface.OutputLogTextShadowColor &&
                _output.DefaultStyle.ShadowOffset == options.Interface.OutputLogTextShadowOffset &&
                _output.WarningStyle.Color == options.Visual.LogWarningColor &&
                _output.ErrorStyle.Color == options.Visual.LogErrorColor)
                return;

            _output.DefaultStyle = new TextBlockStyle
            {
                Font = options.Interface.OutputLogTextFont,
                Color = options.Visual.LogInfoColor,
                ShadowColor = options.Interface.OutputLogTextShadowColor,
                ShadowOffset = options.Interface.OutputLogTextShadowOffset,
                BackgroundSelectedBrush = new SolidColorBrush(Style.Current.BackgroundSelected),
            };

            _output.WarningStyle = _output.DefaultStyle;
            _output.WarningStyle.Color = options.Visual.LogWarningColor;
            _output.ErrorStyle = _output.DefaultStyle;
            _output.ErrorStyle.Color = options.Visual.LogErrorColor;

            _timestampsFormats = options.Interface.OutputLogTimestampsFormat;
            _showLogType = options.Interface.OutputLogShowLogType;

            Refresh();
        }

        private void OnGameCookerEvent(GameCooker.EventType eventType)
        {
            if (eventType == GameCooker.EventType.BuildFailed && !Editor.IsHeadlessMode && Editor.Options.Options.Interface.FocusOutputLogOnGameBuildError)
                FocusOrShow();
        }

        private void OnScriptsCompilationFailed()
        {
            if (!Editor.IsHeadlessMode && Editor.Options.Options.Interface.FocusOutputLogOnCompilationError)
                FocusOrShow();
        }

        private void SaveHistory()
        {
            if (_commandHistory == null || _commandHistory.Count == 0)
                Editor.ProjectCache.RemoveCustomData(CommandHistoryKey);
            else
                Editor.ProjectCache.SetCustomData(CommandHistoryKey, FlaxEngine.Json.JsonSerializer.Serialize(_commandHistory));
        }

        /// <summary>
        /// Refreshes the log output.
        /// </summary>
        private void Refresh()
        {
            _textBufferCount = 0;
            _textBuffer.Clear();
            _textBlocks.Clear();
            _isDirty = true;
        }

        /// <summary>
        /// Clears the log.
        /// </summary>
        public void Clear()
        {
            _entries?.Clear();
            Refresh();
        }

        /// <summary>
        /// Loads the log from the file selected by the user with the file pickup dialog.
        /// </summary>
        public void LoadLogFile()
        {
            if (FileSystem.ShowOpenFileDialog(null, Path.Combine(Globals.ProjectFolder, "Logs"), null, false, "Pick a log file to load", out var files))
                return;
            if (files != null && files.Length > 0)
            {
                LoadLogFile(files[0]);
            }
        }

        /// <summary>
        /// Loads the log file.
        /// </summary>
        /// <param name="path">The path.</param>
        public void LoadLogFile(string path)
        {
            using (var file = File.OpenRead(path))
            using (var stream = new StreamReader(file))
            {
                _entries.Clear();
                var regex = new Regex(@"\[ (\d\d:\d\d:\d\d.\d\d\d) \]\: \[(\w*)\]");

                while (!stream.EndOfStream)
                {
                    // Read next line
                    var line = stream.ReadLine();
                    if (string.IsNullOrEmpty(line))
                        continue;

                    // Parse with regex
                    var match = regex.Match(line);
                    if (!match.Success || match.Groups.Count != 3)
                    {
                        // Try to add the line for multi-line logs
                        if (_entries.Count != 0 && !line.StartsWith("======"))
                        {
                            ref var last = ref CollectionsMarshal.AsSpan(_entries)[_entries.Count - 1];
                            last.Message += '\n';
                            last.Message += line;
                        }

                        continue;
                    }

                    // Parse log time and type
                    var time = match.Groups[1].Value;
                    var level = match.Groups[2].Value;
                    if (time.Length != 12)
                        continue;
                    int hours = int.Parse(time.Substring(0, 2));
                    int minutes = int.Parse(time.Substring(3, 2));
                    int seconds = int.Parse(time.Substring(6, 2));
                    int milliseconds = int.Parse(time.Substring(9, 3));
                    var timeSinceStartup = new TimeSpan(0, hours, minutes, seconds, milliseconds);
                    var logType = (LogType)Enum.Parse(typeof(LogType), level);

                    // Add new entry
                    var e = new Entry
                    {
                        Time = _startupTime + timeSinceStartup,
                        Level = logType,
                        Message = line.Substring(match.Index + match.Length)
                    };
                    _entries.Add(e);
                }

                Refresh();
            }
        }

        /// <inheritdoc />
        protected override void PerformLayoutBeforeChildren()
        {
            base.PerformLayoutBeforeChildren();

            if (_output != null)
            {
                _searchBox.Width = Width - _viewDropdown.Right - 4;
                _output.Size = new Float2(_vScroll.X - 2, _hScroll.Y - 4 - _viewDropdown.Bottom);
                _commandLineBox.Width = Width - 4;
                _commandLineBox.Y = Height - 2 - _commandLineBox.Height;
            }
        }

        /// <inheritdoc/>
        public override void Draw()
        {
            base.Draw();

            bool showHint = (((int)LogType.Info & _logTypeShowMask) == 0 &&
                            ((int)LogType.Warning & _logTypeShowMask) == 0 &&
                            ((int)LogType.Error & _logTypeShowMask) == 0) ||
                            string.IsNullOrEmpty(_output.Text) ||
                            _entries.Count == 0;
            if (showHint)
            {
                var textRect = _output.Bounds;
                var style = Style.Current;
                var text = "No log level filter active or no entries that apply to the current filter exist";
                if (_entries.Count == 0)
                    text = "No log";
                Render2D.DrawText(style.FontMedium, text, textRect, style.ForegroundGrey, TextAlignment.Center, TextAlignment.Center, TextWrapping.WrapWords);
            }
        }

        /// <inheritdoc />
        public override bool OnKeyDown(KeyboardKeys key)
        {
            var input = Editor.Options.Options.Input;
            if (input.Search.Process(this, key))
            {
                if (!_searchBox.ContainsFocus)
                {
                    _searchBox.Focus();
                    _searchBox.SelectAll();
                }
                return true;
            }

            return base.OnKeyDown(key);
        }

        /// <inheritdoc />
        public override bool OnMouseUp(Float2 location, MouseButton button)
        {
            if (base.OnMouseUp(location, button))
                return true;

            if (button == MouseButton.Right)
            {
                _contextMenu.Show(this, location);
                return true;
            }

            return false;
        }

        /// <inheritdoc />
        protected override void OnSizeChanged()
        {
            base.OnSizeChanged();

            // Update scroll range
            OnOutputTextChanged();
        }

        /// <summary>
        /// Focus the debug command line and ensure that the output log window is visible.
        /// </summary>
        public void FocusCommand()
        {
            FocusOrShow();
            _commandLineBox.Focus();
        }

        /// <inheritdoc />
        public override void Update(float deltaTime)
        {
            FlaxEngine.Profiler.BeginEvent("OutputLogWindow.Update");

            // Read the incoming log messages
            int logCount;
            do
            {
                logCount = Editor.Internal_ReadOutputLogs(ref _outMessages, ref _outLogTypes, ref _outLogTimes, OutCapacity);

                for (int i = 0; i < logCount; i++)
                {
                    var entry = new Entry
                    {
                        Level = (LogType)_outLogTypes[i],
                        Time = new DateTime(_outLogTimes[i], DateTimeKind.Utc),
                        Message = _outMessages[i],
                    };
                    _entries.Add(entry);
                    _outMessages[i] = null;
                    _isDirty = true;
                }
            } while (logCount != 0);

            if (_isDirty)
            {
                _isDirty = false;
                var wasEmpty = _output.TextLength == 0;

                // Cache fonts
                _output.DefaultStyle.Font.GetFont();
                _output.WarningStyle.Font.GetFont();
                _output.ErrorStyle.Font.GetFont();

                // Generate the output log
                Span<Entry> entries = CollectionsMarshal.AsSpan(_entries);
                var searchQuery = _searchBox.Text;
                for (int i = _textBufferCount; i < _entries.Count; i++)
                {
                    ref var entry = ref entries[i];
                    if (((int)entry.Level & _logTypeShowMask) == 0)
                        continue;
                    if (searchQuery.Length != 0 && entry.Message.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) == -1)
                        continue;

                    var startIndex = _textBuffer.Length;
                    switch (_timestampsFormats)
                    {
                    case InterfaceOptions.TimestampsFormats.Utc:
                        _textBuffer.AppendFormat("[ {0} ]: ", entry.Time.ToUniversalTime());
                        break;
                    case InterfaceOptions.TimestampsFormats.LocalTime:
                        _textBuffer.AppendFormat("[ {0} ]: ", entry.Time);
                        break;
                    case InterfaceOptions.TimestampsFormats.TimeSinceStartup:
                        var diff = entry.Time - _startupTime;
                        _textBuffer.AppendFormat("[ {0:00}:{1:00}:{2:00}.{3:000} ]: ", diff.Hours, diff.Minutes, diff.Seconds, diff.Milliseconds);
                        break;
                    }
                    if (_showLogType)
                    {
                        _textBuffer.AppendFormat("[{0}] ", entry.Level);
                    }

                    var prefixLength = _textBuffer.Length - startIndex;
                    if (entry.Message.IndexOf('\r') != -1)
                        entry.Message = entry.Message.Replace("\r", "");
                    _textBuffer.Append(entry.Message);
                    var endIndex = _textBuffer.Length;
                    _textBuffer.Append('\n');

                    var textBlock = new TextBlock
                    {
                        Range = new TextRange
                        {
                            StartIndex = startIndex,
                            EndIndex = endIndex - 1,
                        },
                    };

                    switch (entry.Level)
                    {
                    case LogType.Info:
                        textBlock.Style = _output.DefaultStyle;
                        break;
                    case LogType.Warning:
                        textBlock.Style = _output.WarningStyle;
                        break;
                    case LogType.Error:
                    case LogType.Fatal:
                        textBlock.Style = _output.ErrorStyle;
                        break;
                    default: throw new ArgumentOutOfRangeException();
                    }
                    var prevBlockBottom = _textBlocks.Count == 0 ? 0.0f : _textBlocks[_textBlocks.Count - 1].Bounds.Bottom;
                    var entryText = _textBuffer.ToString(startIndex, endIndex - startIndex);
                    var font = textBlock.Style.Font.GetFont();
                    if (!font)
                        continue;
                    var style = textBlock.Style;
                    var lines = font.ProcessText(entryText);
                    for (int j = 0; j < lines.Length; j++)
                    {
                        ref var line = ref lines[j];
                        textBlock.Range.StartIndex = startIndex + line.FirstCharIndex;
                        textBlock.Range.EndIndex = startIndex + line.LastCharIndex + 1;
                        textBlock.Bounds = new Rectangle(new Float2(0.0f, prevBlockBottom), line.Size);

                        if (textBlock.Range.Length > 0)
                        {
                            // Parse compilation error/warning
                            var regexStart = line.FirstCharIndex;
                            if (j == 0)
                                regexStart += prefixLength;
                            var regexLength = line.LastCharIndex + 1 - regexStart;
                            if (regexLength > 0)
                            {
                                var match = _compileRegex.Match(entryText, regexStart, regexLength);
                                if (match.Success)
                                {
                                    switch (match.Groups["level"].Value)
                                    {
                                    case "error":
                                        textBlock.Style = _output.ErrorStyle;
                                        break;
                                    case "warning":
                                        textBlock.Style = _output.WarningStyle;
                                        break;
                                    }
                                    textBlock.Tag = new TextBlockTag
                                    {
                                        Type = TextBlockTag.Types.CodeLocation,
                                        Url = match.Groups["path"].Value,
                                        Line = int.Parse(match.Groups["line"].Value),
                                    };
                                }
                                // TODO: parsing hyperlinks with link
                                // TODO: parsing file paths with link
                            }
                        }

                        prevBlockBottom += line.Size.Y;
                        _textBlocks.Add(textBlock);
                        textBlock.Style = style;
                    }
                }

                // Update the output
                var cachedScrollValue = _vScroll.Value;
                var cachedSelection = _output.SelectionRange;
                var cachedOutputTargetViewOffset = _output.TargetViewOffset;
                var isBottomScroll = _vScroll.Value >= _vScroll.Maximum - (_scrollSize * 2) || wasEmpty;
                _output.Text = _textBuffer.ToString();
                if (_hScroll.Maximum <= 0.0)
                    cachedOutputTargetViewOffset.X = 0;
                if (_vScroll.Maximum <= 0.0)
                    cachedOutputTargetViewOffset.Y = 0;
                _output.TargetViewOffset = cachedOutputTargetViewOffset;
                _textBufferCount = _entries.Count;
                if (!_vScroll.IsThumbClicked)
                    _vScroll.TargetValue = isBottomScroll ? _vScroll.Maximum : cachedScrollValue;
                _output.SelectionRange = cachedSelection;
            }

            base.Update(deltaTime);

            FlaxEngine.Profiler.EndEvent();
        }

        /// <inheritdoc />
        public override void OnInit()
        {
            _startupTime = Time.StartupTime;

            // Load debug commands history
            if (Editor.ProjectCache.TryGetCustomData(CommandHistoryKey, out string history))
            {
                try
                {
                    _commandHistory = (List<string>)FlaxEngine.Json.JsonSerializer.Deserialize(history, typeof(List<string>));
                    for (int i = _commandHistory.Count - 1; i >= 0; i--)
                    {
                        if (string.IsNullOrEmpty(_commandHistory[i]))
                            _commandHistory.RemoveAt(i);
                    }
                }
                catch
                {
                    // Ignore errors
                    _commandHistory = null;
                }
            }
        }

        /// <inheritdoc />
        public override bool UseLayoutData => true;

        /// <inheritdoc />
        public override void OnLayoutSerialize(XmlWriter writer)
        {
            writer.WriteAttributeString("LogTypeShowMask", _logTypeShowMask.ToString());
        }

        /// <inheritdoc />
        public override void OnLayoutDeserialize(XmlElement node)
        {
            if (int.TryParse(node.GetAttribute("LogTypeShowMask"), out int value1))
                _logTypeShowMask = value1;
        }

        /// <inheritdoc />
        public override void OnDestroy()
        {
            if (IsDisposing)
                return;

            // Unbind events
            Editor.Options.OptionsChanged -= OnEditorOptionsChanged;
            GameCooker.Event -= OnGameCookerEvent;
            ScriptsBuilder.CompilationFailed -= OnScriptsCompilationFailed;

            // Cleanup
            _textBuffer.Clear();
            _textBuffer = null;
            _textBlocks.Clear();
            _textBlocks = null;
            _entries.Clear();
            _entries = null;
            _outMessages = null;
            _outLogTypes = null;
            _outLogTimes = null;
            _compileRegex = null;
            _commandHistory = null;

            // Unlink controls
            _viewDropdown = null;
            _searchBox = null;
            _hScroll = null;
            _vScroll = null;
            _output = null;
            _commandLineBox = null;
            _contextMenu = null;

            base.OnDestroy();
        }
    }
}
