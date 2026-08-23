using ScintillaNET;

namespace Horizon.HIDL.Editor
{
    partial class CodeEditorForm
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            rtb = new Scintilla();
            toolStrip1 = new ToolStrip();
            btnNew = new ToolStripButton();
            btnOpen = new ToolStripButton();
            btnSave = new ToolStripButton();
            toolStripSeparator1 = new ToolStripSeparator();
            btnRun = new ToolStripButton();
            statusStrip1 = new StatusStrip();
            lblStatus = new ToolStripStatusLabel();
            lblLineCol = new ToolStripStatusLabel();
            lblEncoding = new ToolStripStatusLabel();
            lblVersion = new ToolStripStatusLabel();
            pnlEditorContainer = new Panel();
            toolStrip1.SuspendLayout();
            statusStrip1.SuspendLayout();
            pnlEditorContainer.SuspendLayout();
            SuspendLayout();
            // 
            // rtb
            // 
            rtb.AutocompleteListSelectedBackColor = Color.FromArgb(0, 120, 215);
            rtb.Dock = DockStyle.Fill;
            rtb.LexerName = null;
            rtb.Location = new Point(0, 0);
            rtb.Name = "rtb";
            rtb.ScrollWidth = 229;
            rtb.Size = new Size(900, 544);
            rtb.TabIndex = 0;
            rtb.Text = "// Welcome to Dawn - HIDL Code Editor";
            // 
            // toolStrip1
            // 
            toolStrip1.GripStyle = ToolStripGripStyle.Hidden;
            toolStrip1.Items.AddRange(new ToolStripItem[] { btnNew, btnOpen, btnSave, toolStripSeparator1, btnRun });
            toolStrip1.Location = new Point(0, 0);
            toolStrip1.Name = "toolStrip1";
            toolStrip1.Size = new Size(900, 25);
            toolStrip1.TabIndex = 3;
            toolStrip1.Text = "toolStrip1";
            // 
            // btnNew
            // 
            btnNew.DisplayStyle = ToolStripItemDisplayStyle.Text;
            btnNew.Name = "btnNew";
            btnNew.Size = new Size(50, 22);
            btnNew.Text = "📄 New";
            // 
            // btnOpen
            // 
            btnOpen.DisplayStyle = ToolStripItemDisplayStyle.Text;
            btnOpen.Name = "btnOpen";
            btnOpen.Size = new Size(55, 22);
            btnOpen.Text = "📂 Open";
            // 
            // btnSave
            // 
            btnSave.DisplayStyle = ToolStripItemDisplayStyle.Text;
            btnSave.Name = "btnSave";
            btnSave.Size = new Size(50, 22);
            btnSave.Text = "💾 Save";
            // 
            // toolStripSeparator1
            // 
            toolStripSeparator1.Name = "toolStripSeparator1";
            toolStripSeparator1.Size = new Size(6, 25);
            // 
            // btnRun
            // 
            btnRun.DisplayStyle = ToolStripItemDisplayStyle.Text;
            btnRun.Name = "btnRun";
            btnRun.Size = new Size(45, 22);
            btnRun.Text = "▶ Run";
            // 
            // statusStrip1
            // 
            statusStrip1.Items.AddRange(new ToolStripItem[] { lblStatus, lblLineCol, lblEncoding, lblVersion });
            statusStrip1.Location = new Point(0, 569);
            statusStrip1.Name = "statusStrip1";
            statusStrip1.Size = new Size(900, 22);
            statusStrip1.TabIndex = 4;
            statusStrip1.Text = "statusStrip1";
            // 
            // lblStatus
            // 
            lblStatus.Name = "lblStatus";
            lblStatus.Size = new Size(725, 17);
            lblStatus.Spring = true;
            lblStatus.Text = "Ready";
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // lblLineCol
            // 
            lblLineCol.Name = "lblLineCol";
            lblLineCol.Size = new Size(62, 17);
            lblLineCol.Text = "Ln 1, Col 1";
            // 
            // lblEncoding
            // 
            lblEncoding.Name = "lblEncoding";
            lblEncoding.Size = new Size(38, 17);
            lblEncoding.Text = "UTF-8";
            // 
            // lblVersion
            // 
            lblVersion.Name = "lblVersion";
            lblVersion.Size = new Size(60, 17);
            lblVersion.Text = "HIDL 0.0.4";
            // 
            // pnlEditorContainer
            // 
            pnlEditorContainer.Controls.Add(rtb);
            pnlEditorContainer.Dock = DockStyle.Fill;
            pnlEditorContainer.Location = new Point(0, 25);
            pnlEditorContainer.Name = "pnlEditorContainer";
            pnlEditorContainer.Size = new Size(900, 544);
            pnlEditorContainer.TabIndex = 2;
            // 
            // CodeEditorForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(900, 591);
            Controls.Add(pnlEditorContainer);
            Controls.Add(toolStrip1);
            Controls.Add(statusStrip1);
            Name = "CodeEditorForm";
            Text = "Dawn - HIDL IDE";
            toolStrip1.ResumeLayout(false);
            toolStrip1.PerformLayout();
            statusStrip1.ResumeLayout(false);
            statusStrip1.PerformLayout();
            pnlEditorContainer.ResumeLayout(false);
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Scintilla rtb;
        private ToolStrip toolStrip1;
        private ToolStripButton btnNew;
        private ToolStripButton btnOpen;
        private ToolStripButton btnSave;
        private ToolStripSeparator toolStripSeparator1;
        private ToolStripButton btnRun;
        private StatusStrip statusStrip1;
        private ToolStripStatusLabel lblStatus;
        private ToolStripStatusLabel lblLineCol;
        private ToolStripStatusLabel lblEncoding;
        private ToolStripStatusLabel lblVersion;
        private Panel pnlEditorContainer;
    }
}
