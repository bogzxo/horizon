using BorderStyle = ScintillaNET.BorderStyle;

namespace Horizon.HIDL.Editor
{
    partial class ProgramRunDialog
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
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
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            tb = new ScintillaNET.Scintilla();
            btnDismiss = new System.Windows.Forms.Button();
            lblStatus = new System.Windows.Forms.Label();
            lblResult = new System.Windows.Forms.Label();
            pbIcon = new System.Windows.Forms.PictureBox();
            ((System.ComponentModel.ISupportInitialize)pbIcon).BeginInit();
            SuspendLayout();
            // 
            // tb
            // 
            tb.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            tb.AutocompleteListSelectedBackColor = System.Drawing.Color.FromArgb(0, 120, 215);
            tb.LexerName = null;
            tb.Location = new System.Drawing.Point(12, 52);
            tb.Name = "tb";
            tb.ScrollWidth = 49;
            tb.Size = new System.Drawing.Size(572, 321);
            tb.TabIndex = 0;
            tb.BorderStyle = BorderStyle.None;
            // 
            // btnDismiss
            // 
            btnDismiss.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right;
            btnDismiss.BackColor = System.Drawing.Color.FromArgb(62, 62, 66);
            btnDismiss.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(45, 45, 48);
            btnDismiss.FlatAppearance.MouseDownBackColor = System.Drawing.Color.FromArgb(235, 110, 45); // Using your theme accent
            btnDismiss.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(85, 85, 85);
            btnDismiss.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            btnDismiss.ForeColor = System.Drawing.Color.White;
            btnDismiss.Location = new System.Drawing.Point(509, 379);
            btnDismiss.Name = "btnDismiss";
            btnDismiss.Size = new System.Drawing.Size(75, 26);
            btnDismiss.TabIndex = 1;
            btnDismiss.Text = "Dismiss";
            btnDismiss.UseVisualStyleBackColor = false;
            // 
            // lblStatus
            // 
            lblStatus.AutoSize = true;
            lblStatus.Font = new System.Drawing.Font("Segoe UI Semibold", 9F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
            lblStatus.ForeColor = System.Drawing.Color.FromArgb(241, 241, 241);
            lblStatus.Location = new System.Drawing.Point(12, 9);
            lblStatus.Name = "lblStatus";
            lblStatus.Size = new System.Drawing.Size(113, 15);
            lblStatus.TabIndex = 2;
            lblStatus.Text = "Program Status: Ok";
            // 
            // lblResult
            // 
            lblResult.AutoSize = true;
            lblResult.ForeColor = System.Drawing.Color.FromArgb(200, 200, 200);
            lblResult.Location = new System.Drawing.Point(12, 30);
            lblResult.Name = "lblResult";
            lblResult.Size = new System.Drawing.Size(114, 15);
            lblResult.TabIndex = 3;
            lblResult.Text = "Program Result: null";
            // 
            // pbIcon
            // 
            pbIcon.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            pbIcon.Location = new System.Drawing.Point(552, 12);
            pbIcon.Name = "pbIcon";
            pbIcon.Size = new System.Drawing.Size(32, 32);
            pbIcon.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
            pbIcon.TabIndex = 4;
            pbIcon.TabStop = false;
            // 
            // ProgramRunDialog
            // 
            AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            BackColor = System.Drawing.Color.FromArgb(37, 37, 38);
            ClientSize = new System.Drawing.Size(600, 417);
            Controls.Add(pbIcon);
            Controls.Add(lblResult);
            Controls.Add(lblStatus);
            Controls.Add(btnDismiss);
            Controls.Add(tb);
            Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            ForeColor = System.Drawing.Color.White;
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.SizableToolWindow;
            Name = "ProgramRunDialog";
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            Text = "Program Result";
            ((System.ComponentModel.ISupportInitialize)pbIcon).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private ScintillaNET.Scintilla tb;
        private System.Windows.Forms.Button btnDismiss;
        private System.Windows.Forms.Label lblStatus;
        private System.Windows.Forms.Label lblResult;
        private System.Windows.Forms.PictureBox pbIcon;
    }
}