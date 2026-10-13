namespace PerpetuaSync;

/// <summary>
/// Asked the first time the window's X is clicked: keep syncing in the tray (Yes), or exit (No). Closing this
/// dialog itself (Esc, its X) is Cancel: the window stays open and nothing is remembered.
/// </summary>
sealed class ClosePrompt : Form
{
    public ClosePrompt(Icon? icon)
    {
        Text = "Perpetua Sync";
        Icon = icon;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Navy;
        ClientSize = new Size(420, 250);

        var root = new Backdrop { Dock = DockStyle.Fill, Padding = new Padding(26, 22, 26, 20) };
        var col = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        var heading = new WrapLabel { Text = "Keep syncing in the background?", ForeColor = Theme.GoldPale, BackColor = Color.Transparent, Font = Theme.Serif(17f), Margin = new Padding(0, 0, 0, 10), MaximumSize = new Size(368, 0) };
        var text = new WrapLabel
        {
            Text = "Perpetua Sync can stay in the system tray and keep syncing whenever WoW saves. "
                 + "Right-click its tray icon to sync or exit. You can change this later in Settings.",
            ForeColor = Theme.Text, BackColor = Color.Transparent, Font = Theme.Serif(12f), MaximumSize = new Size(368, 0), Margin = new Padding(0, 0, 0, 20),
        };
        var tray = new GoldButton("Keep syncing in the tray", GoldButton.Kinds.Solid) { DialogResult = DialogResult.Yes, Margin = new Padding(0, 0, 10, 0) };
        var exit = new GoldButton("Exit") { DialogResult = DialogResult.No, Margin = Padding.Empty };
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
        row.Controls.AddRange([tray, exit]);
        exit.Height = tray.Height;
        col.Controls.AddRange([heading, text, row]);
        root.Controls.Add(col);
        Controls.Add(root);
        AcceptButton = tray;
        HandleCreated += (_, _) => Theme.DarkTitleBar(this);
    }

    // Esc: neither choice; the window stays open.
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
        return base.ProcessDialogKey(keyData);
    }
}
