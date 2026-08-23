using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Horizon.HIDL.Runtime;

namespace Horizon.HIDL.Editor
{
    public partial class CodeEditorForm : Form
    {
        private readonly System.Windows.Forms.Timer _highlightTimer;
        private string? _currentFilePath;
        private SyntaxHighlighter? _syntaxHighlighter;
        private SunOverlayControl _sunOverlay;


        public HIDLRuntime? Runtime { get; set; }

        public CodeEditorForm()
        {
            InitializeComponent();

            ApplyDarkTheme();

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

                HighlightSyntax(true);
                _sunOverlay.BringToFront();
            };

            rtb.TextChanged += (s, e) =>
            {
                HighlightSyntax();
                _sunOverlay.BringToFront();
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

            lblVersion.Text = $"Dawn IDE -- HIDL v{HIDLRuntime.VERSION}";
            UpdateCursorPosition();

            Load += (_, _) =>
            {
                rtb.Invalidate();
                rtb.Update();
                UpdateSunLocation();
                _highlightTimer.Stop();
                _highlightTimer.Start();
            };
        }

        private void UpdateSunLocation()
        {
            if (pnlEditorContainer != null)
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
            Text = "Dawn -- Untitled";
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
                Text = $"Dawn -- {Path.GetFileName(_currentFilePath)}";
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
                Text = $"Dawn -- {Path.GetFileName(_currentFilePath)}";
                lblStatus.Text = $"Saved: {_currentFilePath}";
            }
        }

        private void RunScript_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(rtb.Text)) return;
            Runtime ??= new HIDLRuntime();
            var (success, result) = Runtime.Evaluate(rtb.Text);
            if (!string.IsNullOrEmpty(result))
            {
                MessageBox.Show(result, success ? "Execution Result" : "Runtime Error", MessageBoxButtons.OK, success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            }
        }

        private void HighlightSyntax(bool execute=false)
        {
            if (string.IsNullOrEmpty(rtb.Text) || _syntaxHighlighter == null)
            {
                lblStatus.Text = "Ready";
                btnRun.Enabled = true;
                return;
            }

            Runtime ??= new HIDLRuntime();
            var parseError = _syntaxHighlighter.HighlightSyntax(execute ? Runtime : null);

            btnRun.Enabled = parseError == null;

            lblStatus.Text = parseError != null ? $"Error: {parseError.Message}" : "Ready";
        }
    }
}
