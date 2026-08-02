using System.ComponentModel;
using System.Data;

using GTParamDBEditor.Core;

using Microsoft.Data.Sqlite;

namespace GTParamDBEditor.UI;

/// <summary>
/// Direct SQL against the live database. Handy for bulk edits ("give every car 10% more power")
/// that would be tedious cell by cell.
/// </summary>
public sealed class SqlQueryForm : Form
{
    private readonly LiveDatabase _database;
    private readonly TextBox _sql = new();
    private readonly DataGridView _results = new();
    private readonly Label _status = new();

    /// <summary>Raised after a statement that may have changed data.</summary>
    public event EventHandler? DatabaseChanged;

    /// <summary>
    /// Raised just before a statement runs. The host uses it to push edits sitting in the grid into
    /// the database first, so the statement sees them - and so reloading the grid afterwards cannot
    /// throw them away. Cancelling stops the statement.
    /// </summary>
    public event EventHandler<CancelEventArgs>? BeforeExecute;

    public SqlQueryForm(LiveDatabase database)
    {
        _database = database;

        Text = "SQL query";
        Size = new Size(920, 620);
        MinimumSize = new Size(520, 360);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        _sql.Multiline = true;
        _sql.AcceptsTab = true;
        _sql.ScrollBars = ScrollBars.Both;
        _sql.WordWrap = false;
        _sql.Dock = DockStyle.Fill;
        _sql.Font = new Font(FontFamily.GenericMonospace, 9.5f);
        _sql.Text = "SELECT Label, Price FROM CAR ORDER BY Price DESC LIMIT 20;";

        _results.Dock = DockStyle.Fill;
        _results.ReadOnly = true;
        _results.AllowUserToAddRows = false;
        _results.AllowUserToDeleteRows = false;
        _results.RowHeadersVisible = false;
        _results.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;

        var run = new Button { Text = "Run (F5)", Dock = DockStyle.Right, Width = 110, FlatStyle = FlatStyle.System };
        run.Click += (_, _) => Run();

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(0, 6, 0, 6) };
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Text = "Statements run against the live database. Save afterwards to push the changes into the game files.";
        _status.ForeColor = SystemColors.GrayText;
        bar.Controls.Add(_status);
        bar.Controls.Add(run);

        var editor = new Panel { Dock = DockStyle.Fill };
        editor.Controls.Add(_sql);
        editor.Controls.Add(bar);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
        };
        split.Panel1.Controls.Add(editor);
        split.Panel2.Controls.Add(_results);

        Controls.Add(split);
        Padding = new Padding(8);

        Load += (_, _) => split.SplitterDistance = Math.Max(120, split.Height / 3);
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.F5)
                return;

            e.Handled = true;
            Run();
        };
    }

    private void Run()
    {
        string sql = string.IsNullOrWhiteSpace(_sql.SelectedText) ? _sql.Text : _sql.SelectedText;

        if (string.IsNullOrWhiteSpace(sql))
            return;

        var pending = new CancelEventArgs();
        BeforeExecute?.Invoke(this, pending);

        if (pending.Cancel)
        {
            _status.Text = "Edits in the grid could not be applied, so nothing was run.";
            _status.ForeColor = Color.FromArgb(170, 40, 40);
            return;
        }

        try
        {
            Cursor = Cursors.WaitCursor;

            LiveDatabase.SqlResult result = _database.ExecuteSql(sql);

            // Start from nothing each time; otherwise the grid keeps columns from the previous
            // query whose names happen to match.
            _results.DataSource = null;
            _results.Columns.Clear();
            _results.DataSource = result.Rows;
            _status.Text = result.Message;
            _status.ForeColor = SystemColors.ControlText;

            if (result.Rows is null || result.RecordsAffected > 0)
                DatabaseChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException)
        {
            _results.DataSource = null;
            _status.Text = e.Message;
            _status.ForeColor = Color.FromArgb(170, 40, 40);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }
}
