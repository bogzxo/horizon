using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Horizon.HIDL.Editor
{
    public partial class ProgramRunDialog : Form
    {
        public ProgramRunDialog(string log, string result, bool success)
        {
            InitializeComponent();


            var syntaxHighlighter = new SyntaxHighlighter(tb);
            syntaxHighlighter.InitializeStyles();

            tb.Text = log;
            lblResult.Text = $"Program Result: {result}";
            lblStatus.Text = $"Program Status: {(success ? "Ok" : "Err" )}";

            pbIcon.Image = success ? SystemIcons.Information.ToBitmap() : SystemIcons.Error.ToBitmap();

            this.btnDismiss.Click += (sender, args) =>
            {
                this.Close();
            };
        }
    }
}
