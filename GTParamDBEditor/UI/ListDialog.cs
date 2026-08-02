namespace GTParamDBEditor.UI;

/// <summary>Shows a list of messages (load warnings, rejected values) in a copyable box.</summary>
public sealed class ListDialog : Form
{
    public ListDialog(string title, string description, IEnumerable<string> lines)
    {
        string[] items = lines.ToArray();

        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(720, 420);
        MinimumSize = new Size(420, 260);
        ShowInTaskbar = false;
        MinimizeBox = false;

        var caption = new Label
        {
            Dock = DockStyle.Top,
            Text = description,
            AutoSize = false,
            Height = 40,
            Padding = new Padding(12, 10, 12, 0),
        };

        var content = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            Text = string.Join(Environment.NewLine, items),
            BackColor = SystemColors.Window,
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(8),
        };

        var close = new Button { Text = "Close", DialogResult = DialogResult.OK, Width = 90, FlatStyle = FlatStyle.System };
        var copy = new Button { Text = "Copy all", Width = 90, FlatStyle = FlatStyle.System, Enabled = items.Length > 0 };
        copy.Click += (_, _) => Clipboard.SetText(content.Text);

        buttons.Controls.Add(close);
        buttons.Controls.Add(copy);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
        body.Controls.Add(content);

        Controls.Add(body);
        Controls.Add(buttons);
        Controls.Add(caption);

        AcceptButton = close;
        CancelButton = close;
    }
}
