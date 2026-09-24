using System.Globalization;

using GTDataSQLiteConverter;
using GTDataSQLiteConverter.Entities;
using GTDataSQLiteConverter.ParamDb;

namespace GTParamDBEditor.UI;

/// <summary>Asks for a new column's name, type and offset, and shows what that does to the rows.</summary>
public sealed class AddColumnDialog : Form
{
    private sealed record TypeChoice(DBColumnType Type, string Text)
    {
        public override string ToString() => Text;
    }

    private static readonly TypeChoice[] Choices =
    {
        new(DBColumnType.Byte, "byte (1 byte)"),
        new(DBColumnType.Short, "short (2 bytes)"),
        new(DBColumnType.Int, "int (4 bytes)"),
        new(DBColumnType.Float, "float (4 bytes)"),
        new(DBColumnType.Int64, "int64 (8 bytes)"),
        new(DBColumnType.Double, "double (8 bytes)"),
        new(DBColumnType.Id, "id (8 bytes, a row label)"),
        new(DBColumnType.String, "string (2 bytes, text in paramstr)"),
        new(DBColumnType.Unicode, "unicode (2 bytes, text in paramunistr)"),
    };

    private readonly LiveTable _table;
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _type = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _offset = new() { Width = 100 };
    private readonly Label _free = new() { ForeColor = SystemColors.GrayText };
    private readonly Label _effect = new();
    private readonly Label _problem = new() { ForeColor = Color.FromArgb(170, 40, 40) };
    private readonly Button _add = new() { Text = "Add", DialogResult = DialogResult.OK, Width = 90, FlatStyle = FlatStyle.System };

    // The name and offset follow the type until they are typed into.
    private bool _nameTyped;
    private bool _offsetTyped;
    private bool _updating;

    public AddColumnDialog(LiveTable table)
    {
        _table = table;

        Text = $"Add column to {table.Name}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(500, 260);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12, 12, 12, 0) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddField(fields, "Name", _name);
        AddField(fields, "Type", _type);
        AddField(fields, "Offset (hex)", _offset);
        AddNote(fields, _free);
        AddNote(fields, _effect);
        AddNote(fields, _problem);

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, FlatStyle = FlatStyle.System };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(8) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_add);

        Controls.Add(fields);
        Controls.Add(buttons);

        AcceptButton = _add;
        CancelButton = cancel;

        string free = string.Join(", ", table.FreeRanges().Select(r => r.Length == 1
            ? $"0x{r.Offset:X}"
            : $"0x{r.Offset:X}-0x{r.Offset + r.Length - 1:X}"));

        _free.Text = $"Rows are {table.RowStride} bytes (0x{table.RowStride:X}). " + (free.Length > 0
            ? $"No column uses {free}. A new column can go there, or anywhere from 0x{table.RowStride:X} on."
            : $"Every byte belongs to a column, so a new one goes from 0x{table.RowStride:X} on.");

        _type.Items.AddRange(Choices);
        _type.SelectedIndex = 0;
        SetOffset(table.SuggestOffset(ColumnType));

        _type.SelectedIndexChanged += (_, _) =>
        {
            if (!_offsetTyped)
                SetOffset(_table.SuggestOffset(ColumnType));
            else
                UpdateState();
        };

        _offset.TextChanged += (_, _) =>
        {
            if (_updating)
                return;

            _offsetTyped = true;
            if (!_nameTyped && TryParseOffset(out int offset))
                SetName($"Unk0x{offset:X}");

            UpdateState();
        };

        _name.TextChanged += (_, _) =>
        {
            if (_updating)
                return;

            _nameTyped = true;
            UpdateState();
        };

        Shown += (_, _) =>
        {
            _name.Focus();
            _name.SelectAll();
        };
    }

    public string ColumnName => _name.Text.Trim();

    public DBColumnType ColumnType => ((TypeChoice)_type.SelectedItem!).Type;

    public int ColumnOffset => TryParseOffset(out int offset) ? offset : -1;

    private static void AddField(TableLayoutPanel fields, string caption, Control input)
    {
        int row = fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowCount = row + 1;
        fields.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) }, 0, row);

        input.Margin = new Padding(0, 3, 0, 3);
        fields.Controls.Add(input, 1, row);
    }

    private static void AddNote(TableLayoutPanel fields, Label note)
    {
        int row = fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowCount = row + 1;
        note.AutoSize = true;
        note.MaximumSize = new Size(470, 0);
        note.Margin = new Padding(0, 8, 0, 0);

        fields.Controls.Add(note, 0, row);
        fields.SetColumnSpan(note, 2);
    }

    private void SetOffset(int offset)
    {
        _updating = true;
        _offset.Text = offset.ToString("X", CultureInfo.InvariantCulture);
        _updating = false;

        if (!_nameTyped)
            SetName($"Unk0x{offset:X}");

        UpdateState();
    }

    private void SetName(string name)
    {
        _updating = true;
        _name.Text = name;
        _updating = false;
    }

    private bool TryParseOffset(out int offset)
    {
        string text = _offset.Text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];

        return int.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out offset) && offset >= 0;
    }

    private void UpdateState()
    {
        string? problem = TryParseOffset(out int offset)
            ? _table.CheckNewColumn(ColumnName, ColumnType, offset)
            : "The offset is a hex number, like 30 or 0x30.";

        _effect.Text = "";
        if (problem is null)
        {
            int size = DBUtils.TypeToSize(ColumnType);
            int rowSize = _table.RowSizeWith(offset, ColumnType);
            string bytes = size == 1 ? $"byte 0x{offset:X}" : $"bytes 0x{offset:X}-0x{offset + size - 1:X}";

            _effect.Text = rowSize > _table.RowStride
                ? $"Uses {bytes}. Rows grow from {_table.RowStride} to {rowSize} bytes when you save."
                : $"Uses {bytes}. Rows stay {_table.RowStride} bytes.";
        }

        _problem.Text = problem ?? "";
        _add.Enabled = problem is null;
    }
}
