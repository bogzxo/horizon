using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

using AutocompleteMenuNS;

using Horizon.HIDL.Runtime;
using Microsoft.VisualBasic;

namespace Horizon.HIDL.Editor
{
    public partial class CodeEditorForm : Form
    {
        private readonly System.Windows.Forms.Timer _highlightTimer;
        private string? _currentFilePath;
        private SyntaxHighlighter? _syntaxHighlighter;
        private HidlAutocomplete? _autocompleteHandler;
        private SunOverlayControl _sunOverlay;
        private StringBuilder _programLog = new();
        public HIDLRuntime? Runtime { get; set; }

        public CodeEditorForm()
        {
            InitializeComponent();

            ApplyDarkTheme();

            _autocompleteHandler = new HidlAutocomplete(rtb);

            _sunOverlay = new SunOverlayControl
            {
                Size = new Size(180, 180)
            };
            pnlEditorContainer.Controls.Add(_sunOverlay);
            _sunOverlay.BringToFront();
            UpdateSunLocation();

            _syntaxHighlighter = new SyntaxHighlighter(rtb);
            _syntaxHighlighter.InitializeStyles();

            _highlightTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _highlightTimer.Tick += (s, e) =>
            {
                _highlightTimer.Stop();

                // Postpone highlighting if mouse button is held down (user dragging selection)
                if (MouseButtons != MouseButtons.None)
                {
                    _highlightTimer.Start();
                    return;
                }

                HighlightSyntax();
            };

            rtb.TextChanged += (s, e) =>
            {
                _highlightTimer.Stop();
                _highlightTimer.Start();
            };

            rtb.UpdateUI += (s, e) =>
            {
                UpdateCursorPosition();
            };

            rtb.Resize += (s, e) =>
            {
                UpdateSunLocation();
            };

            pnlEditorContainer.Resize += (s, e) => UpdateSunLocation();

            btnNew.Click += NewFile_Click;
            btnOpen.Click += OpenFile_Click;
            btnSave.Click += SaveFile_Click;
            btnRun.Click += RunScript_Click;

            lblVersion.Text = $"Dawn IDE — HIDL v{HIDLRuntime.VERSION}";
            UpdateCursorPosition();

            Load += (_, _) =>
            {
                UpdateSunLocation();
                _highlightTimer.Stop();
                _highlightTimer.Start();
            };
        }

        private void UpdateSunLocation()
        {
            if (_sunOverlay != null && pnlEditorContainer != null)
            {
                _sunOverlay.Location = new Point(
                    pnlEditorContainer.Width - _sunOverlay.Width,
                    pnlEditorContainer.Height - _sunOverlay.Height);
                _sunOverlay.BringToFront();
            }
        }

        private void ApplyDarkTheme()
        {
            BackColor = Color.FromArgb(30, 30, 30);

            DarkThemeRenderer darkRenderer = new DarkThemeRenderer();
            toolStrip1.Renderer = darkRenderer;
            statusStrip1.Renderer = darkRenderer;

            toolStrip1.BackColor = Color.FromArgb(37, 37, 38);
            toolStrip1.ForeColor = Color.FromArgb(220, 220, 220);

            foreach (ToolStripItem item in toolStrip1.Items)
            {
                item.ForeColor = Color.FromArgb(220, 220, 220);
            }

            statusStrip1.BackColor = Color.FromArgb(215, 85, 25);
            statusStrip1.ForeColor = Color.White;
            lblStatus.ForeColor = Color.White;
            lblLineCol.ForeColor = Color.White;
            lblEncoding.ForeColor = Color.White;
            lblVersion.ForeColor = Color.White;

            pnlEditorContainer.BackColor = Color.FromArgb(30, 30, 30);
        }

        private void UpdateCursorPosition()
        {
            int pos = rtb.CurrentPosition;
            int line = rtb.LineFromPosition(pos);
            int lineStartPos = rtb.Lines[line].Position;
            int col = pos - lineStartPos;

            lblLineCol.Text = $"Ln {line + 1}, Col {col + 1}";
        }

        private void NewFile_Click(object? sender, EventArgs e)
        {
            rtb.Text = string.Empty;
            _currentFilePath = null;
            Text = "Dawn — Untitled";
            lblStatus.Text = "New file created.";
        }

        private void OpenFile_Click(object? sender, EventArgs e)
        {
            using OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "HIDL Source (*.hor)|*.hor|All Files (*.*)|*.*",
                Title = "Open HIDL Source File"
            };

            if (ofd.ShowDialog() == DialogResult.OK)
            {
                rtb.Text = File.ReadAllText(ofd.FileName);
                _currentFilePath = ofd.FileName;
                Text = $"Dawn — {Path.GetFileName(_currentFilePath)}";
                lblStatus.Text = $"Loaded: {_currentFilePath}";
            }
        }

        private void SaveFile_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath))
            {
                SaveAsFile_Click(sender, e);
            }
            else
            {
                File.WriteAllText(_currentFilePath, rtb.Text);
                lblStatus.Text = $"Saved: {_currentFilePath}";
            }
        }

        private void SaveAsFile_Click(object? sender, EventArgs e)
        {
            using SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "HIDL Source (*.hor)|*.hor|All Files (*.*)|*.*",
                Title = "Save HIDL Source File"
            };

            if (sfd.ShowDialog() == DialogResult.OK)
            {
                File.WriteAllText(sfd.FileName, rtb.Text);
                _currentFilePath = sfd.FileName;
                Text = $"Dawn — {Path.GetFileName(_currentFilePath)}";
                lblStatus.Text = $"Saved: {_currentFilePath}";
            }
        }

        private HIDLRuntime GenerateRuntime()
        {
            var runtime = new HIDLRuntime();

            runtime.GlobalScope.DeclareSystem("input", new NativeFunctionValue((args, _) =>
            {
                string title = "Enter text", caption = "";
                if (args.Length > 0)
                    title = args[0].ToString();
                if (args.Length == 2)
                    caption = args[1].ToString();

                var (success, result) = runtime.GenerateValue(Interaction.InputBox(title, caption));
                if (success) return result;

                return new NullValue();
            }));


            runtime.GlobalScope.DeclareSystem("print", new NativeFunctionValue((args, _) =>
            {
                _programLog.AppendLine(string.Join(string.Empty, args));

                return new NullValue();
            }));

            return runtime;
        }

        private void RunScript_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(rtb.Text)) return;
            Runtime ??= GenerateRuntime();

            _programLog.Clear();
            var (success, result) = Runtime.GenerateValue(rtb.Text);
            new ProgramRunDialog(_programLog.ToString(), result.ToString(), success).ShowDialog(this);
        }

        private void HighlightSyntax()
        {
            if (string.IsNullOrEmpty(rtb.Text) || _syntaxHighlighter == null)
            {
                lblStatus.Text = "Ready";
                btnRun.Enabled = true;
                _autocompleteHandler?.UpdateAutocompleteItems(Runtime);
                return;
            }

            Runtime ??= GenerateRuntime();
            var parseError = _syntaxHighlighter.HighlightSyntax(Runtime);

            _autocompleteHandler?.UpdateAutocompleteItems(Runtime);

            btnRun.Enabled = parseError == null;

            if (parseError != null)
            {
                lblStatus.Text = $"Error: {parseError.Message}";
            }
            else
            {
                lblStatus.Text = "Ready";
            }
        }
    }
}